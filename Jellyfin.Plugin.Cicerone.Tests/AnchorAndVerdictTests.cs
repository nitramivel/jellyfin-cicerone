using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Cicerone.Core.Subtitles;
using Jellyfin.Plugin.Cicerone.Core.Sync;
using Xunit;

namespace Jellyfin.Plugin.Cicerone.Tests
{
    public class AnchorPlannerTests
    {
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

        private static IReadOnlyList<CleanCue> Talkative(double from, double to, double spacing = 3)
        {
            var cues = new List<CleanCue>();
            var index = 0;
            for (var at = from; at < to; at += spacing)
            {
                var start = TimeSpan.FromSeconds(at);
                var text = "line" + index++;
                cues.Add(new CleanCue(start, start + TimeSpan.FromSeconds(2), text, text));
            }

            return cues;
        }

        [Fact]
        public void PlacesTheRequestedNumberOfWindows()
        {
            var anchors = AnchorPlanner.Plan(Talkative(0, 3600), TimeSpan.FromHours(1), 5, Window);
            Assert.Equal(5, anchors.Count);
        }

        [Fact]
        public void AvoidsTheHeadAndTailOfTheRuntime()
        {
            // Logos and a cold open at one end, a credit crawl and the ripper's
            // signature at the other. Neither is dialogue.
            var anchors = AnchorPlanner.Plan(Talkative(0, 3600), TimeSpan.FromHours(1), 5, Window);

            Assert.All(anchors, a => Assert.True(a.Start >= TimeSpan.FromSeconds(180)));
            Assert.All(anchors, a => Assert.True(a.End <= TimeSpan.FromSeconds(3312.001)));
        }

        [Fact]
        public void WindowsNeverOverlap()
        {
            var anchors = AnchorPlanner.Plan(Talkative(0, 3600), TimeSpan.FromHours(1), 5, Window);

            for (var i = 1; i < anchors.Count; i++)
            {
                Assert.True(anchors[i].Start >= anchors[i - 1].End);
            }
        }

        [Fact]
        public void FindsTheTalkativePartOfAQuietStretch()
        {
            // One conversation in the first slot and silence around it. A window
            // placed in the silence would record nothing to align against.
            var anchors = AnchorPlanner.Plan(
                Talkative(700, 745, 2), TimeSpan.FromHours(1), 5, Window);

            Assert.InRange(anchors[0].Start.TotalSeconds, 660, 745);
            Assert.True(anchors[0].ExpectedWords > 0);
        }

        [Fact]
        public void AShortItemGivesUpItsTrimsRatherThanItsAnchors()
        {
            // A two-minute item cannot spare five percent at each end and still hold
            // five thirty-second windows. Sampling a title sequence beats reporting
            // the item unverifiable.
            var anchors = AnchorPlanner.Plan(
                Talkative(0, 120, 2), TimeSpan.FromSeconds(120), 3, Window);

            Assert.NotEmpty(anchors);
            Assert.All(anchors, a => Assert.True(a.Duration > TimeSpan.Zero));
        }

        [Fact]
        public void FewerAnchorsRatherThanOverlappingOnes()
        {
            // Five thirty-second windows do not fit in a two-minute episode. Placing
            // them anyway would double-count the same seconds as two independent
            // measurements and let one exchange vote twice in the fit.
            var anchors = AnchorPlanner.Plan(
                Talkative(0, 130, 2), TimeSpan.FromSeconds(130), 5, Window);

            Assert.True(anchors.Count < 5);
            for (var i = 1; i < anchors.Count; i++)
            {
                Assert.True(anchors[i].Start >= anchors[i - 1].End);
            }
        }

        [Fact]
        public void NoCuesMeansNoAnchors()
        {
            Assert.Empty(AnchorPlanner.Plan([], TimeSpan.FromHours(1), 5, Window));
        }

        [Fact]
        public void FallsBackToTheLastCueWhenThereIsNoRuntime()
        {
            var anchors = AnchorPlanner.Plan(Talkative(0, 3600), TimeSpan.Zero, 5, Window);
            Assert.Equal(5, anchors.Count);
        }

        [Fact]
        public void CueSlackReachesFarEnoughToProveALargeOffset()
        {
            // The subtlety that makes a large offset measurable at all. If the file is
            // ninety seconds late, the words spoken during this window are written
            // ninety seconds further on — so a window reading only cues inside its own
            // bounds excludes the exact evidence that would prove it.
            var cues = Talkative(0, 3600);
            var anchor = new Anchor(TimeSpan.FromSeconds(600), Window, 10);

            var tight = AnchorPlanner.CueTokens(cues, anchor, 0);
            var slack = AnchorPlanner.CueTokens(cues, anchor, 120);

            Assert.True(slack.Count > tight.Count);
            Assert.Contains(slack, t => t.At > TimeSpan.FromSeconds(700));
        }

        [Fact]
        public void DistinctWordsIgnoresRepetition()
        {
            var cues = new List<CleanCue>();
            for (var i = 0; i < 10; i++)
            {
                var at = TimeSpan.FromSeconds(i * 2);
                cues.Add(new CleanCue(at, at + TimeSpan.FromSeconds(1), "same same same", "same same same"));
            }

            Assert.Equal(1, AnchorPlanner.DistinctWords(cues, TimeSpan.Zero, TimeSpan.FromSeconds(30)));
        }
    }

    public class SyncVerdictTests
    {
        private static readonly TimeSpan Runtime = TimeSpan.FromSeconds(5400);

        private static AnchorPoint At(double seconds, double offset, double confidence = 0.8) =>
            new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(offset), confidence);

        private static SyncAssessment Assess(IReadOnlyList<AnchorPoint> points) =>
            SyncVerdictBuilder.Assess(points, DriftFit.Fit(points), Runtime);

        [Fact]
        public void ATrackTimedToTheDialogueIsInSync()
        {
            var assessment = Assess([At(600, 0.05), At(1800, -0.1), At(3000, 0.08), At(4200, 0.0)]);

            Assert.Equal(Verdict.InSync, assessment.Verdict);
            Assert.False(assessment.Repairable);
        }

        [Fact]
        public void AConstantErrorIsAnOffset()
        {
            var assessment = Assess([At(600, 4.2), At(1800, 4.2), At(3000, 4.2), At(4200, 4.2)]);

            Assert.Equal(Verdict.Offset, assessment.Verdict);
            Assert.True(assessment.Repairable);
            Assert.Equal(4.2, assessment.WorstErrorSeconds, 1);
        }

        [Fact]
        public void AFrameRateMismatchIsDriftingAndSaysSo()
        {
            const double Scale = 23.976 / 25.0;
            const double Intercept = -(Scale - 1) * 600;

            var assessment = Assess(new[] { 600.0, 1800.0, 3000.0, 4200.0, 5400.0 }
                .Select(t => At(t, Intercept + ((Scale - 1) * t)))
                .ToList());

            Assert.Equal(Verdict.Drifting, assessment.Verdict);
            Assert.True(assessment.Repairable);
            Assert.Contains("25", assessment.Reason, StringComparison.Ordinal);
            Assert.Contains("fps", assessment.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void WorstErrorIsWhatTheViewerFeels()
        {
            // A drifting track has an offset near zero and is unwatchable by the end.
            // Reporting the offset would say it is fine.
            const double Scale = 23.976 / 25.0;
            const double Intercept = -(Scale - 1) * 600;

            var assessment = Assess(new[] { 600.0, 1800.0, 3000.0, 4200.0, 5400.0 }
                .Select(t => At(t, Intercept + ((Scale - 1) * t)))
                .ToList());

            Assert.True(Math.Abs(assessment.Correction.OffsetSeconds) < 30);
            Assert.True(assessment.WorstErrorSeconds > 150);
        }

        [Fact]
        public void AnchorsThatDisagreeMeanADifferentCut()
        {
            var assessment = Assess([At(600, 2), At(1800, -45), At(3000, 18), At(4200, -9)]);

            Assert.Equal(Verdict.Mismatched, assessment.Verdict);
            Assert.False(assessment.Repairable);
            Assert.Contains("different cut", assessment.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void NothingMatchingMeansMismatched()
        {
            var points = new[] { At(600, 0, 0.01), At(1800, 0, 0.01), At(3000, 0, 0.02) };
            var assessment = SyncVerdictBuilder.Assess(points, DriftFit.Fit(points), Runtime);

            Assert.Equal(Verdict.Mismatched, assessment.Verdict);
        }

        [Fact]
        public void NothingListenedToIsUnknown()
        {
            var assessment = SyncVerdictBuilder.Assess([], Correction.None, Runtime);
            Assert.Equal(Verdict.Unknown, assessment.Verdict);
        }

        [Fact]
        public void OneAnchorSaysSoRatherThanClaimingNoDrift()
        {
            var points = new[] { At(600, 8), At(1800, 0, 0.01) };
            var assessment = SyncVerdictBuilder.Assess(points, DriftFit.Fit(points), Runtime);

            Assert.Equal(Verdict.Offset, assessment.Verdict);
            Assert.Contains("drift could not be measured", assessment.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void ReadsErrorsAsLateOrEarly()
        {
            Assert.Contains("late", SyncVerdictBuilder.Format(1.4), StringComparison.Ordinal);
            Assert.Contains("early", SyncVerdictBuilder.Format(-1.4), StringComparison.Ordinal);
        }
    }

    public class RetimingTests
    {
        [Fact]
        public void MovesTimingsAndLeavesTextExactlyAlone()
        {
            var cues = SrtParser.Parse("""
                1
                00:00:10,000 --> 00:00:12,000
                <i>[gasps] Run!</i>

                2
                00:01:00,000 --> 00:01:02,000
                - Where?
                - Anywhere.
                """);

            var moved = Retiming.Apply(cues, new Correction(2.0, 1.0, 0, 4, null));

            Assert.Equal(cues.Select(c => c.Text), moved.Select(c => c.Text));
            Assert.Equal(TimeSpan.FromSeconds(8), moved[0].Start);
            Assert.Equal(TimeSpan.FromSeconds(58), moved[1].Start);
        }

        [Fact]
        public void UndoesAFrameRateMismatchAcrossTheWholeRuntime()
        {
            const double Scale = 23.976 / 25.0;

            // A file whose every timing is the true time scaled by the wrong rate.
            var truth = new[] { 60.0, 1200.0, 3600.0, 5400.0 };
            var cues = truth
                .Select(t => new Cue(
                    TimeSpan.FromSeconds(t * Scale), TimeSpan.FromSeconds((t * Scale) + 2), "line"))
                .ToList();

            var repaired = Retiming.Apply(cues, new Correction(0, Scale, 0, 5, null));

            for (var i = 0; i < truth.Length; i++)
            {
                Assert.Equal(truth[i], repaired[i].Start.TotalSeconds, 2);
            }
        }

        [Fact]
        public void RebasingPutsATranscriptOnTheItemsClock()
        {
            // Skipped, every anchor measures its own position in the film as the
            // offset: large, consistent, and complete fiction.
            var segment = new TranscriptSegment(
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), "hello");

            var rebased = segment.Rebase(TimeSpan.FromSeconds(600));

            Assert.Equal(TimeSpan.FromSeconds(602), rebased.Start);
            Assert.Equal(TimeSpan.FromSeconds(605), rebased.End);
            Assert.Equal("hello", rebased.Text);
        }
    }
}

namespace Jellyfin.Plugin.Cicerone.Tests
{
    public class TwoAnchorVerdictTests
    {
        private static AnchorPoint At(double seconds, double offset) =>
            new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(offset), 1.0);

        [Fact]
        public void TwoMeasurementsCanSeeASlopeAndCannotCheckOne()
        {
            // A line through two points passes through both exactly, so the residual —
            // the only test that ever asks whether a line was the right model — is zero
            // by construction and cannot fire. Reporting Drifting from that is
            // reporting a repairable fault on evidence that cannot be wrong, which is
            // the same error as trusting one anchor, one step further along.
            IReadOnlyList<AnchorPoint> two = [At(200, -30), At(1100, 300)];
            var assessment = SyncVerdictBuilder.Assess(two, DriftFit.Fit(two), TimeSpan.FromSeconds(1300));

            Assert.Equal(Verdict.Unknown, assessment.Verdict);
            Assert.False(assessment.Repairable);
        }

        [Fact]
        public void ThreeMeasurementsAreEnoughToCheckTheLine()
        {
            // Three points is where the residual starts meaning something, so a real
            // drift measured three times is still reported and still repairable.
            IReadOnlyList<AnchorPoint> three =
                [At(200, 2.0), At(700, 22.0), At(1200, 42.0)];

            var assessment = SyncVerdictBuilder.Assess(
                three, DriftFit.Fit(three, snapFrameRates: false), TimeSpan.FromSeconds(1300));

            Assert.Equal(Verdict.Drifting, assessment.Verdict);
            Assert.True(assessment.Repairable);
        }

        [Fact]
        public void ThreeMeasurementsThatDisagreeAreStillAMismatch()
        {
            IReadOnlyList<AnchorPoint> scattered =
                [At(200, -40), At(700, 120), At(1200, -25)];

            var assessment = SyncVerdictBuilder.Assess(
                scattered, DriftFit.Fit(scattered), TimeSpan.FromSeconds(1300));

            Assert.Equal(Verdict.Mismatched, assessment.Verdict);
            Assert.False(assessment.Repairable);
        }
    }
}
