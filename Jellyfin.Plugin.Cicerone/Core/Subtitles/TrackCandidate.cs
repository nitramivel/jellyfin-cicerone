using System;
using Jellyfin.Plugin.Cicerone.Core.Language;
using Jellyfin.Plugin.Cicerone.Core.Sync;

namespace Jellyfin.Plugin.Cicerone.Core.Subtitles
{
    /// <summary>
    /// One subtitle track, described without reference to Jellyfin's own types.
    /// </summary>
    /// <param name="Index">The media stream index, which is how it is asked for again.</param>
    /// <param name="Language">The language the track claims, as tagged.</param>
    /// <param name="Title">The track's display title, when it has one.</param>
    /// <param name="IsText">Whether it is text rather than a picture of text.</param>
    /// <param name="IsForced">Whether it is a forced track.</param>
    /// <param name="IsHearingImpaired">Whether it is an SDH track.</param>
    /// <param name="IsExternal">Whether it is a sidecar file rather than a stream in the container.</param>
    /// <param name="IsDefault">Whether the container marks it as the default for its language.</param>
    /// <param name="Path">The sidecar's path, for an external track.</param>
    /// <remarks>
    /// A plain record so that everything deciding which track to use is testable
    /// without a server. The mapping from <c>MediaStream</c> happens once, in
    /// Services, and nothing in Core has ever heard of it.
    /// </remarks>
    public sealed record TrackCandidate(
        int Index,
        string Language,
        string? Title,
        bool IsText,
        bool IsForced,
        bool IsHearingImpaired,
        bool IsExternal,
        bool IsDefault,
        string? Path)
    {
        /// <summary>Gets a short label for the report.</summary>
        public string Label
        {
            get
            {
                var kind = IsExternal ? "external" : "embedded";
                var flags = (IsForced ? ", forced" : string.Empty)
                    + (IsHearingImpaired ? ", SDH" : string.Empty)
                    + (IsText ? string.Empty : ", image");
                return $"#{Index} {LanguageCodes.Name(Language)} ({kind}{flags})";
            }
        }

        /// <summary>Gets whether this track holds enough dialogue to be worth checking.</summary>
        /// <remarks>
        /// <b>Rejecting forced tracks matters more than it looks.</b> A forced track
        /// carries only the lines spoken in another language — a few dozen cues for a
        /// whole film — and checking one looks exactly like success right up to the
        /// point where the verdict is meaningless. Image subtitles are rejected for
        /// the blunter reason that reading them needs OCR, which is a dependency, a
        /// GPU and an error rate that would land in the sync measurement.
        /// </remarks>
        public bool Checkable => IsText && !IsForced;

        /// <summary>Says why a track is not checkable, for the report.</summary>
        /// <returns>The reason, or null when it is checkable.</returns>
        public string? SkipReason()
        {
            if (!IsText)
            {
                return "image subtitles (PGS or VobSub) cannot be read without OCR";
            }

            return IsForced ? "forced track — a few dozen lines, nothing to align against" : null;
        }
    }

    /// <summary>Everything Cicerone concluded about one track.</summary>
    /// <param name="Track">The track.</param>
    /// <param name="CueCount">How many cues survived cleaning.</param>
    /// <param name="DetectedLanguage">What the text reads as, when that could be told.</param>
    /// <param name="LanguageContradicted">Whether the text is confidently not the tagged language.</param>
    /// <param name="Sync">The sync assessment, or null when none was made.</param>
    /// <param name="Skipped">Why the track was not checked, or null.</param>
    public sealed record TrackReport(
        TrackCandidate Track,
        int CueCount,
        LanguageGuess? DetectedLanguage,
        bool LanguageContradicted,
        SyncAssessment? Sync,
        string? Skipped)
    {
        /// <summary>Gets the single verdict for this track.</summary>
        /// <remarks>
        /// Language beats sync, because a Spanish file tagged English is not a sync
        /// problem and reporting it as one sends the owner off to fix the wrong
        /// thing. It is also the one verdict reached without spending anything.
        /// </remarks>
        public Verdict Verdict
        {
            get
            {
                if (Skipped is not null)
                {
                    return Verdict.NoDialogue;
                }

                if (LanguageContradicted)
                {
                    return Verdict.WrongLanguage;
                }

                if (CueCount == 0)
                {
                    return Verdict.NoDialogue;
                }

                return Sync?.Verdict ?? Verdict.Unknown;
            }
        }
    }
}
