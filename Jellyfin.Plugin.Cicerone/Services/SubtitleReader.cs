using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Cicerone.Core.Subtitles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cicerone.Services
{
    /// <summary>A track's cues, raw and cleaned.</summary>
    /// <param name="Raw">Every cue as it appears in the file, for rewriting.</param>
    /// <param name="Clean">The spoken lines, for matching.</param>
    /// <param name="Error">Why there are none, when there are none.</param>
    public sealed record TrackContent(
        IReadOnlyList<Cue> Raw,
        IReadOnlyList<CleanCue> Clean,
        string? Error);

    /// <summary>
    /// Gets a subtitle track out of a file and into cues.
    /// </summary>
    /// <remarks>
    /// Everything goes through <c>ISubtitleEncoder</c> asking for <c>"srt"</c>,
    /// which is what lets Cicerone parse exactly one format. The server converts
    /// ASS, mov_text, WebVTT and external files on the way out, and an embedded
    /// stream costs an ffmpeg invocation inside the server to do it — which is why
    /// a track is read once per item and held, never re-read per anchor.
    /// </remarks>
    public sealed class SubtitleReader
    {
        private readonly ISubtitleEncoder _encoder;
        private readonly ILogger<SubtitleReader> _logger;

        /// <summary>Initialises a new instance of the <see cref="SubtitleReader"/> class.</summary>
        /// <param name="encoder">Jellyfin's subtitle encoder.</param>
        /// <param name="logger">The logger.</param>
        public SubtitleReader(ISubtitleEncoder encoder, ILogger<SubtitleReader> logger)
        {
            _encoder = encoder;
            _logger = logger;
        }

        /// <summary>Maps Jellyfin's media streams onto the plain track record.</summary>
        /// <param name="streams">Every stream on the item.</param>
        /// <returns>The subtitle tracks.</returns>
        public static IReadOnlyList<TrackCandidate> Tracks(IReadOnlyList<MediaStream>? streams)
        {
            if (streams is null)
            {
                return [];
            }

            return streams
                .Where(s => s.Type == MediaStreamType.Subtitle)
                .Select(s => new TrackCandidate(
                    s.Index,
                    s.Language ?? string.Empty,
                    s.Title,
                    s.IsTextSubtitleStream,
                    s.IsForced,
                    s.IsHearingImpaired,
                    s.IsExternal,
                    s.IsDefault,
                    s.Path))
                .ToList();
        }

        /// <summary>Reads one track.</summary>
        /// <param name="item">The item it belongs to.</param>
        /// <param name="streamIndex">The stream index.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The cues, or the reason there are none.</returns>
        public async Task<TrackContent> ReadAsync(
            BaseItem item,
            int streamIndex,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);

            try
            {
                await using var stream = await _encoder.GetSubtitles(
                        item,
                        item.Id.ToString("N", CultureInfo.InvariantCulture),
                        streamIndex,
                        "srt",
                        0,
                        0,
                        false,
                        cancellationToken)
                    .ConfigureAwait(false);

                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                var raw = SrtParser.Parse(content);
                if (raw.Count == 0)
                {
                    return new TrackContent([], [], "the track parsed to no cues at all");
                }

                var clean = CueCleaner.Clean(raw);
                if (clean.Count == 0)
                {
                    // Every cue was an annotation or a credit. Common on a track that
                    // is nominally SDH and actually just a sound-effects layer.
                    return new TrackContent(raw, [], "the track held no dialogue once annotations were removed");
                }

                return new TrackContent(raw, clean, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Cicerone: could not read subtitle stream {Index} of {Item}", streamIndex, item.Name);

                return new TrackContent([], [], "the track could not be extracted: " + ex.Message);
            }
        }
    }
}
