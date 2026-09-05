using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Cicerone.Core.Audio;
using Jellyfin.Plugin.Cicerone.Core.Subtitles;
using Jellyfin.Plugin.Cicerone.Core.Sync;
using Xunit;

namespace Jellyfin.Plugin.Cicerone.Tests
{
    /// <summary>
    /// A film's worth of speech with a known answer in it.
    /// </summary>
    /// <remarks>
    /// The same strategy as <c>Dialogue</c> in <c>SyncTests</c>, moved down a level.
    /// Where that built a transcript of words and asked whether the arithmetic between
    /// two clocks came out, this builds the pattern of <em>when</em> somebody spoke and
    /// asks the same question of the correlation — because that is what the
    /// measurement is made of now.
    /// <para>
    /// The subtitle side is deliberately not a clean copy of the audio side. Real
    /// subtitle authors pull a cue up a beat before the line so the reader is not
    /// chasing it and hold it after the line has finished, and real tracks leave the
    /// occasional utterance out. All three are injected, because a correlator that
    /// only works on an exact copy of its input works on nothing.
    /// </para>
    /// </remarks>
    internal static class Film
    {
        internal static IReadOnlyList<SpeechSpan> Speech(int seed = 7, double minutes = 90)
        {
            var random = new Random(seed);
            var spans = new List<SpeechSpan>();
            var at = 12.0;
            var end = minutes * 60;

            while (at < end)
            {
                // Every so often the film stops talking: a chase, a score, a landscape.
                if (random.NextDouble() < 0.04)
                {
                    at += 10 + (random.NextDouble() * 25);
                    continue;
                }

                var length = 0.8 + (random.NextDouble() * 3.2);
                spans.Add(new SpeechSpan(TimeSpan.FromSeconds(at), TimeSpan.FromSeconds(at + length)));

                at += length + 0.35 + (random.NextDouble() * 2.4);
            }

            return spans;
        }

        /// <summary>The track a subtitle author would have written for that audio.</summary>
        /// <param name="speech">What was actually said, and when.</param>
        /// <param name="offset">A constant the whole track is out by.</param>
        /// <param name="scale">The factor the track's clock runs at.</param>
        /// <param name="seed">The jitter's seed.</param>
        /// <returns>The cues.</returns>
        internal static IReadOnlyList<CleanCue> Cues(
            IReadOnlyList<SpeechSpan> speech,
            double offset = 0,
            double scale = 1.0,
            int seed = 11)
        {
            var random = new Random(seed);
            var cues = new List<CleanCue>();

            foreach (var span in speech)
            {
                // One line in twenty is not subtitled at all.
                if (random.NextDouble() < 0.05)
                {
                    continue;
                }

                var lead = 0.05 + (random.NextDouble() * 0.25);
                var hold = 0.1 + (random.NextDouble() * 0.5);

                var start = offset + (scale * (span.Start.TotalSeconds - lead));
                var end = offset + (scale * (span.End.TotalSeconds + hold));

                cues.Add(new CleanCue(
                    TimeSpan.FromSeconds(start),
                    TimeSpan.FromSeconds(end),
                    "some dialogue",
                    "some dialogue"));
            }

            return cues;
        }

        internal static TimeSpan Runtime(IReadOnlyList<SpeechSpan> speech) =>
            speech[^1].End + TimeSpan.FromSeconds(30);
    }

    public class VadPlanTests
    {
        [Fact]
        public void BuildsADetectionCommandThatBandLimitsToSpeechFirst()
        {
            var arguments = VadPlan.DetectArguments("/films/Heat (1995).mkv", 1);

            // The band limiting is not decoration: a score and an explosion register as
            // activity exactly like a voice, and a signal built without it is not a
            // signal about dialogue.
            Assert.Contains("highpass=f=200,lowpass=f=3000", arguments, StringComparison.Ordinal);
            Assert.Contains("silencedetect=noise=-30dB:d=0.3", arguments, StringComparison.Ordinal);

            // silencedetect reports through the log, so the log cannot be quietened.
            Assert.Contains("-loglevel info", arguments, StringComparison.Ordinal);
            Assert.Contains("-map 0:1", arguments, StringComparison.Ordinal);
            Assert.Contains("-f null", arguments, StringComparison.Ordinal);
            Assert.Contains("\"/films/Heat (1995).mkv\"", arguments, StringComparison.Ordinal);
        }

        [Fact]
        public void ReadsSpeechAsTheGapsBetweenSilences()
        {
            const string log = """
                [silencedetect @ 0x5599] silence_start: 0
                [silencedetect @ 0x5599] silence_end: 4.5 | silence_duration: 4.5
                [silencedetect @ 0x5599] silence_start: 9.25
                [silencedetect @ 0x5599] silence_end: 12 | silence_duration: 2.75
                """;

            var speech = VadPlan.ParseSilences(log, TimeSpan.FromSeconds(20));

            Assert.Equal(2, speech.Count);
            Assert.Equal(4.5, speech[0].Start.TotalSeconds, 3);
            Assert.Equal(9.25, speech[0].End.TotalSeconds, 3);
            Assert.Equal(12, speech[1].Start.TotalSeconds, 3);
            Assert.Equal(20, speech[1].End.TotalSeconds, 3);
        }

        [Fact]
        public void ASilenceLeftOpenRunsToTheEnd()
        {
            // What a film fading out on its credits looks like.
            const string log = "[silencedetect @ 0x1] silence_start: 88.5";

            var speech = VadPlan.ParseSilences(log, TimeSpan.FromSeconds(100));

            Assert.Single(speech);
            Assert.Equal(0, speech[0].Start.TotalSeconds, 3);
            Assert.Equal(88.5, speech[0].End.TotalSeconds, 3);
        }

        [Fact]
        public void NoSilenceAtAllIsOneUnbrokenSpan()
        {
            var speech = VadPlan.ParseSilences("nothing of interest here", TimeSpan.FromSeconds(60));

            Assert.Single(speech);
            Assert.Equal(60, speech[0].End.TotalSeconds, 3);
        }
    }

    public class SpeechSignalTests
    {
        [Fact]
        public void ACueShorterThanABinStillMarksOne()
        {
            // The shortest lines are the interjections, which are the sharpest edges in
            // the signal and the most worth aligning on. Rounding them away would
            // delete exactly the evidence that locates a track.
            var cues = new[] { new CleanCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.01), "no", "no") };
            var signal = SpeechSignal.FromCues(cues, TimeSpan.FromSeconds(10), 0.2);

            Assert.Equal(1, signal.Active);
        }

        [Fact]
        public void CoverageNoticesASignalWithNoShapeInIt()
        {
            var wall = new[] { new SpeechSpan(TimeSpan.Zero, TimeSpan.FromSeconds(100)) };
            var signal = SpeechSignal.FromSpans(wall, TimeSpan.FromSeconds(100), 0.2);

            Assert.Equal(1.0, signal.Coverage, 2);
        }

        [Fact]
        public void ScalingMapsTheTimesRatherThanStretchingTheBins()
        {
            var cues = new[] { new CleanCue(TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(51), "x", "x") };
            var signal = SpeechSignal.FromCues(cues, TimeSpan.FromSeconds(100), 0.1, 0, 2.0);

            // Halved by the scale, so the bin lit is the one at 25 seconds.
            Assert.True(signal.Bins[250]);
            Assert.False(signal.Bins[500]);
        }
    }

    public class VadAlignmentTests
    {
        [Fact]
        public void APerfectTrackMeasuresAsPerfect()
        {
            var speech = Film.Speech();
            var cues = Film.Cues(speech);

            var measurement = VadAlignment.Measure(speech, cues, Film.Runtime(speech));
            var correction = DriftFit.Fit(measurement.Anchors);

            Assert.Null(measurement.Failure);
            Assert.True(measurement.Anchors.Count >= 8, $"only {measurement.Anchors.Count} windows measured");
            Assert.Equal(0, correction.OffsetSeconds, 1);
            Assert.Equal(1.0, correction.Scale, 4);
        }

        [Theory]
        [InlineData(2.5)]
        [InlineData(-4.0)]
        [InlineData(31.0)]
        [InlineData(-75.0)]
        public void FindsTheOffsetItWasGiven(double offset)
        {
            var speech = Film.Speech();
            var cues = Film.Cues(speech, offset);

            var measurement = VadAlignment.Measure(speech, cues, Film.Runtime(speech));
            var correction = DriftFit.Fit(measurement.Anchors);

            Assert.Equal(offset, correction.OffsetSeconds, 1);
            Assert.Equal(1.0, correction.Scale, 4);
        }

        [Fact]
        public void FindsPalDriftAndNamesIt()
        {
            // The fault the whole plugin exists for: perfect on the first line and four
            // minutes adrift by the end.
            var speech = Film.Speech();
            var scale = 23.976 / 25.0;
            var cues = Film.Cues(speech, 0, scale);

            var measurement = VadAlignment.Measure(speech, cues, Film.Runtime(speech));
            var correction = DriftFit.Fit(measurement.Anchors);

            Assert.Equal(scale, correction.Scale, 5);
            Assert.NotNull(correction.FrameRate);
            Assert.Equal("25 → 23.976 fps", correction.FrameRate!.Value.Label);
        }

        [Fact]
        public void FindsAnOffsetAndADriftTogether()
        {
            var speech = Film.Speech();
            var scale = 25.0 / 24.0;
            var cues = Film.Cues(speech, 6.0, scale);

            var measurement = VadAlignment.Measure(speech, cues, Film.Runtime(speech));
            var correction = DriftFit.Fit(measurement.Anchors);

            Assert.Equal(scale, correction.Scale, 5);
            Assert.Equal(6.0, correction.OffsetSeconds, 1);
        }

        [Fact]
        public void ADifferentFilmDoesNotCorrelate()
        {
            // The check that survives losing the transcript. Two unrelated films both
            // consist of people talking with gaps, and the reason this comes back
            // empty rather than confident is that the peak has to stand out from the
            // rest of the curve, not merely be the largest number in it.
            var speech = Film.Speech(seed: 7);
            var other = Film.Cues(Film.Speech(seed: 99));

            var measurement = VadAlignment.Measure(speech, other, Film.Runtime(speech));
            var confident = measurement.Anchors.Count(a => a.Confidence >= DriftFit.MinConfidence);

            Assert.True(confident <= 2, $"{confident} windows claimed to have found a match");
        }

        [Fact]
        public void AVerdictComesOutOfTheEndOfIt()
        {
            // The point of producing anchors rather than an answer: everything that
            // judged the transcript method judges this one, unchanged.
            var speech = Film.Speech();
            var runtime = Film.Runtime(speech);

            var good = VadAlignment.Measure(speech, Film.Cues(speech), runtime);
            var late = VadAlignment.Measure(speech, Film.Cues(speech, 9.0), runtime);
            var drifting = VadAlignment.Measure(speech, Film.Cues(speech, 0, 23.976 / 25.0), runtime);

            Assert.Equal(Verdict.InSync, Assess(good, runtime).Verdict);
            Assert.Equal(Verdict.Offset, Assess(late, runtime).Verdict);
            Assert.Equal(Verdict.Drifting, Assess(drifting, runtime).Verdict);
        }

        [Fact]
        public void RefusesAnAudioSignalWithNoShapeInIt()
        {
            // A commentary track that never stops, or a noise floor set so low the
            // room tone reads as speech. Every position looks like every other one, so
            // a correlation over it still returns a peak and the peak is nothing.
            var speech = Film.Speech();
            var cues = Film.Cues(speech);
            var wall = new[] { new SpeechSpan(TimeSpan.Zero, cues[^1].End + TimeSpan.FromSeconds(200)) };

            var measurement = VadAlignment.Measure(wall, cues, Film.Runtime(speech));

            Assert.NotNull(measurement.Failure);
            Assert.False(measurement.Matched);
            Assert.Empty(measurement.Anchors);
        }

        [Fact]
        public void ATrackForAnotherFilmIsAMeasurementRatherThanAFailure()
        {
            var speech = Film.Speech(seed: 7);
            var other = Film.Cues(Film.Speech(seed: 99));

            var measurement = VadAlignment.Measure(speech, other, Film.Runtime(speech));

            Assert.Null(measurement.Failure);
            Assert.False(measurement.Matched);
        }

        [Fact]
        public void ATrackForThisFilmMatches()
        {
            var speech = Film.Speech();
            var measurement = VadAlignment.Measure(speech, Film.Cues(speech, 40), Film.Runtime(speech));

            Assert.True(measurement.Matched);
        }

        private static SyncAssessment Assess(VadMeasurement measurement, TimeSpan runtime) =>
            SyncVerdictBuilder.Assess(measurement.Anchors, DriftFit.Fit(measurement.Anchors), runtime);
    }
}
