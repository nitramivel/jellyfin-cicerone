using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Cicerone.Core.Audio;
using Jellyfin.Plugin.Cicerone.Core.Sync;

namespace Jellyfin.Plugin.Cicerone.Services.Transcription
{
    /// <summary>What a transcriber heard in one clip.</summary>
    /// <param name="Segments">
    /// The speech, timed <b>relative to the start of the clip</b>. The caller rebases
    /// them onto the item's clock; nothing here knows where the window sat.
    /// </param>
    /// <param name="DetectedLanguage">What language the provider thought it was, when it says.</param>
    /// <param name="Timestamped">
    /// Whether the times are the provider's or invented. See
    /// <see cref="TranscriptionResult.Untimed"/> for why this must be reported rather
    /// than papered over.
    /// </param>
    /// <param name="Error">Why there is nothing here, when there is nothing.</param>
    public sealed record TranscriptionResult(
        IReadOnlyList<TranscriptSegment> Segments,
        string? DetectedLanguage,
        bool Timestamped,
        string? Error)
    {
        /// <summary>Gets whether anything usable came back.</summary>
        public bool Ok => Error is null && Segments.Count > 0;

        /// <summary>A failure.</summary>
        /// <param name="reason">What went wrong.</param>
        /// <returns>The result.</returns>
        public static TranscriptionResult Failed(string reason) => new([], null, false, reason);

        /// <summary>
        /// A transcript with no timings, spread evenly across the clip.
        /// </summary>
        /// <param name="text">Everything that was said.</param>
        /// <param name="duration">How long the clip was.</param>
        /// <param name="language">The detected language, when known.</param>
        /// <returns>The result, marked as untimed.</returns>
        /// <remarks>
        /// <b>A last resort, and it is flagged all the way to the report.</b> Cicerone
        /// measures a difference in the tenths of a second, and a word placed by
        /// assuming an even speaking rate across thirty seconds is several seconds
        /// out. Spread like this the vote still finds a gross offset — a track a
        /// minute late is still obviously a minute late — and it cannot see the third
        /// of a second that separates "in sync" from "not". So the anchor is kept,
        /// its confidence is discounted, and the report says the timings were
        /// estimated rather than heard.
        /// </remarks>
        public static TranscriptionResult Untimed(string text, TimeSpan duration, string? language) =>
            new([new TranscriptSegment(TimeSpan.Zero, duration, text)], language, false, null);
    }

    /// <summary>
    /// A speech-to-text backend.
    /// </summary>
    /// <remarks>
    /// Implementations are stateless and carry their own endpoint, credentials and
    /// wire format.
    /// <para>
    /// <b>The interface demands timestamps, and that constrains which models can sit
    /// behind it.</b> Cicerone does not need a good transcript — it needs a
    /// <em>timed</em> one, because the entire output is a comparison between when a
    /// word was said and when the subtitle claims it was. A model that returns
    /// perfect prose and no times answers a question nobody asked.
    /// </para>
    /// </remarks>
    public interface ITranscriptionProvider
    {
        /// <summary>Gets the model identifier, for logging and the run record.</summary>
        string ModelId { get; }

        /// <summary>Transcribes one clip.</summary>
        /// <param name="audio">The encoded audio.</param>
        /// <param name="format">What container it is in.</param>
        /// <param name="duration">How long the clip is, used when a provider returns no times.</param>
        /// <param name="languageHint">
        /// The language to expect, or null to let the provider guess. Passing it stops
        /// Whisper translating instead of transcribing, which would leave the
        /// transcript sharing no words with the subtitle track being checked.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What was heard.</returns>
        Task<TranscriptionResult> TranscribeAsync(
            byte[] audio,
            ClipFormat format,
            TimeSpan duration,
            string? languageHint,
            CancellationToken cancellationToken);
    }
}
