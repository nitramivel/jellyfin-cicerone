using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Cicerone.Core.Sync
{
    /// <summary>One word, and when it was said.</summary>
    /// <param name="Word">The normalised word.</param>
    /// <param name="At">Where in the media it falls.</param>
    public readonly record struct TimedToken(string Word, TimeSpan At);

    /// <summary>
    /// Turns a line of text into comparable words.
    /// </summary>
    /// <remarks>
    /// The two sides being compared were written by different hands for different
    /// purposes — a subtitle author typing "I'm gonna" and a speech model writing
    /// "I am going to" — so normalisation has to erase everything that is a spelling
    /// decision rather than a word. Case, punctuation and diacritics all go.
    /// <para>
    /// What deliberately does <em>not</em> happen here is stemming or a stopword
    /// list. Both are per-language, and Cicerone is asked about Japanese and Polish
    /// tracks as readily as English ones. The aligner drops over-frequent words
    /// instead, which is the same idea derived from the text in front of it rather
    /// than from a table that only exists for English.
    /// </para>
    /// </remarks>
    public static class Tokenizer
    {
        /// <summary>Splits a line into normalised words.</summary>
        /// <param name="text">The text.</param>
        /// <returns>The words, in order, lowercased and stripped of accents.</returns>
        public static IReadOnlyList<string> Words(string? text)
        {
            var words = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return words;
            }

            // Decomposing first puts every accent in its own combining character, so
            // dropping non-letters removes them and leaves the base letter behind:
            // "déjà" and "deja" become the same word. CJK has no case and no accents
            // and passes through this untouched.
            var normalised = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(16);

            foreach (var c in normalised)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetter(c) || char.IsDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                    continue;
                }

                // An apostrophe inside a word is a contraction and joins nothing:
                // "don't" and "dont" must be one word, because which of the two a
                // subtitle uses is a house style rather than a difference in speech.
                if (c is '\'' or '’' or '‘' or '´')
                {
                    continue;
                }

                Flush(builder, words);
            }

            Flush(builder, words);
            return words;
        }

        /// <summary>
        /// Spreads a span's words evenly across it.
        /// </summary>
        /// <param name="text">The text spoken during the span.</param>
        /// <param name="start">When the span begins.</param>
        /// <param name="end">When the span ends.</param>
        /// <returns>Each word with the moment it is taken to fall at.</returns>
        /// <remarks>
        /// Both sides of the comparison use this, which is what makes it sound.
        /// Neither a subtitle cue nor a transcript segment says when inside itself a
        /// given word was spoken, and treating a five-second cue as a point puts its
        /// last word two and a half seconds early. Interpolating is still wrong —
        /// people do not speak at a constant rate — but it is wrong in the same
        /// direction on both sides, and the aligner only ever reads the difference.
        /// </remarks>
        public static IReadOnlyList<TimedToken> Spread(string? text, TimeSpan start, TimeSpan end)
        {
            var words = Words(text);
            var timed = new List<TimedToken>(words.Count);
            if (words.Count == 0)
            {
                return timed;
            }

            var span = end - start;
            if (span < TimeSpan.Zero)
            {
                span = TimeSpan.Zero;
            }

            for (var i = 0; i < words.Count; i++)
            {
                // Word centres rather than edges: (i + 0.5) / n, so a single word sits
                // at the middle of its span instead of at the very start of it.
                var fraction = (i + 0.5) / words.Count;
                timed.Add(new TimedToken(words[i], start + (span * fraction)));
            }

            return timed;
        }

        private static void Flush(StringBuilder builder, List<string> words)
        {
            if (builder.Length > 0)
            {
                words.Add(builder.ToString());
                builder.Clear();
            }
        }
    }
}
