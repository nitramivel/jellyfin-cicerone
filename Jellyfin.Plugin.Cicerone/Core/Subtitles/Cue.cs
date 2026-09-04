using System;

namespace Jellyfin.Plugin.Cicerone.Core.Subtitles
{
    /// <summary>One subtitle cue, as it appeared in the file.</summary>
    /// <param name="Start">When it appears.</param>
    /// <param name="End">When it goes away.</param>
    /// <param name="Text">The raw text, newlines collapsed to spaces.</param>
    public sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

    /// <summary>One cue after cleaning, keeping both forms.</summary>
    /// <param name="Start">When it appears.</param>
    /// <param name="End">When it goes away.</param>
    /// <param name="Text">The cleaned dialogue, for matching against what was said.</param>
    /// <param name="Raw">The line as it appeared, for rewriting the file.</param>
    /// <remarks>
    /// Both, because Cicerone does two different things with a cue and they want
    /// different text. Alignment compares words against a transcript, where
    /// <c>[door creaks]</c> is noise the microphone never heard. A repair rewrites
    /// the file, where that annotation is somebody's subtitle and must come out the
    /// other side byte-identical. Cleaning is a lens, never an edit.
    /// </remarks>
    public sealed record CleanCue(TimeSpan Start, TimeSpan End, string Text, string Raw)
    {
        /// <summary>Gets the midpoint of the cue.</summary>
        /// <remarks>
        /// The single time a cue is reduced to when it is treated as a point rather
        /// than a span. The midpoint rather than the start because a cue's start is
        /// where the subtitle appears, which subtitle authors habitually pull a beat
        /// early so the reader is not chasing the line — the words are said nearer
        /// the middle.
        /// </remarks>
        public TimeSpan Middle => Start + ((End - Start) / 2);
    }
}
