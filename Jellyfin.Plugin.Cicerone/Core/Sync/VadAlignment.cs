using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Cicerone.Core.Audio;
using Jellyfin.Plugin.Cicerone.Core.Subtitles;

namespace Jellyfin.Plugin.Cicerone.Core.Sync
{
    /// <summary>What correlating a track against the audio produced.</summary>
    /// <param name="Anchors">One measurement per window, ready to be fitted.</param>
    /// <param name="GlobalScore">How well the whole track matched at its best position.</param>
    /// <param name="GlobalOffsetSeconds">The whole-file offset the windows were searched around.</param>
    /// <param name="GlobalScale">The frame-rate hypothesis that fitted the whole file best.</param>
    /// <param name="Failure">Why nothing could be measured, when nothing could.</param>
    public sealed record VadMeasurement(
        IReadOnlyList<AnchorPoint> Anchors,
        double GlobalScore,
        double GlobalOffsetSeconds,
        double GlobalScale,
        string? Failure)
    {
        /// <summary>Nothing was measurable.</summary>
        /// <param name="reason">Why.</param>
        /// <returns>The measurement.</returns>
        public static VadMeasurement Nothing(string reason) => new([], 0, 0, 1, reason);

        /// <summary>
        /// Gets whether the track and the audio are related at all.
        /// </summary>
        /// <remarks>
        /// The question the transcript method answered by finding no words in common,
        /// and it is answered here by finding no position at all where the two signals
        /// agree. False is not a failure to measure — it is a measurement, and it says
        /// this subtitle file was written for something else.
        /// </remarks>
        public bool Matched => Failure is null && GlobalScore >= VadAlignment.MinGlobalScore;
    }

    /// <summary>
    /// Measures a subtitle track against the audio by sliding one over the other.
    /// </summary>
    /// <remarks>
    /// <b>This is Cicerone's measurement.</b> It answers the same question the anchor
    /// and transcript method answered, over the whole file instead of a few windows,
    /// for nothing instead of a bill — and it produces exactly the same evidence:
    /// a list of <see cref="AnchorPoint"/>, one per window, each saying how far out
    /// the track was at one moment. Everything downstream is untouched.
    /// <see cref="DriftFit"/> still fits the line, <see cref="FrameRates"/> still
    /// snaps it, <see cref="SyncVerdictBuilder"/> still judges it, and the repair is
    /// still written from the same <see cref="Correction"/>.
    /// <para>
    /// Two passes, and the first exists to make the second possible. A file timed
    /// against a 25 fps transfer is five minutes out by the end of a feature, which is
    /// further than any sane search range; worse, within a ten-minute window it drifts
    /// by twenty-five seconds, so the window's own peak is smeared across half a
    /// minute and there is nothing sharp left to find. The global pass tries each
    /// known frame-rate ratio over the whole file and keeps the one that agrees best,
    /// which both locates the track and removes the smear. The windowed pass then
    /// searches a few seconds either side of what the global answer predicts, at a
    /// resolution ten times finer, and it is those residuals that become the anchors.
    /// </para>
    /// <para>
    /// The windows are free, so there are more of them than a transcript could ever
    /// have afforded. That is not a luxury: <b>one measurement is worse than none</b>
    /// remains true, and a dozen measurements is what makes the slope survive several
    /// of them landing somewhere useless.
    /// </para>
    /// </remarks>
    public static class VadAlignment
    {
        /// <summary>The bin size the whole-file search runs at.</summary>
        public const double CoarseBinSeconds = 0.2;

        /// <summary>The bin size the per-window refinement runs at.</summary>
        /// <remarks>
        /// A fiftieth of a second, which is well under the third of a second that
        /// separates "in sync" from "not". Running the whole-file search this finely
        /// would be twenty-five times the work to locate something that is about to be
        /// measured again anyway.
        /// </remarks>
        public const double FineBinSeconds = 0.02;

        /// <summary>How far either side of the global answer a window is searched.</summary>
        public const double WindowSearchSeconds = 4.0;

        /// <summary>How well the whole file must match before the windows are worth measuring.</summary>
        /// <remarks>
        /// Low on purpose. This is not the threshold that decides whether a track is
        /// good — that is <see cref="SyncVerdictBuilder"/>'s job and it works off the
        /// anchors. This only asks whether there is any relationship at all between
        /// these two signals, and below it there is nothing for the windows to refine.
        /// </remarks>
        public const double MinGlobalScore = 0.08;

        /// <summary>Measures one track against the audio.</summary>
        /// <param name="speech">Where the audio has speech in it.</param>
        /// <param name="cues">The track's cleaned cues.</param>
        /// <param name="runtime">The item's runtime.</param>
        /// <param name="windowCount">How many windows to measure the track in.</param>
        /// <param name="maxOffsetSeconds">How far out the track may be assumed to be.</param>
        /// <param name="considerFrameRates">Whether to try the known frame-rate ratios.</param>
        /// <returns>The measurement.</returns>
        public static VadMeasurement Measure(
            IReadOnlyList<SpeechSpan> speech,
            IReadOnlyList<CleanCue> cues,
            TimeSpan runtime,
            int windowCount = 12,
            double maxOffsetSeconds = 120,
            bool considerFrameRates = true)
        {
            ArgumentNullException.ThrowIfNull(speech);
            ArgumentNullException.ThrowIfNull(cues);

            if (speech.Count == 0)
            {
                return VadMeasurement.Nothing("no speech was found in the audio track");
            }

            if (cues.Count == 0)
            {
                return VadMeasurement.Nothing("the track holds no dialogue to align");
            }

            // Long enough to hold both sides however wrong the track is. A subtitle
            // file for a longer cut runs past the runtime, and binning to the runtime
            // would silently truncate the evidence that says so.
            var duration = TimeSpan.FromSeconds(Math.Max(
                Math.Max(runtime.TotalSeconds, speech[^1].End.TotalSeconds),
                cues[^1].End.TotalSeconds + maxOffsetSeconds));

            var audioCoarse = SpeechSignal.FromSpans(speech, duration, CoarseBinSeconds);

            if (audioCoarse.Coverage is <= 0.02 or >= 0.95)
            {
                // Either the noise floor was set so high that the film reads as silent,
                // or so low that it reads as continuous speech. Both produce a signal
                // with no shape, and a correlation over one still returns a confident
                // number.
                return VadMeasurement.Nothing(
                    "the audio came back as almost all speech or almost all silence, "
                    + "which cannot be aligned against — the silence threshold is probably wrong for this mix");
            }

            var (scale, offset, score) = Global(audioCoarse, cues, duration, maxOffsetSeconds, considerFrameRates);

            if (score < MinGlobalScore)
            {
                return new VadMeasurement([], score, offset, scale, null);
            }

            var anchors = Windows(speech, cues, duration, windowCount, scale, offset);
            return new VadMeasurement(anchors, score, offset, scale, null);
        }

        /// <summary>Finds the frame-rate ratio and offset that fit the whole file best.</summary>
        private static (double Scale, double Offset, double Score) Global(
            SpeechSignal audio,
            IReadOnlyList<CleanCue> cues,
            TimeSpan duration,
            double maxOffsetSeconds,
            bool considerFrameRates)
        {
            var candidates = new List<double> { 1.0 };
            if (considerFrameRates)
            {
                candidates.AddRange(FrameRates.Pairs().Select(p => p.Scale));
            }

            var bestScale = 1.0;
            var bestOffset = 0.0;
            var bestScore = double.NegativeInfinity;

            foreach (var scale in candidates)
            {
                // The cue signal is laid out on the audio's clock under this
                // hypothesis: a cue written at subtitle time u is claimed to belong at
                // u / scale. What the correlation then finds is the constant left over,
                // which is the offset.
                var signal = SpeechSignal.FromCues(cues, duration, audio.BinSeconds, 0, scale);
                var peak = SignalCorrelator.Best(audio, signal, maxOffsetSeconds);

                if (!peak.Found || peak.Score <= bestScore)
                {
                    continue;
                }

                bestScore = peak.Score;
                bestScale = scale;

                // The lag is measured on the scaled clock, so it is scaled back to be
                // the offset in the fit's own terms: subtitle = offset + scale x audio.
                bestOffset = peak.LagSeconds * scale;
            }

            return (bestScale, bestOffset, double.IsFinite(bestScore) ? bestScore : 0);
        }

        /// <summary>Measures the residual in each window, at fine resolution.</summary>
        private static IReadOnlyList<AnchorPoint> Windows(
            IReadOnlyList<SpeechSpan> speech,
            IReadOnlyList<CleanCue> cues,
            TimeSpan duration,
            int windowCount,
            double scale,
            double offset)
        {
            var count = Math.Clamp(windowCount, 2, 64);
            var audioFine = SpeechSignal.FromSpans(speech, duration, FineBinSeconds);

            // The cue signal with the global answer already taken out of it. If the
            // global answer is right this now lies on top of the audio everywhere, and
            // what each window measures is how far it still is not.
            var corrected = SpeechSignal.FromCues(cues, duration, FineBinSeconds, -offset, scale);

            var total = duration.TotalSeconds;
            var length = total / count;
            var anchors = new List<AnchorPoint>(count);

            for (var i = 0; i < count; i++)
            {
                var from = i * length;
                var to = from + length;
                var centre = from + (length / 2);

                var audioSlice = audioFine.Slice(from, to);
                var cueSlice = corrected.Slice(from, to);

                if (audioSlice.Active == 0 || cueSlice.Active == 0)
                {
                    // Nobody speaks here, or the track says nobody does. Silence is not
                    // disagreement, so the window is dropped rather than recorded as a
                    // failed measurement that would drag the average confidence down.
                    continue;
                }

                // Weighted by the coefficient rather than by how far the peak stands
                // out: the search here is four seconds wide, and over four seconds
                // every position tried is on the shoulder of the same peak.
                var peak = SignalCorrelator.Best(audioSlice, cueSlice, WindowSearchSeconds);
                var confidence = peak.MatchStrength;
                if (!peak.Found || confidence <= 0)
                {
                    continue;
                }

                // Back to the raw difference the fit expects. The corrected signal put
                // this window's speech at audio time (centre + residual), so the cue
                // behind it was written at offset + scale x (centre + residual), and
                // the difference between that and the audio time is what an anchor is.
                var residual = peak.LagSeconds;
                var raw = offset + ((scale - 1.0) * centre) + (scale * residual);

                anchors.Add(new AnchorPoint(
                    TimeSpan.FromSeconds(centre),
                    TimeSpan.FromSeconds(raw),
                    confidence));
            }

            return anchors;
        }
    }
}
