using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Cicerone.Core.Subtitles
{
    /// <summary>
    /// Strips everything in a subtitle cue that nobody said out loud.
    /// </summary>
    /// <remarks>
    /// Alignment compares a subtitle track against a transcript of the audio, so the
    /// only text worth keeping is text a microphone could have picked up. SDH
    /// annotations — <c>[door creaks]</c>, <c>(SIRENS WAILING)</c>, <c>♪ tense
    /// music ♪</c> — are descriptions of sound rather than sound, and they are
    /// exactly the lines a transcript will never contain. Left in, a hearing-impaired
    /// track scores systematically worse than a clean one against identical audio,
    /// and Cicerone would report a perfectly good file as a different cut.
    /// <para>
    /// Cleaning never touches the file. <see cref="CleanCue.Raw"/> carries the
    /// original through, and a repair rewrites from that.
    /// </para>
    /// </remarks>
    public static partial class CueCleaner
    {
        [GeneratedRegex(@"<[^>]*>", RegexOptions.None, 200)]
        private static partial Regex HtmlTag();

        /// <summary>ASS override blocks: {\an8}, {\pos(190,270)}.</summary>
        [GeneratedRegex(@"\{[^}]*\}", RegexOptions.None, 200)]
        private static partial Regex AssOverride();

        /// <summary>SDH annotations in brackets or parentheses.</summary>
        [GeneratedRegex(@"[\[(][^\])]*[\])]", RegexOptions.None, 200)]
        private static partial Regex Annotation();

        /// <summary>A speaker prefix in caps: "VINCENT:", "MAN ON TV:".</summary>
        [GeneratedRegex(@"^\s*[-–—]?\s*[\p{Lu}][\p{Lu}\p{Nd} .'#\-]{1,24}:\s*", RegexOptions.None, 200)]
        private static partial Regex SpeakerPrefix();

        [GeneratedRegex(@"\s{2,}", RegexOptions.None, 200)]
        private static partial Regex Whitespace();

        /// <summary>
        /// Phrases marking a cue as the subtitle author rather than the film.
        /// </summary>
        /// <remarks>
        /// These cluster at the head and tail of ripped files, which is where the
        /// first and last anchors would otherwise want to sit. A window scored
        /// against "Subtitles by explosiveskull" measures the ripper's signature, not
        /// the film's sync.
        /// </remarks>
        private static readonly string[] CreditMarkers =
        [
            "opensubtitles", "subscene", "addic7ed", "yify", "subtitles by",
            "subtitled by", "sync by", "synced by", "corrected by", "resync",
            "translated by", "www.", "http://", "https://", "@gmail", "subs by",
            "encoded by", "ripped by", "explosiveskull",
        ];

        /// <summary>
        /// Cleans a whole track: strips markup and annotations, drops credits and
        /// consecutive duplicates, and discards anything left with no speech in it.
        /// </summary>
        /// <param name="cues">The parsed cues.</param>
        /// <returns>The spoken lines, in order, each keeping its original text.</returns>
        public static IReadOnlyList<CleanCue> Clean(IReadOnlyList<Cue> cues)
        {
            ArgumentNullException.ThrowIfNull(cues);

            var cleaned = new List<CleanCue>(cues.Count);
            string? previous = null;

            foreach (var cue in cues)
            {
                var text = CleanLine(cue.Text);
                if (text.Length == 0)
                {
                    continue;
                }

                // Rips routinely repeat a line across several timings. Kept, the
                // duplicate votes twice for its own offset and drags the peak.
                if (string.Equals(text, previous, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                previous = text;
                cleaned.Add(new CleanCue(cue.Start, cue.End, text, cue.Text));
            }

            return cleaned;
        }

        /// <summary>Cleans one line of subtitle text.</summary>
        /// <param name="raw">The raw text.</param>
        /// <returns>What was spoken, or empty when the line was not speech at all.</returns>
        public static string CleanLine(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var text = raw;

            if (IsCredits(text))
            {
                return string.Empty;
            }

            text = HtmlTag().Replace(text, " ");
            text = AssOverride().Replace(text, " ");
            text = Annotation().Replace(text, " ");

            // Musical notes wrap two different things and only one of them is sound
            // nobody uttered. "♪ ominous music ♪" is a description; "♪ Let it go ♪"
            // is a line somebody sings, and a transcript will contain it. So the
            // markers always come off and the content only goes when it reads as a
            // description of the music rather than the words of it.
            if (IsMusicDescription(text))
            {
                return string.Empty;
            }

            text = text.Replace("♪", " ", StringComparison.Ordinal)
                .Replace("♫", " ", StringComparison.Ordinal);

            text = SpeakerPrefix().Replace(text, string.Empty);

            // Two speakers in one cue arrive as "- Line one\n- Line two"; the dashes
            // are turn markers, not punctuation, and the newline is already a space.
            text = text.TrimStart('-', '–', '—', ' ');

            text = Whitespace().Replace(text, " ").Trim();

            // Anything left that is only punctuation is a cue that held nothing but
            // an annotation.
            return HasLetter(text) ? text : string.Empty;
        }

        private static bool IsCredits(string text)
        {
            foreach (var marker in CreditMarkers)
            {
                if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Decides whether a musical cue describes the music or quotes it.
        /// </summary>
        /// <remarks>
        /// The test is the word "music" or an instrument, plus brevity. Lyrics are
        /// lines of a song and run on; a description is two or three words and names
        /// the thing making the sound.
        /// </remarks>
        private static bool IsMusicDescription(string text)
        {
            var inner = text.Replace("♪", " ", StringComparison.Ordinal)
                .Replace("♫", " ", StringComparison.Ordinal)
                .Trim();

            if (inner.Length == 0 || inner.Length > 40)
            {
                return false;
            }

            foreach (var word in (string[])["music", "theme", "song plays", "instrumental", "singing", "humming"])
            {
                if (inner.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasLetter(string text)
        {
            foreach (var c in text)
            {
                if (char.IsLetter(c))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
