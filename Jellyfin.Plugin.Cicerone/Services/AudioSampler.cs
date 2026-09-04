using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Cicerone.Core.Audio;
using Jellyfin.Plugin.Cicerone.Core.Sync;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cicerone.Services
{
    /// <summary>One window's worth of audio, cut from the file.</summary>
    /// <param name="Anchor">The window it came from.</param>
    /// <param name="Bytes">The encoded clip.</param>
    /// <param name="Format">What container it is in.</param>
    /// <param name="Error">Why there is no clip, when there is none.</param>
    public sealed record AudioClip(Anchor Anchor, byte[] Bytes, ClipFormat Format, string? Error)
    {
        /// <summary>Gets whether there is audio here to transcribe.</summary>
        public bool Ok => Error is null && Bytes.Length > 0;
    }

    /// <summary>
    /// Cuts the planned windows of audio out of a media file.
    /// </summary>
    /// <remarks>
    /// This is where Cicerone's ground truth comes from. Everything else in the
    /// plugin is an opinion about a subtitle file; the clips this produces are what
    /// the film actually sounds like at a known moment, and the sync measurement is
    /// nothing more than the difference between the two.
    /// <para>
    /// Which makes one thing non-negotiable: the clip has to begin at the second it
    /// was asked for. <see cref="AudioPlan"/> carries the argument construction that
    /// guarantees it, and the reasoning for why the obvious command line does not.
    /// </para>
    /// </remarks>
    public sealed class AudioSampler
    {
        private readonly FfmpegRunner _ffmpeg;
        private readonly IMediaEncoder _encoder;
        private readonly ILogger<AudioSampler> _logger;

        /// <summary>Initialises a new instance of the <see cref="AudioSampler"/> class.</summary>
        /// <param name="ffmpeg">The process runner.</param>
        /// <param name="encoder">Jellyfin's own encoder, for the ffmpeg path it already knows.</param>
        /// <param name="logger">The logger.</param>
        public AudioSampler(FfmpegRunner ffmpeg, IMediaEncoder encoder, ILogger<AudioSampler> logger)
        {
            _ffmpeg = ffmpeg;
            _encoder = encoder;
            _logger = logger;
        }

        /// <summary>Maps Jellyfin's media streams onto the plain audio-track record.</summary>
        /// <param name="streams">Every stream on the item.</param>
        /// <returns>The audio tracks.</returns>
        public static IReadOnlyList<AudioTrack> AudioTracks(IReadOnlyList<MediaStream>? streams)
        {
            if (streams is null)
            {
                return [];
            }

            return streams
                .Where(s => s.Type == MediaStreamType.Audio)
                .Select(s => new AudioTrack(
                    s.Index,
                    s.Language ?? string.Empty,
                    s.IsDefault,
                    s.Channels ?? 0,
                    s.Title))
                .ToList();
        }

        /// <summary>Cuts every planned window out of one file.</summary>
        /// <param name="path">The media file.</param>
        /// <param name="anchors">The windows.</param>
        /// <param name="audioStreamIndex">Which audio stream to take.</param>
        /// <param name="format">The container to emit.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>One clip per anchor, in order, failures included.</returns>
        public async Task<IReadOnlyList<AudioClip>> ExtractAsync(
            string path,
            IReadOnlyList<Anchor> anchors,
            int audioStreamIndex,
            ClipFormat format,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(anchors);

            var clips = new List<AudioClip>(anchors.Count);

            // Sequentially, one ffmpeg at a time per item. The run already checks
            // several items at once, so widening here would multiply two concurrency
            // factors together and take the whole machine for a background job.
            foreach (var anchor in anchors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                clips.Add(await ExtractOneAsync(path, anchor, audioStreamIndex, format, cancellationToken)
                    .ConfigureAwait(false));
            }

            return clips;
        }

        private async Task<AudioClip> ExtractOneAsync(
            string path,
            Anchor anchor,
            int audioStreamIndex,
            ClipFormat format,
            CancellationToken cancellationToken)
        {
            var executable = _encoder.EncoderPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                return new AudioClip(anchor, [], format, "the server has no ffmpeg path configured");
            }

            var arguments = AudioPlan.ClipArguments(path, anchor.Start, anchor.Duration, audioStreamIndex, format);

            try
            {
                var result = await _ffmpeg.RunAsync(executable, arguments, cancellationToken).ConfigureAwait(false);
                if (result.Ok)
                {
                    return new AudioClip(anchor, result.Output, format, null);
                }

                // ffmpeg's stderr is the only account of what went wrong and it is
                // often one useful line buried in nothing. Kept short so a run log
                // stays readable, and kept at all because "extraction failed" without
                // it is unactionable.
                var reason = FirstLine(result.StandardError);
                _logger.LogDebug(
                    "Cicerone: no audio at {Start} of {Path}: {Reason}", anchor.Start, path, reason);

                return new AudioClip(anchor, [], format, reason);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A single unreadable window must not lose the item. Four other
                // anchors still describe the line, and a fit from four points is
                // barely worse than one from five.
                _logger.LogWarning(ex, "Cicerone: extracting audio from {Path} failed", path);
                return new AudioClip(anchor, [], format, ex.Message);
            }
        }

        private static string FirstLine(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "ffmpeg produced no audio and said nothing about why";
            }

            var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => l.Length > 0) ?? text.Trim();

            return line.Length > 200 ? line[..200] : line;
        }
    }
}
