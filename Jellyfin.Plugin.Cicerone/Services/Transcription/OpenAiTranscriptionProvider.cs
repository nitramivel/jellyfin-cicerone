using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
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
    /// Talks to OpenAI's audio transcription endpoint, and to everything else that
    /// speaks it.
    /// </summary>
    /// <remarks>
    /// One class for both providers, because the wire format genuinely is the same
    /// one: Speaches, faster-whisper-server, whisper.cpp's server, LM Studio and
    /// Groq all implement <c>POST /audio/transcriptions</c> as OpenAI defined it.
    /// The only differences are the base URL and whether there is a key.
    /// <para>
    /// <b><c>verbose_json</c> is not optional and it is the reason the default model
    /// is <c>whisper-1</c>.</b> Cicerone needs to know when inside the clip each
    /// phrase was said, and that comes back only in the verbose response's
    /// <c>segments</c> array. OpenAI's newer and better transcription models —
    /// <c>gpt-4o-transcribe</c> and its mini — do not support that response format
    /// at all, so they return an excellent transcript with no times in it and are
    /// useless here. The old cheap model is the right one, which is a pleasant place
    /// to end up.
    /// </para>
    /// </remarks>
    public sealed class OpenAiTranscriptionProvider : ITranscriptionProvider
    {
        private readonly TranscriptionProfile _profile;
        private readonly HttpClient _client;
        private readonly ILogger _logger;

        /// <summary>Initialises a new instance of the <see cref="OpenAiTranscriptionProvider"/> class.</summary>
        /// <param name="profile">The profile to call with.</param>
        /// <param name="client">The HTTP client.</param>
        /// <param name="logger">The logger.</param>
        public OpenAiTranscriptionProvider(TranscriptionProfile profile, HttpClient client, ILogger logger)
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
                ? "https://api.openai.com/v1"
                : _profile.BaseUrl.TrimEnd('/');

            var url = baseUrl + "/audio/transcriptions";

            using var response = await TransientHttpRetry.SendAsync(
                    _client,
                    () => Build(url, audio, format, languageHint),
                    _logger,
                    cancellationToken)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return TranscriptionResult.Failed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"transcription returned {(int)response.StatusCode}: {Trim(body)}"));
            }

            return Parse(body, duration);
        }

        /// <summary>Reads a verbose transcription response.</summary>
        /// <param name="body">The JSON.</param>
        /// <param name="duration">The clip's length, for the untimed fallback.</param>
        /// <returns>The transcript.</returns>
        /// <remarks>
        /// Public so the tests can exercise it against recorded responses from every
        /// server this is meant to work with. It is the part most likely to be quietly
        /// wrong — a local whisper server that omits <c>segments</c> would otherwise
        /// look like a working configuration producing bad sync numbers.
        /// </remarks>
        public static TranscriptionResult Parse(string? body, TimeSpan duration)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return TranscriptionResult.Failed("the transcriber returned an empty response");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                return TranscriptionResult.Failed("the transcriber returned unreadable JSON: " + ex.Message);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return TranscriptionResult.Failed("the transcriber returned something other than an object");
                }

                var language = root.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String
                    ? lang.GetString()
                    : null;

                var text = root.TryGetProperty("text", out var whole) && whole.ValueKind == JsonValueKind.String
                    ? whole.GetString() ?? string.Empty
                    : string.Empty;

                if (!root.TryGetProperty("segments", out var segments)
                    || segments.ValueKind != JsonValueKind.Array
                    || segments.GetArrayLength() == 0)
                {
                    // No segments. Either the model does not support verbose_json, or
                    // the clip was silent and the transcript is empty — and those are
                    // told apart by whether there is any text at all.
                    return text.Trim().Length == 0
                        ? TranscriptionResult.Failed("nothing was said in this window")
                        : TranscriptionResult.Untimed(text, duration, language);
                }

                var parsed = new List<TranscriptSegment>(segments.GetArrayLength());
                foreach (var segment in segments.EnumerateArray())
                {
                    var segmentText = segment.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString() ?? string.Empty
                        : string.Empty;

                    if (segmentText.Trim().Length == 0)
                    {
                        continue;
                    }

                    var start = Number(segment, "start");
                    var end = Number(segment, "end");

                    // A segment claiming to end before it starts is a decoder artefact
                    // and would give its words a negative span, which the tokenizer
                    // would spread backwards through the clip.
                    if (end < start)
                    {
                        end = start;
                    }

                    parsed.Add(new TranscriptSegment(
                        TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), segmentText.Trim()));
                }

                if (parsed.Count == 0)
                {
                    return TranscriptionResult.Failed("nothing was said in this window");
                }

                return new TranscriptionResult(parsed, language, true, null);
            }
        }

        private HttpRequestMessage Build(string url, byte[] audio, ClipFormat format, string? languageHint)
        {
            var content = new MultipartFormDataContent();

            var file = new ByteArrayContent(audio);
            file.Headers.ContentType = new MediaTypeHeaderValue(MimeType(format));

            // The filename is not decoration. Every whisper server routes on the
            // extension to pick a demuxer, and an Ogg stream sent as "audio.bin" is
            // rejected as an unsupported format by servers that would have decoded it
            // happily under the right name.
            content.Add(file, "file", "clip." + Extension(format));
            content.Add(new StringContent(_profile.Model), "model");
            content.Add(new StringContent("verbose_json"), "response_format");

            // Zero, not the default. Whisper's sampler will otherwise embellish a
            // difficult clip, and an invented sentence is a handful of words voting
            // for an offset that never existed.
            content.Add(new StringContent("0"), "temperature");

            if (!string.IsNullOrWhiteSpace(languageHint))
            {
                content.Add(new StringContent(languageHint), "language");
            }

            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

            if (!string.IsNullOrWhiteSpace(_profile.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _profile.ApiKey);
            }

            return request;
        }

        private static double Number(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : 0;

        private static string MimeType(ClipFormat format) => format switch
        {
            ClipFormat.Mp3 => "audio/mpeg",
            ClipFormat.Wav => "audio/wav",
            _ => "audio/ogg",
        };

        private static string Extension(ClipFormat format) => format switch
        {
            ClipFormat.Mp3 => "mp3",
            ClipFormat.Wav => "wav",
            _ => "ogg",
        };

        private static string Trim(string body) => body.Length > 300 ? body[..300] : body;
    }
}
