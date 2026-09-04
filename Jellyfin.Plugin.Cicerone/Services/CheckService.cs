using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Cicerone.Configuration;
using Jellyfin.Plugin.Cicerone.Core.Audio;
using Jellyfin.Plugin.Cicerone.Core.Language;
using Jellyfin.Plugin.Cicerone.Core.Reports;
using Jellyfin.Plugin.Cicerone.Core.Subtitles;
using Jellyfin.Plugin.Cicerone.Core.Sync;
using Jellyfin.Plugin.Cicerone.Services.Transcription;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cicerone.Services
{
    /// <summary>
    /// Checks one item: reads its subtitles, listens to its audio, and says how far
    /// apart the two are.
    /// </summary>
    /// <remarks>
    /// The order of operations here is the plugin's whole economy.
    /// <list type="number">
    /// <item>Tracks are filtered to the languages you asked for, before anything.</item>
    /// <item>The language of each track is read from its own text — free.</item>
    /// <item>Anchors are planned from the cue timings — free.</item>
    /// <item><b>Audio is transcribed once per item</b>, and every track sharing that
    /// audio is scored against the same transcript. Three English tracks cost what
    /// one costs.</item>
    /// <item>Each track is aligned, fitted and judged — free.</item>
    /// </list>
    /// Exactly one step in that list spends anything, and it is the only one that
    /// touches the network. Everything above it exists to make sure the money is
    /// spent on a question worth answering.
    /// </remarks>
    public sealed class CheckService
    {
        private readonly IMediaSourceManager _mediaSources;
        private readonly SubtitleReader _subtitles;
        private readonly AudioSampler _audio;
        private readonly TranscriptionProviderFactory _providers;
        private readonly RepairWriter _repairs;
        private readonly ILogger<CheckService> _logger;

        /// <summary>Initialises a new instance of the <see cref="CheckService"/> class.</summary>
        /// <param name="mediaSources">Where a item's streams are read from.</param>
        /// <param name="subtitles">Subtitle extraction.</param>
        /// <param name="audio">Audio extraction.</param>
        /// <param name="providers">Transcription backends.</param>
        /// <param name="repairs">Where a corrected copy is written.</param>
        /// <param name="logger">The logger.</param>
        public CheckService(
            IMediaSourceManager mediaSources,
            SubtitleReader subtitles,
            AudioSampler audio,
            TranscriptionProviderFactory providers,
            RepairWriter repairs,
            ILogger<CheckService> logger)
        {
            _mediaSources = mediaSources;
            _subtitles = subtitles;
            _audio = audio;
            _providers = providers;
            _repairs = repairs;
            _logger = logger;
        }

        /// <summary>Checks one item.</summary>
        /// <param name="item">The item.</param>
        /// <param name="config">The settings to run under.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The report.</returns>
        public async Task<ItemReport> CheckAsync(
            BaseItem item,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(config);

            var kind = item is Episode ? "Episode" : "Movie";
            var series = (item as Episode)?.SeriesName;
            var runtime = item.RunTimeTicks is { } ticks ? TimeSpan.FromTicks(ticks) : TimeSpan.Zero;
            var version = MediaVersion(item);
            var languages = config.Languages();

            ItemReport Failed(string reason) => new(
                item.Id, item.Name ?? string.Empty, kind, series, DateTime.UtcNow, version,
                runtime.TotalSeconds, [], languages, 0, null, reason);

            if (string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
            {
                return Failed("the media file is not readable from the server");
            }

            var profile = config.ResolveProfile();
            if (profile is null)
            {
                return Failed("no transcription profile is configured");
            }

            var streams = _mediaSources.GetMediaStreams(item.Id);
            var allTracks = SubtitleReader.Tracks(streams);
            var audioTracks = AudioSampler.AudioTracks(streams);

            if (audioTracks.Count == 0)
            {
                return Failed("the item carries no audio track to listen to");
            }

            // Filtered before anything is read. A disc rip with sixteen subtitle
            // languages is sixteen extractions and, without this, sixteen sets of
            // anchors — for fifteen answers nobody asked for.
            var wanted = languages.Count == 0
                ? allTracks
                : allTracks.Where(t => languages.Any(l => LanguageCodes.Same(t.Language, l))).ToList();

            var reports = new List<TrackReport>();
            var audioSeconds = 0.0;

            foreach (var skipped in wanted.Where(t => !t.Checkable))
            {
                reports.Add(new TrackReport(skipped, 0, null, false, null, skipped.SkipReason()));
            }

            var checkable = wanted.Where(t => t.Checkable).ToList();

            // Read every track first, so the language verdicts — which cost nothing —
            // are all in before any audio is fetched. A track that turns out to be
            // the wrong language is one whose audio never has to be paid for.
            var contents = new Dictionary<int, TrackContent>();
            foreach (var track in checkable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                contents[track.Index] = await _subtitles.ReadAsync(item, track.Index, cancellationToken)
                    .ConfigureAwait(false);
            }

            var toAlign = new List<TrackCandidate>();
            var languageGuesses = new Dictionary<int, LanguageGuess?>();

            foreach (var track in checkable)
            {
                var content = contents[track.Index];
                if (content.Error is not null || content.Clean.Count == 0)
                {
                    reports.Add(new TrackReport(track, 0, null, false, null, content.Error));
                    continue;
                }

                LanguageGuess? guess = null;
                var contradicted = false;

                if (config.VerifyLanguage)
                {
                    var sample = Sample(content.Clean);
                    contradicted = LanguageProfile.Contradicts(sample, track.Language, out var detected);
                    guess = detected.Code is null or "" ? null : detected;
                }

                languageGuesses[track.Index] = guess;

                if (contradicted)
                {
                    reports.Add(new TrackReport(track, content.Clean.Count, guess, true, null, null));
                    continue;
                }

                toAlign.Add(track);
            }

            if (toAlign.Count == 0)
            {
                return new ItemReport(
                    item.Id, item.Name ?? string.Empty, kind, series, DateTime.UtcNow, version,
                    runtime.TotalSeconds, reports, Missing(languages, reports), 0, profile.Model, null);
            }

            var provider = _providers.Create(profile);

            // Grouped by the audio track each subtitle track wants to be checked
            // against. Everything in a group hears the same clips.
            var groups = toAlign
                .GroupBy(t => AudioPlan.ChooseAudio(audioTracks, t.Language)?.Index ?? audioTracks[0].Index)
                .ToList();

            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var members = group.ToList();

                // Anchors are planned from the fullest track in the group. Any of them
                // would do — the planner is looking for where the film is talkative,
                // which is a property of the film — and the fullest has the most cues
                // to read that from.
                var planFrom = members
                    .OrderByDescending(t => contents[t.Index].Clean.Count)
                    .First();

                var anchors = AnchorPlanner.Plan(
                    contents[planFrom.Index].Clean,
                    runtime,
                    config.AnchorCount,
                    TimeSpan.FromSeconds(config.AnchorWindowSeconds),
                    config.HeadTrimPercent,
                    config.TailTrimPercent);

                if (anchors.Count == 0)
                {
                    foreach (var track in members)
                    {
                        reports.Add(new TrackReport(
                            track, contents[track.Index].Clean.Count, languageGuesses.GetValueOrDefault(track.Index),
                            false, null, "there was no stretch of dialogue long enough to listen to"));
                    }

                    continue;
                }

                var hint = profile.HintLanguage ? LanguageCodes.Normalize(planFrom.Language) : null;

                var transcript = await ListenAsync(
                        item.Path, anchors, group.Key, config, provider,
                        string.IsNullOrEmpty(hint) ? null : hint, cancellationToken)
                    .ConfigureAwait(false);

                audioSeconds += transcript.Sum(t => t.Anchor.Duration.TotalSeconds);

                foreach (var track in members)
                {
                    var assessment = Align(contents[track.Index].Clean, transcript, runtime, config);

                    string? repaired = null;
                    if (ShouldRepair(assessment, config))
                    {
                        repaired = await _repairs.WriteAsync(
                                item, track, contents[track.Index].Raw, assessment.Correction, config,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    reports.Add(new TrackReport(
                        track,
                        contents[track.Index].Clean.Count,
                        languageGuesses.GetValueOrDefault(track.Index),
                        false,
                        assessment,
                        null));

                    if (repaired is not null)
                    {
                        _logger.LogInformation(
                            "Cicerone: wrote a corrected copy of {Item} track {Index} to {Path}",
                            item.Name, track.Index, repaired);
                    }
                }
            }

            return new ItemReport(
                item.Id,
                item.Name ?? string.Empty,
                kind,
                series,
                DateTime.UtcNow,
                version,
                runtime.TotalSeconds,
                reports.OrderBy(r => r.Track.Index).ToList(),
                Missing(languages, reports),
                audioSeconds,
                profile.Model,
                null);
        }

        /// <summary>What one anchor's audio turned into.</summary>
        /// <param name="Anchor">The window.</param>
        /// <param name="Segments">What was heard, on the item's clock.</param>
        /// <param name="Timestamped">Whether the provider gave real times.</param>
        /// <param name="Error">Why nothing was heard, when nothing was.</param>
        private sealed record Heard(
            Anchor Anchor,
            IReadOnlyList<TranscriptSegment> Segments,
            bool Timestamped,
            string? Error);

        private async Task<IReadOnlyList<Heard>> ListenAsync(
            string path,
            IReadOnlyList<Anchor> anchors,
            int audioStreamIndex,
            PluginConfiguration config,
            ITranscriptionProvider provider,
            string? languageHint,
            CancellationToken cancellationToken)
        {
            var clips = await _audio.ExtractAsync(
                    path, anchors, audioStreamIndex, config.ClipFormat, cancellationToken)
                .ConfigureAwait(false);

            var heard = new List<Heard>(clips.Count);

            foreach (var clip in clips)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!clip.Ok)
                {
                    heard.Add(new Heard(clip.Anchor, [], false, clip.Error));
                    continue;
                }

                var result = await provider.TranscribeAsync(
                        clip.Bytes, clip.Format, clip.Anchor.Duration, languageHint, cancellationToken)
                    .ConfigureAwait(false);

                if (!result.Ok)
                {
                    heard.Add(new Heard(clip.Anchor, [], false, result.Error ?? "nothing came back"));
                    continue;
                }

                // Rebasing happens here and exactly once. A provider reports times from
                // the start of the clip it was given; the aligner compares against
                // subtitle times, which are on the item's clock. Skipping this makes
                // every anchor measure its own position in the film as the offset —
                // large, consistent, and complete fiction.
                heard.Add(new Heard(
                    clip.Anchor,
                    result.Segments.Select(s => s.Rebase(clip.Anchor.Start)).ToList(),
                    result.Timestamped,
                    null));
            }

            return heard;
        }

        private static SyncAssessment Align(
            IReadOnlyList<CleanCue> cues,
            IReadOnlyList<Heard> transcript,
            TimeSpan runtime,
            PluginConfiguration config)
        {
            var points = new List<AnchorPoint>();

            foreach (var window in transcript)
            {
                if (window.Segments.Count == 0)
                {
                    continue;
                }

                // The slack has to be at least the offset being searched for. Cues are
                // read from the subtitle file's own clock, so if the file is ninety
                // seconds late, the words spoken during this window are written ninety
                // seconds further on — and a window that only reads cues inside its own
                // bounds excludes the exact evidence that would prove it.
                var cueTokens = AnchorPlanner.CueTokens(cues, window.Anchor, config.MaxOffsetSeconds);
                var heardTokens = Retiming.HeardTokens(window.Segments);

                var measurement = OffsetSearch.Measure(cueTokens, heardTokens, config.MaxOffsetSeconds);
                if (measurement.Votes == 0)
                {
                    continue;
                }

                var confidence = measurement.Confidence;

                // An untimed transcript places its words by assuming an even speaking
                // rate, which is good to a few seconds and no better. Halving its
                // weight keeps the anchor in the fit — it still rules out a gross
                // offset — without letting it set the tenths of a second that decide
                // whether a track passes.
                if (!window.Timestamped)
                {
                    confidence *= 0.5;
                }

                points.Add(new AnchorPoint(
                    window.Anchor.Start + (window.Anchor.Duration / 2), measurement.Offset, confidence));
            }

            var correction = DriftFit.Fit(points, config.SnapFrameRates);

            return SyncVerdictBuilder.Assess(
                points, correction, runtime, config.ToleranceSeconds, config.MaxResidualSeconds);
        }

        private static bool ShouldRepair(SyncAssessment assessment, PluginConfiguration config)
        {
            if (config.RepairMode == RepairMode.Off || !assessment.Repairable)
            {
                return false;
            }

            if (assessment.WorstErrorSeconds < config.MinErrorToRepairSeconds)
            {
                return false;
            }

            // Averaged over the anchors that counted, not over all of them: an anchor
            // that produced nothing is silence, not disagreement, and letting it drag
            // the average down would block repairs on quiet films.
            var used = assessment.Anchors.Where(a => a.Confidence >= DriftFit.MinConfidence).ToList();
            if (used.Count < 2)
            {
                return false;
            }

            return used.Average(a => a.Confidence) >= config.MinConfidenceToRepair;
        }

        /// <summary>Takes a readable sample of a track for the language check.</summary>
        /// <remarks>
        /// The middle of the file rather than the head. The opening minutes of a
        /// ripped subtitle file are where the release-group credit, the translator's
        /// name and any English-language titles live, and all three read as English
        /// whatever language the film is in.
        /// </remarks>
        private static string Sample(IReadOnlyList<CleanCue> cues)
        {
            var from = cues.Count / 4;
            var take = Math.Min(400, cues.Count - from);
            return string.Join(' ', cues.Skip(from).Take(take).Select(c => c.Text));
        }

        private static IReadOnlyList<string> Missing(
            IReadOnlyList<string> languages,
            IReadOnlyList<TrackReport> reports) => languages
            .Where(l => !reports.Any(r =>
                LanguageCodes.Same(r.Track.Language, l) && r.Skipped is null && r.CueCount > 0))
            .ToList();

        private static string MediaVersion(BaseItem item)
        {
            try
            {
                var file = new FileInfo(item.Path);
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{file.Length}-{file.LastWriteTimeUtc.Ticks}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Without a version the report simply never goes stale on its own,
                // which is a worse default than re-checking but far better than
                // refusing to record the check at all.
                return string.Empty;
            }
        }
    }
}
