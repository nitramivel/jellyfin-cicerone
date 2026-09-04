using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Cicerone.Core.Language;
using Jellyfin.Plugin.Cicerone.Core.Subtitles;
using Jellyfin.Plugin.Cicerone.Core.Sync;

namespace Jellyfin.Plugin.Cicerone.Core.Reports
{
    /// <summary>Everything Cicerone knows about one item's subtitles.</summary>
    /// <param name="ItemId">The Jellyfin item.</param>
    /// <param name="Name">Its name when it was checked.</param>
    /// <param name="Kind">Movie or Episode.</param>
    /// <param name="SeriesName">The series, for an episode.</param>
    /// <param name="CheckedUtc">When it was checked.</param>
    /// <param name="MediaVersion">
    /// The file's size and modification time, joined. This is what makes a stored
    /// report stale: a re-encode or a replaced release keeps the item and invalidates
    /// every measurement made against the old file.
    /// </param>
    /// <param name="RuntimeSeconds">The item's runtime.</param>
    /// <param name="Tracks">One entry per subtitle track examined.</param>
    /// <param name="MissingLanguages">Preferred languages with no usable track at all.</param>
    /// <param name="AudioSeconds">How much audio was transcribed for this item.</param>
    /// <param name="Model">The transcription model used.</param>
    /// <param name="Error">Why the item could not be checked, when it could not.</param>
    public sealed record ItemReport(
        Guid ItemId,
        string Name,
        string Kind,
        string? SeriesName,
        DateTime CheckedUtc,
        string MediaVersion,
        double RuntimeSeconds,
        IReadOnlyList<TrackReport> Tracks,
        IReadOnlyList<string> MissingLanguages,
        double AudioSeconds,
        string? Model,
        string? Error)
    {
        /// <summary>Gets the best track for a language, or null when there is none.</summary>
        /// <param name="language">The language wanted.</param>
        /// <returns>The best track Cicerone found.</returns>
        /// <remarks>
        /// <b>Order matters more than the scores do.</b> A track that was measured
        /// and found good beats one nothing is known about, which beats one known to
        /// be wrong — because the whole point of having checked is that a verified
        /// track should win. Within a tier it is the fuller, cleaner, embedded track,
        /// in that order.
        /// </remarks>
        public TrackReport? Best(string language) => Tracks
            .Where(t => LanguageCodes.Same(t.Track.Language, language) && t.Skipped is null)
            .OrderByDescending(t => Tier(t.Verdict))
            .ThenByDescending(t => t.CueCount)
            .ThenBy(t => t.Track.IsHearingImpaired)

            // Embedded ahead of external, which inverts what a plugin merely reading
            // subtitles would do. An embedded track was timed against the encode it
            // ships inside; an external file was timed against whatever release its
            // author had, and that is the single largest source of the desync this
            // plugin exists to find.
            .ThenBy(t => t.Track.IsExternal)
            .ThenBy(t => t.Track.Index)
            .FirstOrDefault();

        /// <summary>Gets whether every preferred language is present and verified.</summary>
        /// <param name="languages">The preferred languages.</param>
        /// <returns>True when each has a track found to be in sync.</returns>
        public bool Covered(IReadOnlyList<string> languages)
        {
            ArgumentNullException.ThrowIfNull(languages);
            return languages.Count > 0 && languages.All(l => Best(l)?.Verdict == Verdict.InSync);
        }

        private static int Tier(Verdict verdict) => verdict switch
        {
            Verdict.InSync => 4,
            Verdict.Offset => 3,
            Verdict.Drifting => 2,
            Verdict.Unknown => 1,
            _ => 0,
        };
    }

    /// <summary>A library-wide tally, for the report tab.</summary>
    /// <param name="Items">How many items have been checked.</param>
    /// <param name="ByVerdict">How many best-tracks landed on each verdict.</param>
    /// <param name="MissingByLanguage">How many items have no usable track in each preferred language.</param>
    /// <param name="AudioMinutes">How much audio has been transcribed in total.</param>
    public sealed record Coverage(
        int Items,
        IReadOnlyDictionary<Verdict, int> ByVerdict,
        IReadOnlyDictionary<string, int> MissingByLanguage,
        double AudioMinutes)
    {
        /// <summary>Builds the tally.</summary>
        /// <param name="reports">Every stored report.</param>
        /// <param name="languages">The preferred languages.</param>
        /// <returns>The coverage.</returns>
        /// <remarks>
        /// Counted on the <em>best</em> track per language rather than on every
        /// track, because that is the one a viewer will actually be shown. An item
        /// holding a perfect English track and three broken ones is covered, and a
        /// tally over all four would report it as 25% working.
        /// </remarks>
        public static Coverage Build(IReadOnlyList<ItemReport> reports, IReadOnlyList<string> languages)
        {
            ArgumentNullException.ThrowIfNull(reports);
            ArgumentNullException.ThrowIfNull(languages);

            var byVerdict = new Dictionary<Verdict, int>();
            var missing = new Dictionary<string, int>(StringComparer.Ordinal);
            var minutes = 0.0;

            foreach (var report in reports)
            {
                minutes += report.AudioSeconds / 60.0;

                foreach (var language in languages)
                {
                    var best = report.Best(language);
                    if (best is null)
                    {
                        missing[language] = missing.GetValueOrDefault(language) + 1;
                        continue;
                    }

                    byVerdict[best.Verdict] = byVerdict.GetValueOrDefault(best.Verdict) + 1;
                }
            }

            return new Coverage(reports.Count, byVerdict, missing, minutes);
        }
    }
}
