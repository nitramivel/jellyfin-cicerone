using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Cicerone.Core.Language;
using Jellyfin.Plugin.Cicerone.Core.Sync;

namespace Jellyfin.Plugin.Cicerone.Core.Subtitles
{
    /// <summary>Where a subtitle track came from.</summary>
    public enum SourceKind
    {
        /// <summary>A stream inside the media container.</summary>
        Embedded = 0,

        /// <summary>A sidecar file somebody else put there.</summary>
        External = 1,

        /// <summary>A retimed copy Cicerone wrote.</summary>
        Repaired = 2,

        /// <summary>A track Cicerone wrote by listening to the film.</summary>
        Heard = 3,
    }

    /// <summary>
    /// One subtitle track an item has, wherever it came from.
    /// </summary>
    /// <param name="Id">
    /// How this track is named to the API. Stable across a page refresh and safe in a
    /// URL, which rules out the path.
    /// </param>
    /// <param name="Kind">Where it came from.</param>
    /// <param name="StreamIndex">Its index in the container, for an embedded track.</param>
    /// <param name="Language">The language it claims.</param>
    /// <param name="Title">Its title, when it has one.</param>
    /// <param name="FileName">The file's name, for a sidecar.</param>
    /// <param name="Path">Where the file is, for a sidecar.</param>
    /// <param name="SizeBytes">How big the file is.</param>
    /// <param name="ModifiedUtc">When the file last changed.</param>
    /// <param name="IsText">Whether it is text rather than a picture of text.</param>
    /// <param name="IsForced">Whether it is a forced track.</param>
    /// <param name="IsHearingImpaired">Whether it is an SDH track.</param>
    /// <param name="IsDefault">Whether the container marks it default.</param>
    /// <param name="CueCount">How many cues it holds, when that has been read.</param>
    /// <param name="Verdict">What Cicerone last concluded about it.</param>
    /// <param name="Detail">The one-line account behind that verdict.</param>
    /// <param name="CheckedUtc">When that conclusion was reached.</param>
    public sealed record SubtitleSource(
        string Id,
        SourceKind Kind,
        int? StreamIndex,
        string Language,
        string? Title,
        string? FileName,
        string? Path,
        long SizeBytes,
        DateTime? ModifiedUtc,
        bool IsText,
        bool IsForced,
        bool IsHearingImpaired,
        bool IsDefault,
        int? CueCount,
        Verdict? Verdict,
        string? Detail,
        DateTime? CheckedUtc)
    {
        /// <summary>Gets whether Cicerone wrote this file itself.</summary>
        /// <remarks>
        /// The line that governs what the manager is allowed to do without asking
        /// twice. Cicerone's own output can be replaced or removed freely, because
        /// putting it back costs a re-run. Anything else is somebody's work.
        /// </remarks>
        public bool Ours => Kind is SourceKind.Repaired or SourceKind.Heard;

        /// <summary>Gets whether this track lives in a file that can be rewritten.</summary>
        public bool IsFile => !string.IsNullOrEmpty(Path);

        /// <summary>Gets a short description for the manager.</summary>
        public string Label
        {
            get
            {
                var flags = new List<string>();
                if (IsForced)
                {
                    flags.Add("forced");
                }

                if (IsHearingImpaired)
                {
                    flags.Add("SDH");
                }

                if (!IsText)
                {
                    flags.Add("image");
                }

                if (IsDefault)
                {
                    flags.Add("default");
                }

                var suffix = flags.Count == 0 ? string.Empty : " (" + string.Join(", ", flags) + ")";
                return LanguageCodes.Name(Language) + suffix;
            }
        }
    }

    /// <summary>
    /// Works out what a subtitle file beside a film actually is.
    /// </summary>
    /// <remarks>
    /// Kept pure and separate because the answer decides what the manager will let
    /// somebody do to the file, and getting it wrong in the permissive direction means
    /// offering to overwrite a track that took an evening to time by hand.
    /// <b>Nothing here will ever overwrite a file Cicerone did not write</b>, and this
    /// is where that is decided.
    /// </remarks>
    public static class SidecarNaming
    {
        /// <summary>The extensions Jellyfin will load as a sidecar.</summary>
        public static readonly string[] Extensions =
            [".srt", ".ass", ".ssa", ".vtt", ".sub", ".sup", ".idx", ".smi"];

        /// <summary>The ones Cicerone can read, rewrite and edit as text.</summary>
        public static readonly string[] Editable = [".srt", ".vtt", ".ass", ".ssa"];

        /// <summary>Decides what a sidecar is from its name.</summary>
        /// <param name="fileName">The file's name.</param>
        /// <param name="repairSuffix">The marker a repaired file carries.</param>
        /// <param name="heardSuffix">The marker a transcribed file carries.</param>
        /// <returns>What kind of source it is.</returns>
        public static SourceKind Classify(string fileName, string repairSuffix, string heardSuffix)
        {
            ArgumentNullException.ThrowIfNull(fileName);

            // The transcript marker is tested first and deliberately: it contains the
            // repair marker as a prefix by default, so testing the shorter one first
            // would file every transcript as a repair.
            if (HasToken(fileName, heardSuffix))
            {
                return SourceKind.Heard;
            }

            return HasToken(fileName, repairSuffix) ? SourceKind.Repaired : SourceKind.External;
        }

        /// <summary>Whether a file is one Cicerone may rewrite or remove without ceremony.</summary>
        /// <param name="fileName">The file's name.</param>
        /// <param name="repairSuffix">The marker a repaired file carries.</param>
        /// <param name="heardSuffix">The marker a transcribed file carries.</param>
        /// <returns>True only for Cicerone's own output.</returns>
        public static bool IsOurs(string fileName, string repairSuffix, string heardSuffix) =>
            Classify(fileName, repairSuffix, heardSuffix) is SourceKind.Repaired or SourceKind.Heard;

        /// <summary>Whether a name is a subtitle belonging to a given media file.</summary>
        /// <param name="fileName">The candidate file's name.</param>
        /// <param name="mediaFileName">The media file's name.</param>
        /// <returns>True when Jellyfin would associate the two.</returns>
        /// <remarks>
        /// Jellyfin pairs a sidecar to a film by the stem of the name, with everything
        /// after it read as language and flags. The same rule is applied here so the
        /// manager lists exactly the files the server will, plus the ones Cicerone has
        /// only just written and the scanner has not seen yet.
        /// </remarks>
        public static bool BelongsTo(string fileName, string mediaFileName)
        {
            ArgumentNullException.ThrowIfNull(fileName);
            ArgumentNullException.ThrowIfNull(mediaFileName);

            var extension = System.IO.Path.GetExtension(fileName);
            if (!Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            var stem = System.IO.Path.GetFileNameWithoutExtension(mediaFileName);
            if (stem.Length == 0)
            {
                return false;
            }

            var name = System.IO.Path.GetFileNameWithoutExtension(fileName);

            return name.Equals(stem, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Reads the language a sidecar's name claims.</summary>
        /// <param name="fileName">The file's name.</param>
        /// <param name="mediaFileName">The media file's name.</param>
        /// <returns>The two-letter code, or empty when the name does not say.</returns>
        public static string LanguageOf(string fileName, string mediaFileName)
        {
            ArgumentNullException.ThrowIfNull(fileName);
            ArgumentNullException.ThrowIfNull(mediaFileName);

            var stem = System.IO.Path.GetFileNameWithoutExtension(mediaFileName);
            var name = System.IO.Path.GetFileNameWithoutExtension(fileName);

            if (name.Length <= stem.Length || !name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            foreach (var token in name[(stem.Length + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var code = LanguageCodes.Normalize(token);
                if (code.Length > 0)
                {
                    return code;
                }
            }

            return string.Empty;
        }

        /// <summary>Turns a path into something safe to put in a URL.</summary>
        /// <param name="path">The file's path.</param>
        /// <returns>The token.</returns>
        /// <remarks>
        /// A hash rather than the path itself. A path in a URL has to survive slashes,
        /// spaces, colons and non-ASCII on three operating systems, and — more to the
        /// point — an endpoint that accepts a path accepts <em>any</em> path. The
        /// manager looks a token up against the files it has just listed, so the only
        /// files reachable through the API are the ones that were already on offer.
        /// </remarks>
        public static string Token(string path)
        {
            ArgumentNullException.ThrowIfNull(path);

            var hash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(path));

            return "file_" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }

        private static bool HasToken(string fileName, string suffix)
        {
            var marker = (suffix ?? string.Empty).Trim().Trim('.');
            if (marker.Length == 0)
            {
                return false;
            }

            var name = System.IO.Path.GetFileNameWithoutExtension(fileName);

            return name.Split('.', StringSplitOptions.RemoveEmptyEntries)
                .Any(token => token.Equals(marker, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Everything an item has by way of subtitles.</summary>
    /// <param name="ItemId">The item.</param>
    /// <param name="Name">Its name.</param>
    /// <param name="SeriesName">Its series, for an episode.</param>
    /// <param name="MediaPath">Where the media file is.</param>
    /// <param name="RuntimeSeconds">How long it runs.</param>
    /// <param name="Sources">Every track, embedded and beside it.</param>
    /// <param name="AudioLanguages">The languages the item has audio in.</param>
    /// <param name="PreferredLanguages">The languages the owner asked for.</param>
    /// <param name="CanWriteBeside">Whether the media's own folder is writable.</param>
    public sealed record SubtitleInventory(
        Guid ItemId,
        string Name,
        string? SeriesName,
        string? MediaPath,
        double RuntimeSeconds,
        IReadOnlyList<SubtitleSource> Sources,
        IReadOnlyList<string> AudioLanguages,
        IReadOnlyList<string> PreferredLanguages,
        bool CanWriteBeside)
    {
        /// <summary>Gets the preferred languages with no readable track at all.</summary>
        /// <remarks>
        /// What the offer to transcribe is made on. An image-only track does not count
        /// as having the language, because nothing can read it without OCR — which is
        /// exactly the case where hearing the film is the only way to get a track.
        /// </remarks>
        public IReadOnlyList<string> Missing => PreferredLanguages
            .Where(l => !Sources.Any(s => s.IsText && !s.IsForced && LanguageCodes.Same(s.Language, l)))
            .ToList();

        /// <summary>Gets how much of the item's audio a transcription would cost, in minutes.</summary>
        public double TranscriptionMinutes => Math.Round(RuntimeSeconds / 60.0, 1);

        /// <summary>Renders the size of a file the way the manager shows it.</summary>
        /// <param name="bytes">The size.</param>
        /// <returns>Something like <c>84 KB</c>.</returns>
        public static string Size(long bytes) => bytes switch
        {
            < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
            < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
        };
    }
}
