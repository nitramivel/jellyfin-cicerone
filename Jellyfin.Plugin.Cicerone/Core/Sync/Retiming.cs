using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Cicerone.Core.Subtitles;

namespace Jellyfin.Plugin.Cicerone.Core.Sync
{
    /// <summary>A stretch of speech a transcriber heard.</summary>
    /// <param name="Start">When it starts, in media time.</param>
    /// <param name="End">When it ends, in media time.</param>
    /// <param name="Text">What was said.</param>
    /// <remarks>
    /// Times are in <em>media</em> time, not window time. Providers return offsets
    /// from the start of the clip they were handed, and shifting them back onto the
    /// item's own clock is the caller's job and must happen exactly once — an anchor
    /// whose transcript was never rebased measures its own window position as the
    /// offset, which is a large, confident, entirely fictitious answer.
    /// </remarks>
    public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text)
    {
        /// <summary>Returns this segment moved onto the media's clock.</summary>
        /// <param name="windowStart">Where the clip began in the media.</param>
        /// <returns>The rebased segment.</returns>
        public TranscriptSegment Rebase(TimeSpan windowStart)
            => new(Start + windowStart, End + windowStart, Text);
    }

    /// <summary>Applies a correction to a subtitle track.</summary>
    /// <remarks>
    /// Deliberately trivial, and deliberately its own file. Everything difficult
    /// about a repair is decided before this runs — whether to repair at all, by how
    /// much, and where the result is allowed to be written. What is left is
    /// arithmetic on two numbers per cue, and keeping it separate is what lets the
    /// test suite prove that a repaired file round-trips to the same text with only
    /// its timings moved.
    /// </remarks>
    public static class Retiming
    {
        /// <summary>Retimes cues, leaving their text exactly as it was.</summary>
        /// <param name="cues">The cues to move, raw and uncleaned.</param>
        /// <param name="correction">The correction to apply.</param>
        /// <returns>The retimed cues, in the order given.</returns>
        public static IReadOnlyList<Cue> Apply(IReadOnlyList<Cue> cues, Correction correction)
        {
            ArgumentNullException.ThrowIfNull(cues);

            var moved = new List<Cue>(cues.Count);
            foreach (var cue in cues)
            {
                moved.Add(cue with
                {
                    Start = correction.Apply(cue.Start),
                    End = correction.Apply(cue.End),
                });
            }

            return moved;
        }

        /// <summary>Collects a transcript's words, timed.</summary>
        /// <param name="segments">The transcript, already on the media's clock.</param>
        /// <returns>Every word with the moment it was heard.</returns>
        public static IReadOnlyList<TimedToken> HeardTokens(IReadOnlyList<TranscriptSegment> segments)
        {
            ArgumentNullException.ThrowIfNull(segments);

            var tokens = new List<TimedToken>();
            foreach (var segment in segments)
            {
                tokens.AddRange(Tokenizer.Spread(segment.Text, segment.Start, segment.End));
            }

            return tokens;
        }
    }
}
