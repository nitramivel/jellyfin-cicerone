using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Cicerone.Core.Sync
{
    /// <summary>One anchor's finished measurement, ready to be fitted.</summary>
    /// <param name="At">Where in the media the window sat, in audio time.</param>
    /// <param name="Offset">How far the subtitles were from the audio there.</param>
    /// <param name="Confidence">How much that measurement is worth, 0 to 1.</param>
    public readonly record struct AnchorPoint(TimeSpan At, TimeSpan Offset, double Confidence);

    /// <summary>
    /// The correction that turns subtitle time into media time.
    /// </summary>
    /// <param name="OffsetSeconds">The constant part, in seconds.</param>
    /// <param name="Scale">
    /// The time-scale factor. 1.0 means the subtitles run at the right speed and only
    /// need shifting.
    /// </param>
    /// <param name="ResidualSeconds">
    /// How far the anchors sit from the fitted line, RMS. This is the number that
    /// says whether a straight line was the right thing to fit at all.
    /// </param>
    /// <param name="Points">How many anchors went in.</param>
    /// <param name="FrameRate">The frame rate pair <paramref name="Scale"/> was snapped to, when it is one.</param>
    public readonly record struct Correction(
        double OffsetSeconds,
        double Scale,
        double ResidualSeconds,
        int Points,
        FrameRatePair? FrameRate)
    {
        /// <summary>Gets the identity correction — subtitles already correct.</summary>
        public static Correction None => new(0, 1, 0, 0, null);

        /// <summary>Gets whether this correction would move anything.</summary>
        public bool IsIdentity => Math.Abs(OffsetSeconds) < 1e-6 && Math.Abs(Scale - 1.0) < 1e-9;

        /// <summary>Maps a subtitle time to where it belongs in the media.</summary>
        /// <param name="subtitleTime">The time in the subtitle file.</param>
        /// <returns>The corrected time.</returns>
        /// <remarks>
        /// The inverse of the model that was fitted. Anchors measure
        /// <c>subtitle − audio</c> against audio time, giving
        /// <c>subtitle = offset + scale × audio</c>; a repair has only the subtitle
        /// time in hand and needs the audio time, so it divides rather than
        /// multiplying.
        /// </remarks>
        public TimeSpan Apply(TimeSpan subtitleTime)
        {
            if (Scale <= 0 || !double.IsFinite(Scale))
            {
                return subtitleTime - TimeSpan.FromSeconds(OffsetSeconds);
            }

            return TimeSpan.FromSeconds((subtitleTime.TotalSeconds - OffsetSeconds) / Scale);
        }

        /// <summary>How far out the subtitles are at a given point.</summary>
        /// <param name="mediaTime">A time in the media.</param>
        /// <returns>Subtitle time minus media time there. Positive means late.</returns>
        public double ErrorAt(TimeSpan mediaTime)
            => OffsetSeconds + ((Scale - 1.0) * mediaTime.TotalSeconds);
    }

    /// <summary>
    /// Fits a straight line through the anchor measurements.
    /// </summary>
    /// <remarks>
    /// <b>Two anchors is the minimum and the whole reason the plugin exists.</b>
    /// One measurement can only ever report a constant offset, and the most common
    /// real fault is not a constant — it is a drift that reads as perfect sync
    /// wherever you happened to check. A single-point checker tells you a
    /// frame-rate-mismatched file is fine, which is worse than not checking, because
    /// now you believe it.
    /// <para>
    /// Weighted by confidence, so an anchor that landed in a silence or came back
    /// garbled is outvoted rather than allowed to tilt the line. The residual is
    /// reported alongside because it is the fit's own account of whether the model
    /// holds: a subtitle file for a different cut of the film produces anchors that
    /// each measure something real and do not lie on any one line, and a large
    /// residual is how that arrives here rather than as a confident wrong answer.
    /// </para>
    /// </remarks>
    public static class DriftFit
    {
        /// <summary>Anchors below this confidence carry no weight in the fit.</summary>
        public const double MinConfidence = 0.15;

        /// <summary>Fits offset and scale.</summary>
        /// <param name="points">The anchor measurements.</param>
        /// <param name="snapFrameRates">Whether a scale near a known frame rate pair is snapped to it.</param>
        /// <returns>The correction, or <see cref="Correction.None"/> when nothing usable came in.</returns>
        public static Correction Fit(IReadOnlyList<AnchorPoint> points, bool snapFrameRates = true)
        {
            ArgumentNullException.ThrowIfNull(points);

            var usable = points.Where(p => p.Confidence >= MinConfidence).ToList();
            if (usable.Count == 0)
            {
                return Correction.None;
            }

            if (usable.Count == 1)
            {
                // One anchor answers the constant and says nothing about the slope,
                // so the honest fit is a flat line through it. The caller sees
                // Points == 1 and reports the drift as unmeasured rather than as
                // absent — those are different claims and only one of them is true.
                return new Correction(usable[0].Offset.TotalSeconds, 1.0, 0, 1, null);
            }

            var w = usable.Select(p => p.Confidence).ToArray();
            var x = usable.Select(p => p.At.TotalSeconds).ToArray();
            var y = usable.Select(p => p.Offset.TotalSeconds).ToArray();

            var sw = w.Sum();
            var meanX = w.Zip(x, (a, b) => a * b).Sum() / sw;
            var meanY = w.Zip(y, (a, b) => a * b).Sum() / sw;

            var sxx = 0.0;
            var sxy = 0.0;
            for (var i = 0; i < usable.Count; i++)
            {
                var dx = x[i] - meanX;
                sxx += w[i] * dx * dx;
                sxy += w[i] * dx * (y[i] - meanY);
            }

            // Anchors that all landed at the same moment cannot describe a slope. It
            // should not happen — the planner spreads them — but a library with a
            // two-minute item can force it, and a division by zero here would come
            // back as a NaN correction that silently destroys a file.
            var slope = sxx > 1e-9 ? sxy / sxx : 0.0;
            var intercept = meanY - (slope * meanX);
            var scale = 1.0 + slope;

            FrameRatePair? frameRate = null;
            if (snapFrameRates && FrameRates.Identify(scale) is { } pair)
            {
                // Snapping moves the line, so the intercept is re-solved at the
                // anchors' centre of mass rather than kept. Keeping it would pivot the
                // whole fit about time zero — the one place no anchor was measured —
                // and push the middle of the film out by more than the snap corrected.
                frameRate = pair;
                scale = pair.Scale;
                intercept = meanY - ((scale - 1.0) * meanX);
            }

            var residual = 0.0;
            for (var i = 0; i < usable.Count; i++)
            {
                var predicted = intercept + ((scale - 1.0) * x[i]);
                var e = y[i] - predicted;
                residual += w[i] * e * e;
            }

            return new Correction(
                intercept,
                scale,
                Math.Sqrt(residual / sw),
                usable.Count,
                frameRate);
        }
    }
}
