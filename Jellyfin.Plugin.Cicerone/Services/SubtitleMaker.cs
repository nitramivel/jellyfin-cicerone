using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Cicerone.Configuration;
using Jellyfin.Plugin.Cicerone.Core.Audio;
using Jellyfin.Plugin.Cicerone.Core.Language;
using Jellyfin.Plugin.Cicerone.Services.Transcription;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cicerone.Services
{
    /// <summary>What came of writing a track for an item that had none.</summary>
    /// <param name="Ok">Whether a file was written.</param>
    /// <param name="Message">What happened, in a sentence.</param>
    /// <param name="Path">Where the track went.</param>
    /// <param name="CueCount">How many cues it holds.</param>
    /// <param name="AudioSeconds">How much audio was sent to be transcribed.</param>
    /// <param name="Incomplete">Whether some pieces of the film came back empty.</param>
    public sealed record MadeSubtitle(
        bool Ok,
        string Message,
        string? Path,
        int CueCount,
        double AudioSeconds,
        bool Incomplete)
    {
        /// <summary>A refusal or a failure.</summary>
        /// <param name="why">The reason.</param>
        /// <param name="audioSeconds">Audio already paid for before it failed.</param>
        /// <returns>The result.</returns>
        public static MadeSubtitle No(string why, double audioSeconds = 0) =>
            new(false, why, null, 0, audioSeconds, false);
    }

    /// <summary>
    /// Writes a subtitle track for an item that has none, and puts it where Jellyfin
    /// will find it.
    /// </summary>
    /// <remarks>
    /// The one operation in Cicerone that adds a subtitle rather than grading one, and
    /// it is deliberately narrow: it is offered for a language the item does not
    /// already have a readable track in, it always writes a new file marked as
    /// Cicerone's own, and it never touches anything that was already there.
    /// <para>
    /// <b>It is also the only thing here that still costs money</b>, and unlike the
    /// old per-item charge for checking, what it buys did not exist before. A film's
    /// whole runtime goes to the transcriber rather than two and a half minutes of it,
    /// so the arithmetic is completely different: this is priced per hour of film, and
    /// the settings page quotes it that way.
    /// </para>
    /// </remarks>
    public sealed class SubtitleMaker
    {
        private readonly IMediaSourceManager _mediaSources;
        private readonly FullTranscriber _transcriber;
        private readonly RepairWriter _writer;
        private readonly ILogger<SubtitleMaker> _logger;

        /// <summary>Initialises a new instance of the <see cref="SubtitleMaker"/> class.</summary>
        /// <param name="mediaSources">Where an item's streams are read from.</param>
        /// <param name="transcriber">The whole-file transcriber.</param>
        /// <param name="writer">Where the track is written.</param>
        /// <param name="logger">The logger.</param>
        public SubtitleMaker(
            IMediaSourceManager mediaSources,
            FullTranscriber transcriber,
            RepairWriter writer,
            ILogger<SubtitleMaker> logger)
        {
            _mediaSources = mediaSources;
            _transcriber = transcriber;
            _writer = writer;
            _logger = logger;
        }

        /// <summary>Transcribes an item and writes the result beside it.</summary>
        /// <param name="item">The item.</param>
        /// <param name="language">The language to write, which also picks the audio track.</param>
        /// <param name="config">The settings.</param>
        /// <param name="progress">Told the share of the film finished, 0 to 1.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What happened.</returns>
        public async Task<MadeSubtitle> MakeAsync(
            BaseItem item,
            string language,
            PluginConfiguration config,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(config);

            if (string.IsNullOrWhiteSpace(item.Path))
            {
                return MadeSubtitle.No("the media file is not readable from the server");
            }

            if (config.ResolveProfile() is null)
            {
                return MadeSubtitle.No(
                    "no transcription profile is configured — add one on the Models tab first");
            }

            var runtime = item.RunTimeTicks is { } ticks ? TimeSpan.FromTicks(ticks) : TimeSpan.Zero;
            if (runtime <= TimeSpan.Zero)
            {
                return MadeSubtitle.No("the item has no known runtime, so there is nothing to divide into pieces");
            }

            var audioTracks = AudioSampler.AudioTracks(_mediaSources.GetMediaStreams(item.Id));
            if (audioTracks.Count == 0)
            {
                return MadeSubtitle.No("the item carries no audio track to transcribe");
            }

            // The same language match the checker uses. Transcribing a German dub and
            // filing the result as English would produce a track that is fluent,
            // well-timed and wrong.
            var wanted = LanguageCodes.Normalize(language);
            var audio = AudioPlan.ChooseAudio(audioTracks, wanted);
            if (audio is null)
            {
                return MadeSubtitle.No("no audio track could be chosen for that language");
            }

            if (wanted.Length > 0 && !audioTracks.Any(a => LanguageCodes.Same(a.Language, wanted)))
            {
                _logger.LogInformation(
                    "Cicerone: {Item} has no {Language} audio; transcribing {Chosen} instead",
                    item.Name, wanted, audio.Language);
            }

            var transcript = await _transcriber.TranscribeAsync(
                    item.Path, audio.Index, runtime,
                    wanted.Length > 0 ? wanted : null, config, progress, cancellationToken)
                .ConfigureAwait(false);

            if (!transcript.Ok)
            {
                return MadeSubtitle.No(
                    transcript.Error ?? "the transcriber returned nothing", transcript.AudioSeconds);
            }

            var written = await _writer.WriteCuesAsync(
                    item,
                    transcript.Cues,
                    wanted.Length > 0 ? wanted : LanguageCodes.Normalize(transcript.Language),
                    config.HeardSuffix,
                    false,
                    true,
                    config,
                    cancellationToken)
                .ConfigureAwait(false);

            if (written is null)
            {
                return MadeSubtitle.No(
                    "the track was transcribed but could not be written — check the folder is writable by Jellyfin",
                    transcript.AudioSeconds);
            }

            var incomplete = transcript.PiecesFailed > 0;
            _logger.LogInformation(
                "Cicerone: wrote {Cues} cues heard from {Item} to {Path}",
                transcript.Cues.Count, item.Name, written);

            return new MadeSubtitle(
                true,
                incomplete
                    ? $"wrote {transcript.Cues.Count} cues, but {transcript.PiecesFailed} of "
                        + $"{transcript.PiecesSent} pieces came back empty — the track has gaps in it"
                    : $"wrote {transcript.Cues.Count} cues from {transcript.AudioSeconds / 60:0} minutes of audio",
                written,
                transcript.Cues.Count,
                transcript.AudioSeconds,
                incomplete);
        }
    }
}
