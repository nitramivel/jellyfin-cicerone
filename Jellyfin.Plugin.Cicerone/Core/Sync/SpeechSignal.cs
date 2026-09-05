using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Cicerone.Core.Audio;
using Jellyfin.Plugin.Cicerone.Core.Subtitles;

namespace Jellyfin.Plugin.Cicerone.Core.Sync
{
    /// <summary>
    /// When somebody was speaking, as a row of equal-length bins.
    /// </summary>
    /// <remarks>
    /// <b>Both sides of the comparison reduce to this same shape, and that is the
    /// whole trick.</b> The audio gives one — the stretches ffmpeg found that are not
    /// silence — and the subtitle file gives another, because a cue is a claim that
    /// somebody is speaking from its start to its end. Two rows of the same kind can
    /// be slid over one another until they agree, and where they agree is the answer,
    /// without a single word having been read on either side.
    /// <para>
    /// Binned rather than kept as spans because sliding is what happens next, and
    /// sliding an array by an integer is a subtraction where sliding a list of
    /// intervals is a merge. The bin size sets the resolution of the answer: a fifth
    /// of a second is enough to find the peak, a fiftieth is what measures it.
    /// </para>
    /// </remarks>
    public sealed class SpeechSignal
    {
        private SpeechSignal(bool[] bins, double binSeconds, int active)
        {
            Bins = bins;
            BinSeconds = binSeconds;
            Active = active;
            FirstActive = Array.IndexOf(bins, true);
            LastActive = Array.LastIndexOf(bins, true);
        }

        /// <summary>Gets how long one bin is, in seconds.</summary>
        public double BinSeconds { get; }

        /// <summary>Gets the bins. True where somebody is speaking.</summary>
        public bool[] Bins { get; }

        /// <summary>Gets how many bins there are.</summary>
        public int Count => Bins.Length;

        /// <summary>Gets how many bins hold speech.</summary>
        public int Active { get; }

        /// <summary>Gets the first bin holding speech, or -1 when there are none.</summary>
        public int FirstActive { get; }

        /// <summary>Gets the last bin holding speech, or -1 when there are none.</summary>
        public int LastActive { get; }

        /// <summary>
        /// Gets the share of the signal that is speech.
        /// </summary>
        /// <remarks>
        /// The signal's own account of whether it is worth correlating. Ordinary
        /// dialogue fills something like a third to a half of a film. A signal that is
        /// almost all speech carries no shape to align — every position looks like
        /// every other one — and so does a signal that is almost all silence. Both are
        /// refused rather than measured, because a correlation over a shapeless signal
        /// still returns a peak and it is noise.
        /// </remarks>
        public double Coverage => Count == 0 ? 0 : (double)Active / Count;

        /// <summary>
        /// Gets the share of speech between the first and last thing said.
        /// </summary>
        /// <remarks>
        /// <b>The figure to judge a signal by, because it does not move when the
        /// signal is padded.</b> Both signals are laid out on a timeline long enough
        /// to hold either of them however wrong the track is, so a subtitle file that
        /// is five minutes late has five minutes of dead bins in front of it and the
        /// audio has dead bins after it. Measured over the whole length, that dead
        /// space silently dilutes the density of both — and two tracks can then look
        /// alike for no better reason than being padded to the same length.
        /// </remarks>
        public double ContentCoverage
        {
            get
            {
                if (FirstActive < 0)
                {
                    return 0;
                }

                var span = LastActive - FirstActive + 1;
                return span <= 0 ? 0 : (double)Active / span;
            }
        }

        /// <summary>Builds a signal from stretches of speech found in the audio.</summary>
        /// <param name="spans">The stretches.</param>
        /// <param name="duration">How long the whole signal runs.</param>
        /// <param name="binSeconds">How long one bin is.</param>
        /// <returns>The signal.</returns>
        public static SpeechSignal FromSpans(
            IReadOnlyList<SpeechSpan> spans,
            TimeSpan duration,
            double binSeconds)
        {
            ArgumentNullException.ThrowIfNull(spans);

            var bins = Allocate(duration, binSeconds, out var active);
            foreach (var span in spans)
            {
                active += Mark(bins, span.Start.TotalSeconds, span.End.TotalSeconds, binSeconds);
            }

            return new SpeechSignal(bins, binSeconds, active);
        }

        /// <summary>Builds a signal from a subtitle track's cues.</summary>
        /// <param name="cues">The cleaned cues.</param>
        /// <param name="duration">How long the whole signal runs.</param>
        /// <param name="binSeconds">How long one bin is.</param>
        /// <param name="shiftSeconds">A constant added to every cue time first.</param>
        /// <param name="scale">
        /// A factor every cue time is divided by first, to test the hypothesis that
        /// the track was timed against a different frame rate.
        /// </param>
        /// <returns>The signal.</returns>
        /// <remarks>
        /// The shift and the scale are applied here rather than by moving the signal
        /// afterwards, because a scale is not a shift: stretching a binned array
        /// resamples it and loses the edges that carry all the information. Mapping
        /// the cue times and then binning keeps every edge exact.
        /// </remarks>
        public static SpeechSignal FromCues(
            IReadOnlyList<CleanCue> cues,
            TimeSpan duration,
            double binSeconds,
            double shiftSeconds = 0,
            double scale = 1.0)
        {
            ArgumentNullException.ThrowIfNull(cues);

            var bins = Allocate(duration, binSeconds, out var active);
            var divisor = scale > 0 && double.IsFinite(scale) ? scale : 1.0;

            foreach (var cue in cues)
            {
                var start = (cue.Start.TotalSeconds + shiftSeconds) / divisor;
                var end = (cue.End.TotalSeconds + shiftSeconds) / divisor;
                active += Mark(bins, start, end, binSeconds);
            }

            return new SpeechSignal(bins, binSeconds, active);
        }

        /// <summary>Takes the part of the signal covering one stretch of time.</summary>
        /// <param name="from">Where the slice begins.</param>
        /// <param name="to">Where it ends.</param>
        /// <returns>The slice, on its own clock starting at zero.</returns>
        public SpeechSignal Slice(double from, double to)
        {
            var first = Math.Clamp((int)Math.Floor(from / BinSeconds), 0, Count);
            var last = Math.Clamp((int)Math.Ceiling(to / BinSeconds), first, Count);

            var bins = new bool[last - first];
            var active = 0;

            for (var i = 0; i < bins.Length; i++)
            {
                bins[i] = Bins[first + i];
                if (bins[i])
                {
                    active++;
                }
            }

            return new SpeechSignal(bins, BinSeconds, active);
        }

        private static bool[] Allocate(TimeSpan duration, double binSeconds, out int active)
        {
            active = 0;

            if (binSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(binSeconds), "a bin must have a length");
            }

            var count = (int)Math.Ceiling(Math.Max(duration.TotalSeconds, 0) / binSeconds);
            return new bool[Math.Max(count, 0)];
        }

        private static int Mark(bool[] bins, double startSeconds, double endSeconds, double binSeconds)
        {
            if (bins.Length == 0 || !double.IsFinite(startSeconds) || !double.IsFinite(endSeconds))
            {
                return 0;
            }

            var first = (int)Math.Floor(Math.Max(startSeconds, 0) / binSeconds);
            var last = (int)Math.Ceiling(Math.Max(endSeconds, 0) / binSeconds);

            if (last <= first)
            {
                // A cue shorter than a bin still says somebody spoke. Rounding it away
                // would quietly delete the shortest lines in the file, which are the
                // interjections, which are exactly the sharp edges worth aligning on.
                last = first + 1;
            }

            first = Math.Max(first, 0);
            last = Math.Min(last, bins.Length);

            var added = 0;
            for (var i = first; i < last; i++)
            {
                if (!bins[i])
                {
                    bins[i] = true;
                    added++;
                }
            }

            return added;
        }
    }
}
