using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Cicerone.Configuration;
using Jellyfin.Plugin.Cicerone.Core.Audio;
using Jellyfin.Plugin.Cicerone.Core.Sync;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cicerone.Services.Transcription
{
    /// <summary>
    /// Transcribes through Gemini, which takes audio as an ordinary part of a prompt.
    /// </summary>
    /// <remarks>
    /// A different shape of provider from the Whisper family and worth having for
    /// one reason: it is a general model, so the response schema can demand exactly
    /// the fields Cicerone needs and the API will enforce them. There is no
    /// <c>verbose_json</c> to hope for.
    /// <para>
    /// The trade is that timing is being asked of a model that is reading audio
    /// rather than decoding it, and it is less exact than Whisper's. Ask for
    /// segments of a few seconds — a model told to timestamp "each sentence" returns
    /// one span for the whole clip, which is the untimed fallback wearing a
    /// timestamp.
    /// </para>
    /// </remarks>
    public sealed class GoogleTranscriptionProvider : ITranscriptionProvider
    {
        private const string Instruction =
            "Transcribe the speech in this audio clip exactly as spoken, in the original language. "
            + "Do not translate it. Break the transcript into short segments of at most a few seconds "
            + "each, and give the start and end of every segment in seconds from the beginning of the "
            + "clip. Transcribe only speech: ignore music, sound effects and background noise, and "
            + "return no segments at all if nobody speaks.";

        private readonly TranscriptionProfile _profile;
        private readonly HttpClient _client;
        private readonly ILogger _logger;

        /// <summary>Initialises a new instance of the <see cref="GoogleTranscriptionProvider"/> class.</summary>
        /// <param name="profile">The profile to call with.</param>
        /// <param name="client">The HTTP client.</param>
        /// <param name="logger">The logger.</param>
        public GoogleTranscriptionProvider(TranscriptionProfile profile, HttpClient client, ILogger logger)
        {
            _profile = profile;
            _client = client;
            _logger = logger;
        }

        /// <inheritdoc />
        public string ModelId => _profile.Model;

        /// <inheritdoc />
        public async Task<TranscriptionResult> TranscribeAsync(
            byte[] audio,
            ClipFormat format,
            TimeSpan duration,
            string? languageHint,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(audio);

            var baseUrl = string.IsNullOrWhiteSpace(_profile.BaseUrl)
                ? "https://generativelanguage.googleapis.com/v1beta"
                : _profile.BaseUrl.TrimEnd('/');

            var url = string.Create(
                CultureInfo.InvariantCulture,
                $"{baseUrl}/models/{_profile.Model}:generateContent");

            var payload = Payload(audio, format, languageHint);

            using var response = await TransientHttpRetry.SendAsync(
                    _client,
                    () =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Post, url)
                        {
                            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                        };

                        // The header rather than a query parameter, so the key does not
                        // end up in a proxy's access log.
                        request.Headers.Add("x-goog-api-key", _profile.ApiKey);
                        return request;
                    },
                    _logger,
                    cancellationToken)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return TranscriptionResult.Failed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Gemini returned {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}"));
            }

            return Parse(body, duration, languageHint);
        }

        /// <summary>Reads a Gemini response.</summary>
        /// <param name="body">The JSON envelope.</param>
        /// <param name="duration">The clip's length, used to reject impossible times.</param>
        /// <param name="languageHint">What language was asked for, reported back when the model says nothing.</param>
        /// <returns>The transcript.</returns>
        public static TranscriptionResult Parse(string? body, TimeSpan duration, string? languageHint)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return TranscriptionResult.Failed("Gemini returned an empty response");
            }

            string inner;
            try
            {
                using var envelope = JsonDocument.Parse(body);
                if (!envelope.RootElement.TryGetProperty("candidates", out var candidates)
                    || candidates.ValueKind != JsonValueKind.Array
                    || candidates.GetArrayLength() == 0)
                {
                    // A blocked or empty candidate list. The prompt is a clip of a
                    // film the owner already owns, so this is nearly always a safety
                    // filter rather than anything the plugin did.
                    return TranscriptionResult.Failed("Gemini returned no candidates");
                }

                var builder = new StringBuilder();
                if (candidates[0].TryGetProperty("content", out var content)
                    && content.TryGetProperty("parts", out var parts)
                    && parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        {
                            builder.Append(text.GetString());
                        }
                    }
                }

                inner = builder.ToString();
            }
            catch (JsonException ex)
            {
                return TranscriptionResult.Failed("Gemini returned unreadable JSON: " + ex.Message);
            }

            if (inner.Trim().Length == 0)
            {
                return TranscriptionResult.Failed("nothing was said in this window");
            }

            try
            {
                using var document = JsonDocument.Parse(inner);
                if (!document.RootElement.TryGetProperty("segments", out var segments)
                    || segments.ValueKind != JsonValueKind.Array)
                {
                    return TranscriptionResult.Failed("Gemini's answer carried no segments");
                }

                var parsed = new List<TranscriptSegment>(segments.GetArrayLength());
                foreach (var segment in segments.EnumerateArray())
                {
                    var text = segment.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                        ? (t.GetString() ?? string.Empty).Trim()
                        : string.Empty;

                    if (text.Length == 0)
                    {
                        continue;
                    }

                    var start = Number(segment, "start");
                    var end = Number(segment, "end");

                    // A model reporting times can report nonsense ones, and a segment
                    // placed outside the clip it came from would vote for an offset
                    // manufactured entirely by the mistake. Clamping rather than
                    // discarding: the words were still heard somewhere in the window.
                    start = Math.Clamp(start, 0, duration.TotalSeconds);
                    end = Math.Clamp(end, start, duration.TotalSeconds);

                    parsed.Add(new TranscriptSegment(
                        TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text));
                }

                if (parsed.Count == 0)
                {
                    return TranscriptionResult.Failed("nothing was said in this window");
                }

                return new TranscriptionResult(parsed, languageHint, true, null);
            }
            catch (JsonException ex)
            {
                return TranscriptionResult.Failed("Gemini's answer was not the requested shape: " + ex.Message);
            }
        }

        private static string Payload(byte[] audio, ClipFormat format, string? languageHint)
        {
            var instruction = languageHint is null
                ? Instruction
                : Instruction + " The speech is in " + languageHint + ".";

            using var stream = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();

                writer.WriteStartArray("contents");
                writer.WriteStartObject();
                writer.WriteStartArray("parts");

                writer.WriteStartObject();
                writer.WriteString("text", instruction);
                writer.WriteEndObject();

                writer.WriteStartObject();
                writer.WriteStartObject("inline_data");
                writer.WriteString("mime_type", MimeType(format));
                writer.WriteString("data", Convert.ToBase64String(audio));
                writer.WriteEndObject();
                writer.WriteEndObject();

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndArray();

                writer.WriteStartObject("generationConfig");

                // Zero, for the same reason Whisper is sent zero: a model improvising
                // over a difficult clip contributes words that were never spoken, and
                // every one of them votes.
                writer.WriteNumber("temperature", 0);
                writer.WriteString("responseMimeType", "application/json");

                // The schema makes the shape an API guarantee rather than something
                // the prompt asks for and the parser hopes for.
                writer.WriteStartObject("responseSchema");
                writer.WriteString("type", "object");
                writer.WriteStartObject("properties");
                writer.WriteStartObject("segments");
                writer.WriteString("type", "array");
                writer.WriteStartObject("items");
                writer.WriteString("type", "object");
                writer.WriteStartObject("properties");
                WriteField(writer, "start", "number");
                WriteField(writer, "end", "number");
                WriteField(writer, "text", "string");
                writer.WriteEndObject();
                writer.WriteStartArray("required");
                writer.WriteStringValue("start");
                writer.WriteStringValue("end");
                writer.WriteStringValue("text");
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteStartArray("required");
                writer.WriteStringValue("segments");
                writer.WriteEndArray();
                writer.WriteEndObject();

                writer.WriteEndObject();

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void WriteField(Utf8JsonWriter writer, string name, string type)
        {
            writer.WriteStartObject(name);
            writer.WriteString("type", type);
            writer.WriteEndObject();
        }

        private static double Number(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                return 0;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDouble(),

                // A schema-constrained number still arrives as a string from some
                // model versions, and refusing it would throw the segment away.
                JsonValueKind.String => double.TryParse(
                    value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0,
                _ => 0,
            };
        }

        private static string MimeType(ClipFormat format) => format switch
        {
            ClipFormat.Mp3 => "audio/mp3",
            ClipFormat.Wav => "audio/wav",
            _ => "audio/ogg",
        };
    }
}
