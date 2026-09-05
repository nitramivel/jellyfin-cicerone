using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Cicerone.Core.Audio;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cicerone.Services
{
    /// <summary>What listening to a whole file for speech produced.</summary>
    /// <param name="Spans">Every stretch where somebody is speaking.</param>
    /// <param name="Error">Why there are none, when there are none.</param>
    public sealed record SpeechActivity(IReadOnlyList<SpeechSpan> Spans, string? Error)
    {
        /// <summary>Gets whether there is anything to correlate against.</summary>
        public bool Ok => Error is null && Spans.Count > 0;
    }

    /// <summary>
    /// Asks ffmpeg where a file has speech in it.
    /// </summary>
    /// <remarks>
    /// <b>The whole of Cicerone's ground truth, and it costs a decode.</b> Where the
    /// plugin used to cut a few windows out of a film and pay somebody to listen to
    /// them, it now reads the entire audio track locally and gets back the shape of
    /// the conversation — every pause, every exchange, for the length of the film.
    /// That is both more evidence and no money, and it is the change that made
    /// checking a library of episodes possible at all.
    /// <para>
    /// It is not free of everything: it is a full decode of one audio stream, which is
    /// tens of seconds of CPU for a feature. That is why it runs below normal priority
    /// like every other ffmpeg call here, and why the result is fetched once per item
    /// and shared by every subtitle track wanting that audio.
    /// </para>
    /// </remarks>
    public sealed class SpeechActivityReader
    {
        private readonly FfmpegRunner _ffmpeg;
        private readonly IMediaEncoder _encoder;
        private readonly ILogger<SpeechActivityReader> _logger;

        /// <summary>Initialises a new instance of the <see cref="SpeechActivityReader"/> class.</summary>
        /// <param name="ffmpeg">The process runner.</param>
        /// <param name="encoder">Jellyfin's own encoder, for the ffmpeg path it already knows.</param>
        /// <param name="logger">The logger.</param>
        public SpeechActivityReader(
            FfmpegRunner ffmpeg,
            IMediaEncoder encoder,
            ILogger<SpeechActivityReader> logger)
        {
            _ffmpeg = ffmpeg;
            _encoder = encoder;
            _logger = logger;
        }

        /// <summary>Finds every stretch of speech in one audio stream.</summary>
        /// <param name="path">The media file.</param>
        /// <param name="audioStreamIndex">Which audio stream to listen to.</param>
        /// <param name="duration">How long the item runs.</param>
        /// <param name="noiseFloorDb">The level below which audio counts as silence.</param>
        /// <param name="minSilenceSeconds">How long a quiet stretch must run to count.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The spans, or the reason there are none.</returns>
        public async Task<SpeechActivity> ReadAsync(
            string path,
            int audioStreamIndex,
            TimeSpan duration,
            double noiseFloorDb,
            double minSilenceSeconds,
            CancellationToken cancellationToken)
        {
            var executable = _encoder.EncoderPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                return new SpeechActivity([], "the server has no ffmpeg path configured");
            }

            var arguments = VadPlan.DetectArguments(path, audioStreamIndex, noiseFloorDb, minSilenceSeconds);

            try
            {
                var result = await _ffmpeg.RunAsync(executable, arguments, cancellationToken).ConfigureAwait(false);

                // The exit code is what is checked, not the output: this call writes
                // nothing to stdout by design, so the runner's own idea of success —
                // which includes having produced bytes — does not apply.
                if (result.ExitCode != 0)
                {
                    return new SpeechActivity([], Summarise(result.StandardError));
                }

                var spans = VadPlan.ParseSilences(result.StandardError, duration);
                if (spans.Count == 0)
                {
                    return new SpeechActivity([], "the audio track came back as one unbroken silence");
                }

                _logger.LogDebug(
                    "Cicerone: {Count} stretches of speech in stream {Index} of {Path}",
                    spans.Count, audioStreamIndex, path);

                return new SpeechActivity(spans, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cicerone: could not read speech activity from {Path}", path);
                return new SpeechActivity([], ex.Message);
            }
        }

        /// <summary>Pulls something useful out of ffmpeg's stderr.</summary>
        private static string Summarise(string? stderr)
        {
            if (string.IsNullOrWhiteSpace(stderr))
            {
                return "ffmpeg failed and said nothing about why";
            }

            // The last error line rather than the first. This call runs at info level
            // because silencedetect reports through the log, so the head of stderr is
            // the banner and the stream listing, and what went wrong is at the end.
            var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (lines[i].Contains("rror", StringComparison.Ordinal)
                    || lines[i].Contains("Invalid", StringComparison.Ordinal)
                    || lines[i].Contains("No such", StringComparison.Ordinal))
                {
                    return lines[i].Length > 200 ? lines[i][..200] : lines[i];
                }
            }

            var last = lines.Length > 0 ? lines[^1] : stderr.Trim();
            return last.Length > 200 ? last[..200] : last;
        }
    }
}
