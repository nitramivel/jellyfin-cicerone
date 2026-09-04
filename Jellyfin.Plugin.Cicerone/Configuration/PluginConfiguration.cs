using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Cicerone.Core.Audio;
using Jellyfin.Plugin.Cicerone.Core.Language;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Cicerone.Configuration
{
    /// <summary>What Cicerone does about a track it finds to be wrong.</summary>
    public enum RepairMode
    {
        /// <summary>Nothing. Report only.</summary>
        Off = 0,

        /// <summary>Write a corrected copy beside the media, leaving the original alone.</summary>
        WriteSidecar = 1,

        /// <summary>
        /// Write the corrected copy and ask Jellyfin to rescan so it appears as a
        /// selectable track.
        /// </summary>
        WriteSidecarAndRefresh = 2,
    }

    /// <summary>Cicerone's settings.</summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>Gets or sets the saved transcription profiles.</summary>
        public List<TranscriptionProfile> Profiles { get; set; } = [];

        /// <summary>Gets or sets the profile used when nothing else is specified.</summary>
        public string DefaultProfileId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the languages you actually want subtitles in, best first.
        /// </summary>
        /// <remarks>
        /// Comma-separated, in preference order — <c>en, es</c>. This is the setting
        /// the whole plugin is arranged around: it decides which tracks are worth
        /// spending audio on, which audio track each one is checked against, and
        /// which language an item is reported as missing.
        /// <para>
        /// Empty means every track in the library, which on a disc rip with sixteen
        /// language tracks is sixteen times the bill for fifteen answers nobody
        /// wanted.
        /// </para>
        /// </remarks>
        public string PreferredLanguages { get; set; } = "en";

        /// <summary>
        /// Gets or sets whether tracks are checked against their tagged language
        /// before any audio is fetched.
        /// </summary>
        /// <remarks>
        /// Free, and it runs first for that reason. It is also the only check that
        /// can explain the fault it finds: a track that is the wrong language
        /// entirely would otherwise come back as "does not match the dialogue",
        /// which is true and sends the owner looking for a sync problem.
        /// </remarks>
        public bool VerifyLanguage { get; set; } = true;

        /// <summary>Gets or sets how many windows of audio each track is checked against.</summary>
        /// <remarks>
        /// <b>Five, and never one.</b> One window can only measure a constant offset,
        /// and the most common real fault is a drift that reads as perfect sync
        /// wherever you happened to look. Two is the minimum that can see a slope at
        /// all; five is what makes the slope survive an anchor landing in a silence.
        /// </remarks>
        public int AnchorCount { get; set; } = 5;

        /// <summary>Gets or sets how long each window of audio is, in seconds.</summary>
        public int AnchorWindowSeconds { get; set; } = 30;

        /// <summary>
        /// Gets or sets how far out a subtitle track may be assumed to be, in seconds.
        /// </summary>
        /// <remarks>
        /// The search range, and it is not free to raise: every extra second widens
        /// the field of word pairings the aligner has to consider, and the wrong ones
        /// are noise. 120 covers everything short of a subtitle file for a different
        /// release; beyond that the answer wanted is "this is the wrong file", which
        /// arrives as a mismatch either way.
        /// </remarks>
        public int MaxOffsetSeconds { get; set; } = 120;

        /// <summary>Gets or sets the share of runtime skipped at the start, as a percentage.</summary>
        public double HeadTrimPercent { get; set; } = 5;

        /// <summary>Gets or sets the share of runtime skipped at the end, as a percentage.</summary>
        public double TailTrimPercent { get; set; } = 8;

        /// <summary>
        /// Gets or sets how far out a track may be and still be called in sync, in seconds.
        /// </summary>
        /// <remarks>
        /// A third of a second, which is about where a viewer starts to notice a
        /// subtitle arriving before the mouth moves. Below that the measurement's own
        /// error is the larger number anyway.
        /// </remarks>
        public double ToleranceSeconds { get; set; } = 0.35;

        /// <summary>
        /// Gets or sets how far the anchors may sit from the fitted line before the
        /// track is called mismatched rather than merely out of sync.
        /// </summary>
        public double MaxResidualSeconds { get; set; } = 1.5;

        /// <summary>
        /// Gets or sets whether a fitted drift close to a known frame rate ratio is
        /// snapped to it exactly.
        /// </summary>
        public bool SnapFrameRates { get; set; } = true;

        /// <summary>Gets or sets the container audio clips are sent in.</summary>
        public ClipFormat ClipFormat { get; set; } = ClipFormat.Opus;

        /// <summary>Gets or sets what happens to a track found to be out of sync.</summary>
        public RepairMode RepairMode { get; set; } = RepairMode.Off;

        /// <summary>
        /// Gets or sets how far out a track has to be before repairing it is worth
        /// doing, in seconds.
        /// </summary>
        /// <remarks>
        /// Above <see cref="ToleranceSeconds"/> on purpose. Between the two sits a
        /// band where the track is imperfect and rewriting it would move the timings
        /// by less than the measurement's own error — which is not a repair, it is a
        /// coin toss that leaves a second file in the folder.
        /// </remarks>
        public double MinErrorToRepairSeconds { get; set; } = 0.6;

        /// <summary>
        /// Gets or sets how confident the fit must be before a repair is written.
        /// </summary>
        public double MinConfidenceToRepair { get; set; } = 0.4;

        /// <summary>Gets or sets the suffix given to a repaired copy.</summary>
        public string RepairSuffix { get; set; } = "cicerone";

        /// <summary>
        /// Gets or sets whether repaired copies go beside the media rather than into
        /// the plugin's data directory.
        /// </summary>
        /// <remarks>
        /// Beside the media is the only location Jellyfin will pick a subtitle file
        /// up from, so turning this off means the repair exists and no player can see
        /// it. Kept as a setting anyway because a read-only library is a real
        /// arrangement, and a repair in the data directory is still something the
        /// owner can copy out by hand.
        /// </remarks>
        public bool WriteBesideMedia { get; set; } = true;

        /// <summary>Gets or sets whether movies are checked.</summary>
        public bool IncludeMovies { get; set; } = true;

        /// <summary>Gets or sets whether episodes are checked.</summary>
        /// <remarks>
        /// Off by default, and the reason is arithmetic rather than taste. A library
        /// of 200 films is 200 items; the same library's television is often ten
        /// thousand, and every one of them costs the same two and a half minutes of
        /// audio. Turn it on deliberately, after seeing what films cost.
        /// </remarks>
        public bool IncludeEpisodes { get; set; }

        /// <summary>
        /// Gets or sets whether items already checked are checked again.
        /// </summary>
        public bool RecheckExisting { get; set; }

        /// <summary>Gets or sets how many items are checked at once. 0 works it out.</summary>
        public int MaxConcurrency { get; set; }

        /// <summary>
        /// Gets or sets a ceiling on the audio one run may transcribe, in minutes.
        /// 0 means no ceiling.
        /// </summary>
        /// <remarks>
        /// The one setting standing between a first run over an unexamined library
        /// and a bill nobody agreed to. It is in minutes of audio rather than in
        /// dollars because minutes are what Cicerone can count exactly and prices
        /// change underneath it.
        /// </remarks>
        public int AudioMinuteBudget { get; set; } = 240;

        /// <summary>Gets the preferred languages, normalised, in order.</summary>
        /// <returns>Two-letter codes; empty when every language is wanted.</returns>
        public IReadOnlyList<string> Languages() =>
            (PreferredLanguages ?? string.Empty)
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(LanguageCodes.Normalize)
                .Where(c => c.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        /// <summary>Finds the profile a run should use.</summary>
        /// <returns>The profile, or null when none is configured.</returns>
        public TranscriptionProfile? ResolveProfile()
        {
            if (Profiles.Count == 0)
            {
                return null;
            }

            var byId = Profiles.FirstOrDefault(p => string.Equals(p.Id, DefaultProfileId, StringComparison.Ordinal));

            // Falling back to the first rather than to null: a configuration whose
            // default was deleted is a list with a usable profile in it, and refusing
            // to run would be a worse reading of the owner's intent than using it.
            return byId ?? Profiles[0];
        }

        /// <summary>How much audio one item will cost to check, in seconds.</summary>
        /// <param name="trackCount">How many tracks on the item are being checked.</param>
        /// <returns>Seconds of audio.</returns>
        /// <remarks>
        /// Exact, and knowable before anything is spent — which is the point of
        /// anchoring rather than transcribing whole files. A run can be quoted in
        /// advance instead of being explained afterwards.
        /// </remarks>
        public double AudioSecondsPerItem(int trackCount) =>
            Math.Max(trackCount, 0) * Math.Max(AnchorCount, 0) * Math.Max(AnchorWindowSeconds, 0);
    }
}
