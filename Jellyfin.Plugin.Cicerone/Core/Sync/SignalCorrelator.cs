using System;

namespace Jellyfin.Plugin.Cicerone.Core.Sync
{
    /// <summary>Where two signals agreed best, and how much that is worth.</summary>
    /// <param name="LagSeconds">
    /// How far the second signal sits after the first. Positive means the subtitles
    /// are late.
    /// </param>
    /// <param name="Score">
    /// How well they matched there, as a correlation coefficient. Zero is what an
    /// unrelated pair of signals scores; one is impossible on real audio.
    /// </param>
    /// <param name="Prominence">
    /// How far the peak stands above the rest of the curve, in standard deviations.
    /// This, rather than the score, is what says the answer is real.
    /// </param>
    /// <param name="Found">Whether there was anything to measure at all.</param>
    public readonly record struct CorrelationPeak(
        double LagSeconds,
        double Score,
        double Prominence,
        bool Found)
    {
        /// <summary>Nothing was measurable.</summary>
        public static CorrelationPeak None => new(0, 0, 0, false);

        /// <summary>
        /// Gets the weight this measurement should carry in a fit, 0 to 1.
        /// </summary>
        /// <remarks>
        /// <b>Read off the prominence, not the score.</b> The score says how alike two
        /// signals are, and that is dominated by things which have nothing to do with
        /// being right: a talkative film scores higher than a quiet one at every lag,
        /// correct or not. The prominence asks a better question — how much better is
        /// this position than every other position tried — and an answer that is only
        /// slightly better than the alternatives is exactly the answer that should not
        /// be trusted, whatever it scores.
        /// <para>
        /// The mapping is deliberately unforgiving at the bottom. Three standard
        /// deviations is where a peak stops being remarkable on a curve with hundreds
        /// of lags in it, so that is zero, and full weight is not reached until
        /// twelve — which a genuine alignment reaches easily and a coincidence does
        /// not.
        /// </para>
        /// </remarks>
        public double Confidence => !Found || Score <= 0
            ? 0
            : Math.Clamp((Prominence - 3.0) / 9.0, 0, 1);

        /// <summary>
        /// Gets the weight a measurement made over a narrow search should carry, 0 to 1.
        /// </summary>
        /// <remarks>
        /// <b>The companion to <see cref="Confidence"/>, and which one applies depends
        /// entirely on how wide the search was.</b> Prominence asks how much better the
        /// winning position is than the alternatives, which is the right question when
        /// hundreds of positions were tried and only one can be right. It is a useless
        /// question over a few seconds of lag: the curve there is one peak and its own
        /// shoulders, every position tried is nearly the answer, and a genuine match
        /// scores no better against that background than a coincidence would.
        /// <para>
        /// What survives a narrow search is the coefficient itself, which is absolute.
        /// Speech aligned with the subtitles describing it correlates somewhere in the
        /// sixes and sevens; two stretches that merely both contain talking correlate
        /// near nothing. The floor sits above what an unrelated pair reaches and full
        /// weight is not given until well beyond it.
        /// </para>
        /// </remarks>
        public double MatchStrength => !Found
            ? 0
            : Math.Clamp((Score - 0.15) / 0.40, 0, 1);
    }

    /// <summary>
    /// Slides one speech signal over another and reports where they agree.
    /// </summary>
    /// <remarks>
    /// The measurement the whole plugin now rests on, and it is ordinary normalised
    /// cross-correlation over two rows of booleans.
    /// <para>
    /// Correlation rather than counting the bins that agree, and the difference
    /// matters: two signals that are each speech four fifths of the time agree four
    /// fifths of the time <em>at every lag</em>, so a raw agreement count is a large
    /// number that barely moves and its peak is noise. Subtracting each signal's mean
    /// — which is what the coefficient does — measures agreement above what chance
    /// already provides, and that peaks sharply and only in the right place.
    /// </para>
    /// <para>
    /// Written as a direct loop rather than through a transform. An hour and a half at
    /// a fifth of a second is 27,000 bins and the lag range is a few hundred, which is
    /// a few million operations — nothing beside the minute ffmpeg spends decoding the
    /// audio in the first place. An FFT would be asymptotically better and is not
    /// worth the two ways it could be subtly wrong.
    /// </para>
    /// </remarks>
    public static class SignalCorrelator
    {
        /// <summary>How near the peak is excluded when measuring how much it stands out.</summary>
        /// <remarks>
        /// A real peak is a few bins wide rather than one, because speech does not
        /// start on a bin boundary. Measuring the background right beside the peak
        /// would measure the peak's own shoulders and report that nothing stands out.
        /// </remarks>
        public const int GuardBins = 3;

        /// <summary>Finds where two signals agree best.</summary>
        /// <param name="audio">The signal from the audio, which is the ground truth.</param>
        /// <param name="cues">The signal from the subtitle file, which is the claim.</param>
        /// <param name="maxLagSeconds">How far apart the two may be assumed to be.</param>
        /// <param name="centreLagSeconds">The lag to search around, when one is already expected.</param>
        /// <returns>The peak.</returns>
        public static CorrelationPeak Best(
            SpeechSignal audio,
            SpeechSignal cues,
            double maxLagSeconds,
            double centreLagSeconds = 0)
        {
            ArgumentNullException.ThrowIfNull(audio);
            ArgumentNullException.ThrowIfNull(cues);

            // A signal with no edges in it cannot locate anything. Both of these are
            // real: a track of pure annotation marks nothing, and a commentary that
            // never stops marks everything, and each would still produce a peak.
            if (audio.Count == 0 || cues.Count == 0
                || audio.Active == 0 || cues.Active == 0
                || audio.Coverage > 0.95 || cues.Coverage > 0.95)
            {
                return CorrelationPeak.None;
            }

            var bin = audio.BinSeconds;
            var span = (int)Math.Round(Math.Max(maxLagSeconds, 0) / bin);
            var centre = (int)Math.Round(centreLagSeconds / bin);

            var from = centre - span;
            var to = centre + span;
            if (to <= from)
            {
                return CorrelationPeak.None;
            }

            var curve = new double[to - from + 1];

            // The winning position is kept as an index into the curve rather than as
            // the lag itself, because a lag is legitimately negative — a subtitle
            // author pulls a cue up a beat before the line is spoken, so a perfectly
            // timed track peaks slightly early — and a negative number cannot also
            // serve as the "nothing was found" sentinel.
            var bestIndex = -1;
            var best = double.NegativeInfinity;

            var audioPrefix = Prefix(audio.Bins);
            var cuePrefix = Prefix(cues.Bins);

            for (var lag = from; lag <= to; lag++)
            {
                var score = At(audio.Bins, cues.Bins, audioPrefix, cuePrefix, lag);
                curve[lag - from] = score;

                if (score > best)
                {
                    best = score;
                    bestIndex = lag - from;
                }
            }

            if (bestIndex < 0 || !double.IsFinite(best) || best <= 0)
            {
                return CorrelationPeak.None;
            }

            return new CorrelationPeak(
                (bestIndex + from) * bin,
                best,
                Prominence(curve, bestIndex),
                true);
        }

        /// <summary>
        /// How far the peak stands above the rest of the curve, in standard deviations.
        /// </summary>
        /// <param name="curve">Every lag's score.</param>
        /// <param name="peak">Which one won.</param>
        /// <returns>The peak's z-score against the others, or zero when there are too few.</returns>
        public static double Prominence(double[] curve, int peak)
        {
            ArgumentNullException.ThrowIfNull(curve);

            var count = 0;
            var sum = 0.0;
            var sumSquares = 0.0;

            for (var i = 0; i < curve.Length; i++)
            {
                if (Math.Abs(i - peak) <= GuardBins)
                {
                    continue;
                }

                count++;
                sum += curve[i];
                sumSquares += curve[i] * curve[i];
            }

            if (count < 8)
            {
                return 0;
            }

            var mean = sum / count;
            var variance = (sumSquares / count) - (mean * mean);
            if (variance <= 1e-12)
            {
                return 0;
            }

            return (curve[peak] - mean) / Math.Sqrt(variance);
        }

        private static int[] Prefix(bool[] bins)
        {
            var prefix = new int[bins.Length + 1];
            for (var i = 0; i < bins.Length; i++)
            {
                prefix[i + 1] = prefix[i] + (bins[i] ? 1 : 0);
            }

            return prefix;
        }

        /// <summary>
        /// The correlation coefficient at one lag.
        /// </summary>
        /// <remarks>
        /// Over the overlapping part only, and the means are recomputed for that part
        /// rather than taken from the whole signal. At a large lag the overlap is a
        /// different stretch of the film with a different amount of talking in it, and
        /// using the whole signal's mean there measures the difference between two
        /// scenes instead of the agreement between two tracks.
        /// </remarks>
        private static double At(bool[] audio, bool[] cues, int[] audioPrefix, int[] cuePrefix, int lag)
        {
            // cues[i] lines up with audio[i - lag]: a positive lag means the subtitle
            // signal sits after the audio, which is a track that is late.
            var firstCue = Math.Max(0, lag);
            var lastCue = Math.Min(cues.Length, audio.Length + lag);
            var n = lastCue - firstCue;

            // Too little overlap and the coefficient is measuring a handful of bins,
            // which is both meaningless and prone to scoring a perfect one.
            if (n < 16)
            {
                return 0;
            }

            var sumCue = cuePrefix[lastCue] - cuePrefix[firstCue];
            var sumAudio = audioPrefix[lastCue - lag] - audioPrefix[firstCue - lag];

            if (sumCue == 0 || sumAudio == 0 || sumCue == n || sumAudio == n)
            {
                return 0;
            }

            var both = 0;
            for (var i = firstCue; i < lastCue; i++)
            {
                if (cues[i] && audio[i - lag])
                {
                    both++;
                }
            }

            // Pearson's r for two binary vectors: the sum of squares of a 0/1 vector is
            // its sum, which is what collapses the usual formula to this.
            var numerator = ((double)n * both) - ((double)sumAudio * sumCue);
            var denominator = Math.Sqrt(
                ((double)n * sumAudio) - ((double)sumAudio * sumAudio))
                * Math.Sqrt(((double)n * sumCue) - ((double)sumCue * sumCue));

            return denominator <= 0 ? 0 : numerator / denominator;
        }
    }
}
