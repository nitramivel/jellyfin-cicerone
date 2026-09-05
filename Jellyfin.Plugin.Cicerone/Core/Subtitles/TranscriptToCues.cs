using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Cicerone.Core.Sync;

namespace Jellyfin.Plugin.Cicerone.Core.Subtitles
{
    /// <summary>
    /// Turns what a model heard into a subtitle track somebody can read.
    /// </summary>
    /// <remarks>
    /// <b>A transcript is not subtitles.</b> A speech model returns whatever length of
    /// utterance it felt like ending — sometimes two words, sometimes a forty-second
    /// paragraph spanning three speakers — and putting that on screen unaltered gives
    /// a viewer either a flicker or a wall. What a subtitle has to be is short enough
    /// to read in the time it is up, long enough not to flash, and broken where the
    /// sentence breaks.
    /// <para>
    /// So the segments are re-cut. Long ones are split at sentence ends, then at
    /// clause commas, then on word boundaries as a last resort; short ones are joined
    /// to their neighbour when the gap between them is small enough that they were
    /// plainly one breath. The times are carried through the whole operation
    /// proportionally to the characters, which is the same assumption a transcript
    /// without timings makes and is good to a fraction of a second inside a single
    /// utterance.
    /// </para>
    /// <para>
    /// This is the one part of the plugin that invents a subtitle rather than
    /// measuring one, so it is pure and it is tested. Everything it produces is
    /// written to a file marked as Cicerone's own; nothing here ever touches a track
    /// somebody else wrote.
    /// </para>
    /// </remarks>
    public static class TranscriptToCues
    {
        /// <summary>The longest a single cue may stay on screen.</summary>
        public const double MaxCueSeconds = 7.0;

        /// <summary>The shortest a cue may stay on screen, when there is room.</summary>
        /// <remarks>
        /// Under about a second a line reads as a flash rather than as text. A cue is
        /// extended into the gap that follows it to reach this, never into the next
        /// cue.
        /// </remarks>
        public const double MinCueSeconds = 1.0;

        /// <summary>The most characters one cue may carry.</summary>
        /// <remarks>
        /// Two lines of about forty-two characters, which is the convention every
        /// broadcaster settled on and roughly what fits across a screen at a readable
        /// size.
        /// </remarks>
        public const int MaxCharacters = 84;

        /// <summary>How close two segments must be to be treated as one breath.</summary>
        public const double JoinGapSeconds = 0.35;

        /// <summary>Builds a readable subtitle track from a transcript.</summary>
        /// <param name="segments">What was heard, on the item's clock.</param>
        /// <returns>The cues, in order, never overlapping.</returns>
        public static IReadOnlyList<Cue> Build(IReadOnlyList<TranscriptSegment> segments)
        {
            ArgumentNullException.ThrowIfNull(segments);

            var tidy = segments
                .Where(s => !string.IsNullOrWhiteSpace(s.Text))
                .OrderBy(s => s.Start)
                .ToList();

            if (tidy.Count == 0)
            {
                return [];
            }

            var joined = Join(tidy);

            var cues = new List<Cue>(joined.Count);
            foreach (var segment in joined)
            {
                cues.AddRange(Split(segment));
            }

            return Tidy(cues);
        }

        /// <summary>Wraps a line so it reads as subtitles rather than as a paragraph.</summary>
        /// <param name="text">The line.</param>
        /// <returns>The text, broken over at most two lines.</returns>
        /// <remarks>
        /// Broken as near the middle as a word boundary allows. A subtitle split into
        /// a long line and a short one reads worse than one split evenly, because the
        /// eye travels the width of the longest line either way.
        /// </remarks>
        public static string Wrap(string text)
        {
            var line = Collapse(text);
            if (line.Length <= MaxCharacters / 2)
            {
                return line;
            }

            var target = line.Length / 2;
            var best = -1;

            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == ' ' && (best < 0 || Math.Abs(i - target) < Math.Abs(best - target)))
                {
                    best = i;
                }
            }

            return best <= 0 ? line : line[..best] + "\n" + line[(best + 1)..];
        }

        /// <summary>Joins segments the model split mid-breath.</summary>
        private static List<TranscriptSegment> Join(List<TranscriptSegment> segments)
        {
            var joined = new List<TranscriptSegment>();

            foreach (var segment in segments)
            {
                var text = Collapse(segment.Text);
                if (text.Length == 0)
                {
                    continue;
                }

                var current = new TranscriptSegment(segment.Start, segment.End, text);

                if (joined.Count == 0)
                {
                    joined.Add(current);
                    continue;
                }

                var previous = joined[^1];
                var gap = (current.Start - previous.End).TotalSeconds;
                var combined = previous.Text.Length + 1 + current.Text.Length;

                // Only joined when all three agree it was one utterance: the gap is
                // short, the result still fits on screen, and the previous line did not
                // already end in a full stop.
                if (gap <= JoinGapSeconds
                    && combined <= MaxCharacters
                    && (current.End - previous.Start).TotalSeconds <= MaxCueSeconds
                    && !EndsSentence(previous.Text))
                {
                    joined[^1] = new TranscriptSegment(
                        previous.Start, current.End, previous.Text + " " + current.Text);
                    continue;
                }

                joined.Add(current);
            }

            return joined;
        }

        /// <summary>Cuts a segment too long or too wordy to be one cue.</summary>
        private static IEnumerable<Cue> Split(TranscriptSegment segment)
        {
            var length = (segment.End - segment.Start).TotalSeconds;

            if (segment.Text.Length <= MaxCharacters && length <= MaxCueSeconds)
            {
                yield return new Cue(segment.Start, segment.End, Wrap(segment.Text));
                yield break;
            }

            var pieces = Pieces(segment.Text);
            var characters = pieces.Sum(p => p.Length);
            if (characters == 0)
            {
                yield break;
            }

            // Time is handed out in proportion to the characters. Inside one utterance
            // that is a good approximation — speaking rate is near enough constant over
            // a few seconds — and it is the only information available, since the model
            // timed the utterance and not the words in it.
            var at = segment.Start;
            foreach (var piece in pieces)
            {
                var share = TimeSpan.FromSeconds(length * piece.Length / characters);
                yield return new Cue(at, at + share, Wrap(piece));
                at += share;
            }
        }

        /// <summary>Breaks a long line at the best place available.</summary>
        private static List<string> Pieces(string text)
        {
            var sentences = new List<string>();
            var builder = new StringBuilder();

            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                builder.Append(builder.Length > 0 ? " " : string.Empty).Append(word);

                // Sentence ends first, then clause commas, then sheer length. Breaking
                // where the writer broke reads as subtitles; breaking every eighty-four
                // characters reads as a teleprinter.
                var full = builder.Length >= MaxCharacters
                    || EndsSentence(word)
                    || (word.EndsWith(",", StringComparison.Ordinal) && builder.Length > MaxCharacters / 2);

                if (full)
                {
                    sentences.Add(builder.ToString());
                    builder.Clear();
                }
            }

            if (builder.Length > 0)
            {
                sentences.Add(builder.ToString());
            }

            return sentences;
        }

        /// <summary>Puts the finished cues in order and stops them overlapping.</summary>
        private static IReadOnlyList<Cue> Tidy(List<Cue> cues)
        {
            var tidy = new List<Cue>(cues.Count);

            foreach (var cue in cues.OrderBy(c => c.Start))
            {
                if (string.IsNullOrWhiteSpace(cue.Text))
                {
                    continue;
                }

                var start = cue.Start < TimeSpan.Zero ? TimeSpan.Zero : cue.Start;
                var end = cue.End;

                if (tidy.Count > 0 && start < tidy[^1].End)
                {
                    // Two cues on screen at once is a player's decision to make badly.
                    // The later one wins its start; the earlier one is cut short.
                    var previous = tidy[^1];
                    tidy[^1] = previous with { End = start < previous.Start ? previous.Start : start };
                }

                if (end - start < TimeSpan.FromSeconds(MinCueSeconds))
                {
                    end = start + TimeSpan.FromSeconds(MinCueSeconds);
                }

                if (end - start > TimeSpan.FromSeconds(MaxCueSeconds))
                {
                    end = start + TimeSpan.FromSeconds(MaxCueSeconds);
                }

                tidy.Add(new Cue(start, end, cue.Text));
            }

            // A cue extended to reach the minimum may now run into the next one, so the
            // overlap pass is done once more over the finished list.
            for (var i = 0; i < tidy.Count - 1; i++)
            {
                if (tidy[i].End > tidy[i + 1].Start)
                {
                    tidy[i] = tidy[i] with { End = tidy[i + 1].Start };
                }
            }

            return tidy.Where(c => c.End > c.Start).ToList();
        }

        private static bool EndsSentence(string text) =>
            text.Length > 0 && text[^1] is '.' or '!' or '?' or '。' or '！' or '？';

        private static string Collapse(string? text) => string.Join(
            ' ',
            (text ?? string.Empty).Split(
                [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
