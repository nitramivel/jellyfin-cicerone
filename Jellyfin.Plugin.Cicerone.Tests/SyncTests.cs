using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Cicerone.Core.Subtitles;
using Jellyfin.Plugin.Cicerone.Core.Sync;
using Xunit;

namespace Jellyfin.Plugin.Cicerone.Tests
{
    /// <summary>Builds synthetic dialogue whose true offset is known exactly.</summary>
    /// <remarks>
    /// The whole test strategy for this plugin. There is no ffmpeg here and no
    /// transcription endpoint, but the thing that actually has to be right is the
    /// arithmetic between a cue's timing and a transcript's — and that can be posed
    /// exactly: build a track, build the transcript of a differently-timed copy of
    /// it, and assert that the measured difference is the one that was injected.
    /// </remarks>
    internal static class Dialogue
    {
        /// <summary>Distinctive words, so nothing pairs by accident.</summary>
        private static readonly string[] Vocabulary =
        [
            "quixotic", "belfry", "marmalade", "tungsten", "harpsichord", "vellum",
            "obelisk", "cyanide", "trellis", "gantry", "puffin", "molybdenum",
            "sextant", "arboretum", "crevasse", "mandolin", "zeppelin", "quarry",
            "lantern", "isthmus", "cormorant", "pergola", "sable", "tundra",
            "ampersand", "vestibule", "quagmire", "nutmeg", "fjord", "cobalt",
        ];

        /// <summary>One cue per word, spaced evenly from <paramref name="from"/>.</summary>
        public static IReadOnlyList<CleanCue> Cues(double from, int count, double spacing = 3.0)
        {
            var cues = new List<CleanCue>(count);
            for (var i = 0; i < count; i++)
            {
                var start = TimeSpan.FromSeconds(from + (i * spacing));
                var word = Vocabulary[i % Vocabulary.Length]
                    + (i / Vocabulary.Length).ToString(CultureInfo.InvariantCulture);

                cues.Add(new CleanCue(start, start + TimeSpan.FromSeconds(1.5), word, word));
            }

            return cues;
        }

        /// <summary>The same lines, heard <paramref name="offset"/> seconds earlier.</summary>
        public static IReadOnlyList<TranscriptSegment> Heard(
            IReadOnlyList<CleanCue> cues,
            double offset,
            double scale = 1.0)
        {
            return cues.Select(c => new TranscriptSegment(
                    TimeSpan.FromSeconds((c.Start.TotalSeconds - offset) / scale),
                    TimeSpan.FromSeconds((c.End.TotalSeconds - offset) / scale),
                    c.Text))
                .ToList();
        }

        public static IReadOnlyList<TimedToken> CueTokens(IReadOnlyList<CleanCue> cues) =>
            cues.SelectMany(c => Tokenizer.Spread(c.Text, c.Start, c.End)).ToList();
    }

    public class TokenizerTests
    {
        [Fact]
        public void LowercasesAndStripsAccents()
        {
            Assert.Equal(["deja", "vu"], Tokenizer.Words("Déjà vu"));
        }

        [Fact]
        public void TreatsContractionsAsOneWord()
        {
            Assert.Equal(["dont", "you", "dare"], Tokenizer.Words("Don't you dare"));
            Assert.Equal(Tokenizer.Words("don't"), Tokenizer.Words("dont"));
        }

        [Fact]
        public void SplitsOnPunctuation()
        {
            Assert.Equal(["well", "im", "the", "only", "one", "here"],
                Tokenizer.Words("Well, I'm the only one here."));
        }

        [Fact]
        public void SpreadsWordsAcrossTheirSpan()
        {
            var tokens = Tokenizer.Spread("one two", TimeSpan.Zero, TimeSpan.FromSeconds(4));

            Assert.Equal(2, tokens.Count);
            Assert.Equal(TimeSpan.FromSeconds(1), tokens[0].At);
            Assert.Equal(TimeSpan.FromSeconds(3), tokens[1].At);
        }

        [Fact]
        public void PutsASingleWordInTheMiddleOfItsCue()
        {
            var tokens = Tokenizer.Spread("hello", TimeSpan.Zero, TimeSpan.FromSeconds(4));
            Assert.Equal(TimeSpan.FromSeconds(2), tokens[0].At);
        }
    }

    public class OffsetSearchTests
    {
        [Theory]
        [InlineData(0.0)]
        [InlineData(2.0)]
        [InlineData(-3.4)]
        [InlineData(45.0)]
        [InlineData(-90.0)]
        public void RecoversAnInjectedOffset(double offset)
        {
            var cues = Dialogue.Cues(600, 30);
            var heard = Dialogue.Heard(cues, offset);

            var measurement = OffsetSearch.Measure(
                Dialogue.CueTokens(cues), Retiming.HeardTokens(heard), 120);

            Assert.InRange(
                measurement.Offset.TotalSeconds,
                offset - OffsetSearch.BinSeconds,
                offset + OffsetSearch.BinSeconds);
        }

        [Fact]
        public void APerfectMatchIsFullyConfident()
        {
            var cues = Dialogue.Cues(600, 30);
            var heard = Dialogue.Heard(cues, 2.0);

            var measurement = OffsetSearch.Measure(
                Dialogue.CueTokens(cues), Retiming.HeardTokens(heard), 120);

            // The scale has to reach 1, or every threshold set against it is
            // silently calibrated against a maximum that is not one.
            Assert.True(measurement.Confidence > 0.95, $"confidence was {measurement.Confidence}");
        }

        [Fact]
        public void UnrelatedTextProducesNoConfidentAnswer()
        {
            var cues = Dialogue.Cues(600, 30);

            var other = new[]
            {
                new TranscriptSegment(
                    TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(630),
                    "entirely different words about entirely different matters spoken by other people"),
            };

            var measurement = OffsetSearch.Measure(
                Dialogue.CueTokens(cues), Retiming.HeardTokens(other), 120);

            Assert.True(
                measurement.Confidence < Jellyfin.Plugin.Cicerone.Core.Sync.DriftFit.MinConfidence,
                $"confidence was {measurement.Confidence}");
        }

        [Fact]
        public void CommonWordsCannotDecideTheAnswer()
        {
            // "the" appears far more often than the threshold allows, at times that
            // support no single offset. If it were counted, its votes would swamp the
            // handful of rare words that carry the real answer.
            var cues = new List<CleanCue>();
            for (var i = 0; i < 20; i++)
            {
                var at = TimeSpan.FromSeconds(600 + (i * 3));
                cues.Add(new CleanCue(at, at + TimeSpan.FromSeconds(1), "the thing", "the thing"));
            }

            cues.Add(new CleanCue(
                TimeSpan.FromSeconds(700), TimeSpan.FromSeconds(702), "harpsichord", "harpsichord"));

            var heard = Dialogue.Heard(cues, 5.0);

            var measurement = OffsetSearch.Measure(
                Dialogue.CueTokens(cues), Retiming.HeardTokens(heard), 120);

            Assert.InRange(measurement.Offset.TotalSeconds, 4.5, 5.5);
        }

        [Fact]
        public void PairsBeyondTheSearchRangeAreNotConsidered()
        {
            var cues = Dialogue.Cues(600, 30);
            var heard = Dialogue.Heard(cues, 300);

            var measurement = OffsetSearch.Measure(
                Dialogue.CueTokens(cues), Retiming.HeardTokens(heard), 120);

            Assert.Equal(0, measurement.Votes);
        }

        [Fact]
        public void EmptyInputIsNotAnAnswer()
        {
            Assert.Equal(0, OffsetSearch.Measure([], [], 120).Votes);
            Assert.Equal(0, OffsetSearch.Measure(Dialogue.CueTokens(Dialogue.Cues(0, 5)), [], 120).Votes);
        }
    }

    public class FrameRatesTests
    {
        [Fact]
        public void NamesThePalToFilmRatio()
        {
            // A file timed against 25 fps played over a 23.976 fps encode: perfect at
            // the first line, four minutes adrift by the end of a feature.
            var pair = FrameRates.Identify(23.976 / 25.0);

            Assert.NotNull(pair);
            Assert.Equal(25.0, pair!.Value.From);
            Assert.Equal(23.976, pair.Value.To);
        }

        [Fact]
        public void NamesTheFilmToPalRatio()
        {
            var pair = FrameRates.Identify(25.0 / 23.976);

            Assert.NotNull(pair);
            Assert.Equal(23.976, pair!.Value.From);
            Assert.Equal(25.0, pair.Value.To);
        }

        [Fact]
        public void RefusesAScaleThatIsNotAKnownRatio()
        {
            Assert.Null(FrameRates.Identify(1.02));
            Assert.Null(FrameRates.Identify(1.0));
            Assert.Null(FrameRates.Identify(0));
            Assert.Null(FrameRates.Identify(double.NaN));
        }

        [Fact]
        public void TellsTwentyFourFromTwentyThreeNineSevenSix()
        {
            // One part in a thousand apart, and two and a half seconds of drift across
            // a feature. The snap tolerance has to be inside that or the wrong pair
            // gets named.
            var pair = FrameRates.Identify(24.0 / 23.976);

            Assert.NotNull(pair);
            Assert.Equal(23.976, pair!.Value.From);
            Assert.Equal(24.0, pair.Value.To);
        }
    }

    public class DriftFitTests
    {
        private static AnchorPoint At(double seconds, double offset, double confidence = 1.0) =>
            new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(offset), confidence);

        [Fact]
        public void AConstantOffsetFitsAFlatLine()
        {
            var fit = DriftFit.Fit([At(600, 2), At(1800, 2), At(3000, 2)]);

            Assert.Equal(2, fit.OffsetSeconds, 3);
            Assert.Equal(1.0, fit.Scale, 6);
            Assert.Null(fit.FrameRate);
            Assert.Equal(0, fit.ResidualSeconds, 6);
        }

        [Fact]
        public void AFrameRateMismatchIsRecognisedAndNamed()
        {
            // Subtitles authored against 25 fps, video running at 23.976.
            const double Scale = 23.976 / 25.0;

            var points = new[] { 600.0, 1800.0, 3000.0, 4200.0 }
                .Select(t => At(t, (Scale - 1) * t))
                .ToList();

            var fit = DriftFit.Fit(points);

            Assert.NotNull(fit.FrameRate);
            Assert.Equal(25.0, fit.FrameRate!.Value.From);
            Assert.Equal(Scale, fit.Scale, 6);
            Assert.Equal(0, fit.OffsetSeconds, 3);
        }

        [Fact]
        public void OneAnchorCallsADriftingFilePerfect()
        {
            // The reason a single-point checker is worse than no checker. This is the
            // file somebody has already nudged until the opening scene matched: at ten
            // minutes in it is a twentieth of a second out, and by the end of the
            // feature it is three minutes out.
            const double Scale = 23.976 / 25.0;
            const double Intercept = -(Scale - 1) * 600;

            double ErrorAt(double t) => Intercept + ((Scale - 1) * t);

            Assert.True(Math.Abs(ErrorAt(600)) < 0.05);
            Assert.True(Math.Abs(ErrorAt(5400)) > 180);

            // Checked once, at the place it happens to be right: in sync, no drift.
            var single = DriftFit.Fit([At(600, ErrorAt(600))]);
            Assert.Equal(1.0, single.Scale, 6);
            Assert.Equal(1, single.Points);
            Assert.True(Math.Abs(single.OffsetSeconds) < 0.05);

            // Checked at five, the same file is a named frame rate mismatch.
            var spread = DriftFit.Fit(
                new[] { 600.0, 1800.0, 3000.0, 4200.0, 5400.0 }.Select(t => At(t, ErrorAt(t))).ToList());

            Assert.NotNull(spread.FrameRate);
            Assert.Equal(25.0, spread.FrameRate!.Value.From);
            Assert.Equal(Scale, spread.Scale, 6);
        }

        [Fact]
        public void AnchorsThatDoNotLieOnALineLeaveALargeResidual()
        {
            var fit = DriftFit.Fit([At(600, 2), At(1800, -40), At(3000, 15), At(4200, -8)]);
            Assert.True(fit.ResidualSeconds > 1.5, $"residual was {fit.ResidualSeconds}");
        }

        [Fact]
        public void LowConfidenceAnchorsAreLeftOut()
        {
            var fit = DriftFit.Fit([At(600, 2), At(1800, 2), At(3000, 900, 0.01)]);

            Assert.Equal(2, fit.Points);
            Assert.Equal(2, fit.OffsetSeconds, 3);
        }

        [Fact]
        public void ConfidenceWeightsTheLine()
        {
            // A shaky anchor disagreeing with two solid ones bends the line a little
            // rather than deciding it. Read where the solid anchors are, not at time
            // zero, because a bent line is furthest from the truth where nothing was
            // measured.
            var weighted = DriftFit.Fit([At(600, 2, 1.0), At(1800, 2, 1.0), At(3000, 20, 0.2)]);
            var unweighted = DriftFit.Fit([At(600, 2, 1.0), At(1800, 2, 1.0), At(3000, 20, 1.0)]);

            Assert.InRange(weighted.ErrorAt(TimeSpan.FromSeconds(1200)), 1.0, 4.0);
            Assert.True(
                Math.Abs(weighted.ErrorAt(TimeSpan.FromSeconds(1200)) - 2)
                < Math.Abs(unweighted.ErrorAt(TimeSpan.FromSeconds(1200)) - 2));
        }

        [Fact]
        public void NothingUsableIsNoCorrection()
        {
            Assert.Equal(Correction.None, DriftFit.Fit([]));
            Assert.Equal(Correction.None, DriftFit.Fit([At(600, 5, 0.01)]));
        }

        [Fact]
        public void ApplyingACorrectionUndoesTheError()
        {
            const double Scale = 23.976 / 25.0;
            var correction = new Correction(3.0, Scale, 0, 4, null);

            // A line the subtitle file puts at t = offset + scale * true.
            var trueTime = TimeSpan.FromSeconds(2400);
            var subtitleTime = TimeSpan.FromSeconds(3.0 + (Scale * 2400));

            Assert.Equal(trueTime.TotalSeconds, correction.Apply(subtitleTime).TotalSeconds, 3);
        }

        [Fact]
        public void SnappingDoesNotPivotTheFitAboutTimeZero()
        {
            // The intercept is re-solved at the anchors' centre of mass after a snap.
            // Keeping it would swing the middle of the film — where every anchor
            // actually is — by more than the snap corrected.
            const double Scale = 23.976 / 25.0;
            var points = new[] { 1200.0, 2400.0, 3600.0 }.Select(t => At(t, 5 + ((Scale - 1) * t))).ToList();

            var fit = DriftFit.Fit(points);

            Assert.NotNull(fit.FrameRate);
            foreach (var point in points)
            {
                Assert.Equal(point.Offset.TotalSeconds, fit.ErrorAt(point.At), 1);
            }
        }
    }
}
