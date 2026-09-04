using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Cicerone.Core.Sync
{
    /// <summary>A time-scale factor that is a known pair of frame rates.</summary>
    /// <param name="From">The rate the subtitles were timed against.</param>
    /// <param name="To">The rate the video actually runs at.</param>
    /// <param name="Scale">The factor, <c>To / From</c> as it appears in a fit.</param>
    public readonly record struct FrameRatePair(double From, double To, double Scale)
    {
        /// <summary>Gets a label for the report: <c>25 → 23.976 fps</c>.</summary>
        public string Label => string.Create(
            CultureInfo.InvariantCulture,
            $"{From:0.###} → {To:0.###} fps");
    }

    /// <summary>
    /// The frame rates subtitles are mistimed between, and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>This is the single most common way a subtitle file is wrong, and the
    /// hardest to see.</b> A file timed against a 25 fps PAL transfer, played over
    /// a 23.976 fps source, is perfect on the first line and four minutes adrift by
    /// the end. Everyone who has nudged a subtitle offset until the opening scene
    /// matched and then found it broken again an hour later has met this.
    /// <para>
    /// Recognising the ratio matters because a fitted slope is a measurement with
    /// error in it and a frame rate pair is exact. 1.04270 is not a number worth
    /// keeping when what it means is 25/23.976 — snapping to the ratio makes the
    /// repair exact for the whole runtime instead of accumulating the fit's residual
    /// error across three hours, and it lets the report name the fault rather than
    /// print a slope.
    /// </para>
    /// </remarks>
    public static class FrameRates
    {
        /// <summary>The rates in circulation. Everything else is a rounding of one of these.</summary>
        public static readonly double[] Known = [23.976, 24.0, 25.0, 29.97, 30.0];

        /// <summary>
        /// How far a fitted scale may sit from a ratio and still be called that ratio.
        /// </summary>
        /// <remarks>
        /// The tightest pair worth telling apart is 24 against 23.976, which differ
        /// by one part in a thousand — two and a half seconds across a feature. The
        /// tolerance has to be well inside that to name the right pair, and five
        /// anchors over an hour and a half measure a slope far better than this, so
        /// it is generous rather than tight.
        /// </remarks>
        public const double SnapTolerance = 0.0002;

        /// <summary>Every ordered pair of distinct known rates.</summary>
        /// <returns>The pairs, nearest-to-unity first.</returns>
        public static IReadOnlyList<FrameRatePair> Pairs()
        {
            var pairs = new List<FrameRatePair>();
            foreach (var from in Known)
            {
                foreach (var to in Known)
                {
                    if (Math.Abs(from - to) < 1e-9)
                    {
                        continue;
                    }

                    pairs.Add(new FrameRatePair(from, to, to / from));
                }
            }

            // 30/25 and 29.97/24.975 are the same factor and only one of them is a
            // pair anyone has ever encoded, so ordering by distance from unity and
            // letting the first match win keeps the plausible reading.
            return pairs.OrderBy(p => Math.Abs(p.Scale - 1.0)).ToList();
        }

        /// <summary>Names a fitted scale, when it is one of the known ratios.</summary>
        /// <param name="scale">The fitted time-scale factor.</param>
        /// <returns>The pair it matches, or null.</returns>
        public static FrameRatePair? Identify(double scale)
        {
            if (!double.IsFinite(scale) || scale <= 0)
            {
                return null;
            }

            foreach (var pair in Pairs())
            {
                if (Math.Abs(pair.Scale - scale) <= SnapTolerance)
                {
                    return pair;
                }
            }

            return null;
        }
    }
}
