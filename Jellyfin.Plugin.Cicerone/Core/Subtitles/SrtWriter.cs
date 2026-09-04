using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Cicerone.Core.Subtitles
{
    /// <summary>
    /// Writes cues back out as SRT.
    /// </summary>
    /// <remarks>
    /// Only ever used to emit a <em>repaired copy</em>. Cicerone does not edit the
    /// track it read — a subtitle file is somebody's work and a bad measurement must
    /// not be able to destroy it — so the output of this class always lands at a new
    /// path beside the original.
    /// <para>
    /// Cues are written from <see cref="CleanCue.Raw"/>, so annotations, italics and
    /// speaker names survive a repair untouched. The only thing a repair changes is
    /// the two numbers on the timing line.
    /// </para>
    /// </remarks>
    public static class SrtWriter
    {
        /// <summary>Renders cues as an SRT document.</summary>
        /// <param name="cues">The cues, which are sorted by start time before writing.</param>
        /// <returns>The file's text, with CRLF line endings.</returns>
        public static string Write(IReadOnlyList<Cue> cues)
        {
            ArgumentNullException.ThrowIfNull(cues);

            // Retiming cannot reorder cues — it is monotonic — but a source file
            // whose cues were already out of order would carry that through, and a
            // player reading SRT sequentially stops at the first backward jump.
            var ordered = new List<Cue>(cues);
            ordered.Sort((a, b) => a.Start.CompareTo(b.Start));

            var builder = new StringBuilder(ordered.Count * 96);
            var number = 1;

            foreach (var cue in ordered)
            {
                // A correction can push the opening cues of a track before zero.
                // Clamping rather than dropping them: the line is still said, and a
                // player showing it a moment early is a smaller error than a film
                // whose first exchange has silently gone missing.
                var start = cue.Start < TimeSpan.Zero ? TimeSpan.Zero : cue.Start;
                var end = cue.End < start ? start : cue.End;

                builder.Append(number.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
                    .Append(Stamp(start)).Append(" --> ").Append(Stamp(end)).Append("\r\n")
                    .Append(cue.Text.Replace("\n", "\r\n", StringComparison.Ordinal)).Append("\r\n\r\n");

                number++;
            }

            return builder.ToString();
        }

        /// <summary>Formats a timestamp as <c>HH:MM:SS,mmm</c>.</summary>
        /// <param name="time">The time.</param>
        /// <returns>The SRT spelling.</returns>
        public static string Stamp(TimeSpan time)
        {
            // Hours are not clamped to two digits by the format: a 100-hour subtitle
            // is nonsense, but truncating it to 04 would move a line by four days
            // rather than reporting anything, and SRT readers accept the extra digit.
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}");
        }
    }
}
