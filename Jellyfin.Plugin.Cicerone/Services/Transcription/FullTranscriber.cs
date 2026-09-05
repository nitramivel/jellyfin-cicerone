using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Cicerone.Configuration;
using Jellyfin.Plugin.Cicerone.Core.Audio;
using Jellyfin.Plugin.Cicerone.Core.Subtitles;
using Jellyfin.Plugin.Cicerone.Core.Sync;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cicerone.Services.Transcription
{
    /// <summary>What transcribing a whole item produced.</summary>
    /// <param name="Cues">The subtitle track, ready to write.</param>
    /// <param name="AudioSeconds">How much audio was sent.</param>
    /// <param name="Language">What language the provider thought it heard.</param>
    /// <param name="PiecesSent">How many requests were made.</param>
    /// <param name="PiecesFailed">How many came back with nothing.</param>
    /// <param name="Error">Why there is no track, when there is none.</param>
    public sealed record FullTranscript(
        IReadOnlyList<Cue> Cues,
        double AudioSeconds,
        string? Language,
        int PiecesSent,
        int PiecesFailed,
        string? Error)
    {
        /// <summary>Gets whether a usable track came out.</summary>
        public bool Ok => Error is null && Cues.Count > 0;
    }

    /// <summary>
    /// Writes a subtitle track for an item that has none, by listening to all of it.
    /// </summary>
    /// <remarks>
    /// <b>The only thing in Cicerone that spends money now, and the only one that
    /// produces something rather than an opinion.</b> Checking sync stopped needing a
    /// model the moment the measurement moved to speech activity; what a model is still
    /// irreplaceable for is saying what the words were, and that is worth paying for
    /// exactly when there are no words on offer at all.
    /// <para>
    /// The economics are the reverse of everything else here. A check costs a fixed
    /// two and a half minutes of audio however long the film is; a transcription costs
    /// the film. That is why it is per item and opt-in rather than something a run does
    /// by default, and why the estimate the settings page shows for it is quoted in
    /// whole runtimes.
    /// </para>
    /// <para>
    /// Sent in pieces because a request that fails takes its piece with it, and losing
    /// ten minutes of a two-hour film is recoverable where losing the film is a
    /// restart. The pieces are rebased onto the item's clock as they come back — the
    /// same rebasing the anchor method needed, for the same reason, and just as fatal
    /// to forget.
    /// </para>
    /// </remarks>
    public sealed class FullTranscriber
    {
        private readonly AudioSampler _audio;
        private readonly TranscriptionProviderFactory _providers;
        private readonly ILogger<FullTranscriber> _logger;

        /// <summary>Initialises a new instance of the <see cref="FullTranscriber"/> class.</summary>
        /// <param name="audio">Audio extraction.</param>
        /// <param name="providers">Transcription backends.</param>
        /// <param name="logger">The logger.</param>
        public FullTranscriber(
            AudioSampler audio,
            TranscriptionProviderFactory providers,
            ILogger<FullTranscriber> logger)
        {
            _audio = audio;
            _providers = providers;
            _logger = logger;
        }

        /// <summary>Splits a runtime into the pieces that will be sent.</summary>
        /// <param name="runtime">How long the item runs.</param>
        /// <param name="chunkSeconds">How long each piece should be.</param>
        /// <returns>The pieces, in order, covering the whole runtime.</returns>
        /// <remarks>
        /// Pure and tested. The pieces butt up against one another exactly: an overlap
        /// would transcribe the same speech twice and put two cues on screen for it,
        /// and a gap would silently lose whatever was said across the join.
        /// </remarks>
        public static IReadOnlyList<Anchor> Pieces(TimeSpan runtime, int chunkSeconds)
        {
            var total = Math.Max(runtime.TotalSeconds, 0);
            var length = Math.Clamp(chunkSeconds, 30, 3600);

            if (total <= 0)
            {
                return [];
            }

            var pieces = new List<Anchor>();
            for (var at = 0.0; at < total; at += length)
            {
                pieces.Add(new Anchor(
                    TimeSpan.FromSeconds(at),
                    TimeSpan.FromSeconds(Math.Min(length, total - at)),
                    0));
            }

            return pieces;
        }

        /// <summary>Transcribes a whole item.</summary>
        /// <param name="path">The media file.</param>
        /// <param name="audioStreamIndex">Which audio stream to listen to.</param>
        /// <param name="runtime">How long the item runs.</param>
        /// <param name="languageHint">The language to expect, or null to let the provider guess.</param>
        /// <param name="config">The settings.</param>
        /// <param name="progress">Told the share of pieces finished, 0 to 1.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The track, or the reason there is none.</returns>
        public async Task<FullTranscript> TranscribeAsync(
            string path,
            int audioStreamIndex,
            TimeSpan runtime,
            string? languageHint,
            PluginConfiguration config,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(config);

            var profile = config.ResolveProfile();
            if (profile is null)
            {
                return new FullTranscript([], 0, null, 0, 0, "no transcription profile is configured");
            }

            var pieces = Pieces(runtime, config.TranscribeChunkSeconds);
            if (pieces.Count == 0)
            {
                return new FullTranscript([], 0, null, 0, 0, "the item has no runtime to transcribe");
            }

            var provider = _providers.Create(profile);
            var segments = new List<TranscriptSegment>();
            var seconds = 0.0;
            var failed = 0;
            string? language = null;

            for (var i = 0; i < pieces.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var piece = pieces[i];
                var clips = await _audio.ExtractAsync(
                        path, [piece], audioStreamIndex, config.ClipFormat, cancellationToken)
                    .ConfigureAwait(false);

                var clip = clips[0];
                if (!clip.Ok)
                {
                    failed++;
                    _logger.LogWarning(
                        "Cicerone: no audio for the piece at {Start} of {Path}: {Error}",
                        piece.Start, path, clip.Error);
                    continue;
                }

                var result = await provider.TranscribeAsync(
                        clip.Bytes, clip.Format, piece.Duration, languageHint, cancellationToken)
                    .ConfigureAwait(false);

                seconds += piece.Duration.TotalSeconds;

                if (!result.Ok)
                {
                    // One piece's failure is a gap in the subtitles, not a lost film.
                    // Reported in the count so the manager can say the track is
                    // incomplete rather than quietly handing over a film with a hole.
                    failed++;
                    _logger.LogWarning(
                        "Cicerone: the transcriber returned nothing for the piece at {Start}: {Error}",
                        piece.Start, result.Error);
                    continue;
                }

                language ??= result.DetectedLanguage;
                segments.AddRange(result.Segments.Select(s => s.Rebase(piece.Start)));

                progress?.Report((i + 1.0) / pieces.Count);
            }

            if (segments.Count == 0)
            {
                return new FullTranscript(
                    [], seconds, language, pieces.Count, failed,
                    "nothing came back from the transcriber for any part of the item");
            }

            return new FullTranscript(
                TranscriptToCues.Build(segments), seconds, language, pieces.Count, failed, null);
        }
    }
}
