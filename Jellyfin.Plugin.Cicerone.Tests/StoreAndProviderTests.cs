using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Cicerone.Core.Reports;
using Jellyfin.Plugin.Cicerone.Core.Runs;
using Jellyfin.Plugin.Cicerone.Core.Subtitles;
using Jellyfin.Plugin.Cicerone.Core.Sync;
using Jellyfin.Plugin.Cicerone.Services.Transcription;
using Xunit;

namespace Jellyfin.Plugin.Cicerone.Tests
{
    public class OpenAiTranscriptionParseTests
    {
        private static readonly TimeSpan Clip = TimeSpan.FromSeconds(30);

        private const string Verbose = """
            {
              "task": "transcribe",
              "language": "english",
              "duration": 30.0,
              "text": "Hello there. General Kenobi.",
              "segments": [
                { "id": 0, "seek": 0, "start": 1.2, "end": 2.5, "text": " Hello there." },
                { "id": 1, "seek": 0, "start": 3.0, "end": 5.0, "text": " General Kenobi." }
              ]
            }
            """;

        [Fact]
        public void ReadsSegmentsAndTheirTimings()
        {
            var result = OpenAiTranscriptionProvider.Parse(Verbose, Clip);

            Assert.True(result.Ok);
            Assert.True(result.Timestamped);
            Assert.Equal(2, result.Segments.Count);
            Assert.Equal(TimeSpan.FromSeconds(1.2), result.Segments[0].Start);
            Assert.Equal("Hello there.", result.Segments[0].Text);
            Assert.Equal("english", result.DetectedLanguage);
        }

        [Fact]
        public void AResponseWithNoSegmentsIsKeptButFlaggedUntimed()
        {
            // What a model that does not support verbose_json returns. The anchor is
            // still worth having — it rules out a gross offset — and the caller halves
            // its weight because words placed by assuming an even speaking rate are
            // good to a few seconds and no better.
            var result = OpenAiTranscriptionProvider.Parse(
                """{ "text": "Hello there. General Kenobi." }""", Clip);

            Assert.True(result.Ok);
            Assert.False(result.Timestamped);
            Assert.Single(result.Segments);
            Assert.Equal(Clip, result.Segments[0].End);
        }

        [Fact]
        public void SilenceIsNotAFailureOfTheRequest()
        {
            var result = OpenAiTranscriptionProvider.Parse("""{ "text": "   " }""", Clip);

            Assert.False(result.Ok);
            Assert.Contains("nothing was said", result.Error!, StringComparison.Ordinal);
        }

        [Fact]
        public void EmptySegmentsCountAsSilence()
        {
            var result = OpenAiTranscriptionProvider.Parse(
                """{ "text": "x", "segments": [ { "start": 0, "end": 1, "text": "  " } ] }""", Clip);

            Assert.False(result.Ok);
        }

        [Fact]
        public void ASegmentEndingBeforeItStartsIsClamped()
        {
            // A decoder artefact. Left alone it gives its words a negative span, which
            // the tokenizer would spread backwards through the clip.
            var result = OpenAiTranscriptionProvider.Parse(
                """{ "segments": [ { "start": 5.0, "end": 2.0, "text": "backwards" } ] }""", Clip);

            Assert.True(result.Ok);
            Assert.Equal(result.Segments[0].Start, result.Segments[0].End);
        }

        [Fact]
        public void UnreadableResponsesFailLoudly()
        {
            Assert.False(OpenAiTranscriptionProvider.Parse("not json at all", Clip).Ok);
            Assert.False(OpenAiTranscriptionProvider.Parse("", Clip).Ok);
            Assert.False(OpenAiTranscriptionProvider.Parse(null, Clip).Ok);
            Assert.False(OpenAiTranscriptionProvider.Parse("[1,2,3]", Clip).Ok);
        }
    }

    public class GoogleTranscriptionParseTests
    {
        private static readonly TimeSpan Clip = TimeSpan.FromSeconds(30);

        private static string Envelope(string inner) =>
            $$"""{ "candidates": [ { "content": { "parts": [ { "text": {{System.Text.Json.JsonSerializer.Serialize(inner)}} } ] } } ] }""";

        [Fact]
        public void ReadsTheSchemaShapedAnswer()
        {
            var result = GoogleTranscriptionProvider.Parse(
                Envelope("""{ "segments": [ { "start": 1.0, "end": 3.0, "text": "Hello there." } ] }"""),
                Clip,
                "en");

            Assert.True(result.Ok);
            Assert.True(result.Timestamped);
            Assert.Equal(TimeSpan.FromSeconds(1), result.Segments[0].Start);
            Assert.Equal("Hello there.", result.Segments[0].Text);
        }

        [Fact]
        public void TimesOutsideTheClipAreClampedIntoIt()
        {
            // A model reporting times can report nonsense ones, and a segment placed
            // outside the clip it came from would vote for an offset manufactured
            // entirely by the mistake.
            var result = GoogleTranscriptionProvider.Parse(
                Envelope("""{ "segments": [ { "start": 100, "end": 200, "text": "impossible" } ] }"""),
                Clip,
                "en");

            Assert.True(result.Ok);
            Assert.Equal(Clip, result.Segments[0].Start);
            Assert.Equal(Clip, result.Segments[0].End);
        }

        [Fact]
        public void AcceptsNumbersThatArriveAsStrings()
        {
            var result = GoogleTranscriptionProvider.Parse(
                Envelope("""{ "segments": [ { "start": "1.5", "end": "4.0", "text": "Hello." } ] }"""),
                Clip,
                "en");

            Assert.True(result.Ok);
            Assert.Equal(TimeSpan.FromSeconds(1.5), result.Segments[0].Start);
        }

        [Fact]
        public void NoCandidatesIsAFailure()
        {
            Assert.False(GoogleTranscriptionProvider.Parse("""{ "candidates": [] }""", Clip, "en").Ok);
            Assert.False(GoogleTranscriptionProvider.Parse("{}", Clip, "en").Ok);
            Assert.False(GoogleTranscriptionProvider.Parse(null, Clip, "en").Ok);
        }

        [Fact]
        public void AnAnswerOfTheWrongShapeIsAFailure()
        {
            Assert.False(GoogleTranscriptionProvider
                .Parse(Envelope("""{ "lines": [] }"""), Clip, "en").Ok);
        }
    }

    public class ItemReportTests
    {
        private static TrackCandidate Track(
            int index,
            string language = "eng",
            bool external = false,
            bool sdh = false) =>
            new(index, language, null, true, false, sdh, external, false, null);

        private static TrackReport Report(
            TrackCandidate track,
            Verdict verdict,
            int cues = 900,
            string? skipped = null)
        {
            var sync = skipped is null && verdict is not (Verdict.WrongLanguage or Verdict.NoDialogue)
                ? new SyncAssessment(verdict, Correction.None, [], 0, "because")
                : null;

            return new TrackReport(track, cues, null, verdict == Verdict.WrongLanguage, sync, skipped);
        }

        private static ItemReport Item(params TrackReport[] tracks) =>
            new(Guid.NewGuid(), "Film", "Movie", null, DateTime.UtcNow, "1-2", 5400, tracks, [], 150, "whisper-1", null);

        [Fact]
        public void AVerifiedTrackBeatsAnUnknownOne()
        {
            // The whole point of having checked. A track measured and found good wins
            // even when every other tiebreak favours the other one.
            var item = Item(
                Report(Track(1), Verdict.Unknown),
                Report(Track(2, external: true), Verdict.InSync));

            Assert.Equal(2, item.Best("en")!.Track.Index);
        }

        [Fact]
        public void RanksVerdictsBeforeAnythingElse()
        {
            var item = Item(
                Report(Track(1), Verdict.Drifting),
                Report(Track(2), Verdict.Offset),
                Report(Track(3), Verdict.InSync),
                Report(Track(4), Verdict.Mismatched));

            Assert.Equal(3, item.Best("en")!.Track.Index);
        }

        [Fact]
        public void EmbeddedBeatsExternalAllElseEqual()
        {
            // Inverting what a plugin merely reading subtitles would do. An embedded
            // track was timed against the encode it ships inside; an external file was
            // timed against whatever release its author had, which is the single
            // largest source of the desync this plugin exists to find.
            var item = Item(
                Report(Track(1, external: true), Verdict.InSync),
                Report(Track(2, external: false), Verdict.InSync));

            Assert.Equal(2, item.Best("en")!.Track.Index);
        }

        [Fact]
        public void ACleanTrackBeatsAHearingImpairedOne()
        {
            var item = Item(
                Report(Track(1, sdh: true), Verdict.InSync),
                Report(Track(2, sdh: false), Verdict.InSync));

            Assert.Equal(2, item.Best("en")!.Track.Index);
        }

        [Fact]
        public void SkippedTracksAreNeverTheBest()
        {
            var item = Item(Report(Track(1), Verdict.NoDialogue, 0, "forced track"));
            Assert.Null(item.Best("en"));
        }

        [Fact]
        public void OnlyLooksAtTheLanguageAsked()
        {
            var item = Item(
                Report(Track(1, "spa"), Verdict.InSync),
                Report(Track(2, "eng"), Verdict.Offset));

            Assert.Equal(2, item.Best("en")!.Track.Index);
            Assert.Equal(1, item.Best("es")!.Track.Index);
            Assert.Null(item.Best("fr"));
        }

        [Fact]
        public void CoverageIsCountedOnTheBestTrackNotEveryTrack()
        {
            // An item holding one perfect track and three broken ones is covered.
            // Tallying all four would report it as a quarter working.
            var item = Item(
                Report(Track(1), Verdict.InSync),
                Report(Track(2), Verdict.Mismatched),
                Report(Track(3), Verdict.Drifting));

            var coverage = Coverage.Build([item], ["en"]);

            Assert.Equal(1, coverage.Items);
            Assert.Equal(1, coverage.ByVerdict[Verdict.InSync]);
            Assert.False(coverage.ByVerdict.ContainsKey(Verdict.Mismatched));
            Assert.True(item.Covered(["en"]));
        }

        [Fact]
        public void CountsItemsMissingALanguageEntirely()
        {
            var item = Item(Report(Track(1, "spa"), Verdict.InSync));
            var coverage = Coverage.Build([item], ["en", "es"]);

            Assert.Equal(1, coverage.MissingByLanguage["en"]);
            Assert.False(coverage.MissingByLanguage.ContainsKey("es"));
            Assert.False(item.Covered(["en", "es"]));
        }

        [Fact]
        public void TotalsTheAudioSpent()
        {
            var coverage = Coverage.Build([Item(Report(Track(1), Verdict.InSync))], ["en"]);
            Assert.Equal(2.5, coverage.AudioMinutes, 3);
        }
    }

    public class RunEstimateTests
    {
        private static readonly DateTime Start = new(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

        private static List<DateTime> Every(int count, double seconds) =>
            Enumerable.Range(0, count).Select(i => Start.AddSeconds(i * seconds)).ToList();

        [Fact]
        public void SaysNothingUntilThereIsSomethingToGoOn()
        {
            // The first item pays for a cold HTTP handler and whatever the filesystem
            // had not cached, so an estimate drawn from it is wrong by a factor.
            Assert.Null(RunEstimate.TimeLeft(Every(2, 10), 10, Start.AddSeconds(10)));
            Assert.Null(RunEstimate.TimeLeft([], 10, Start));
        }

        [Fact]
        public void ReadsTheRateFromCompletionsPerSecond()
        {
            // Five completions ten seconds apart, four items left: forty seconds.
            var left = RunEstimate.TimeLeft(Every(5, 10), 4, Start.AddSeconds(40));

            Assert.NotNull(left);
            Assert.Equal(40, left!.Value.TotalSeconds, 1);
        }

        [Fact]
        public void AStalledRunShowsAGrowingEstimate()
        {
            // Measured against now rather than against the last completion, so a run
            // stuck on one enormous file counts up instead of counting down through a
            // hang.
            var healthy = RunEstimate.TimeLeft(Every(5, 10), 4, Start.AddSeconds(40));
            var stalled = RunEstimate.TimeLeft(Every(5, 10), 4, Start.AddSeconds(140));

            Assert.True(stalled!.Value > healthy!.Value);
        }

        [Fact]
        public void NothingLeftIsNoTime()
        {
            Assert.Equal(TimeSpan.Zero, RunEstimate.TimeLeft(Every(5, 10), 0, Start.AddSeconds(40)));
        }

        [Fact]
        public void OnlyTheRecentWindowCounts()
        {
            // A library is not uniform: a run genuinely changes pace crossing from
            // feature films into half-hour episodes, and an average over the whole run
            // would take an hour to notice.
            var slowThenFast = Enumerable.Range(0, 10).Select(i => Start.AddSeconds(i * 60))
                .Concat(Enumerable.Range(1, 20).Select(i => Start.AddSeconds(540 + (i * 5))))
                .ToList();

            var left = RunEstimate.TimeLeft(slowThenFast, 10, slowThenFast[^1]);

            Assert.NotNull(left);
            Assert.True(left!.Value.TotalSeconds < 200, $"estimate was {left.Value.TotalSeconds}s");
        }
    }

    public class RunBudgetTests
    {
        // Two and a half minutes, which is what five thirty-second anchors cost.
        private const double Item = 150;

        [Fact]
        public void NoCeilingAdmitsEverything()
        {
            var budget = new RunBudget(0);

            for (var i = 0; i < 1000; i++)
            {
                Assert.True(budget.TryReserve(Item));
                budget.Settle(Item, Item);
            }

            Assert.False(budget.Exhausted);
        }

        [Fact]
        public void StopsTheRunOnceTheCeilingIsReached()
        {
            var budget = new RunBudget(Item * 3);

            for (var i = 0; i < 3; i++)
            {
                Assert.True(budget.TryReserve(Item));
                budget.Settle(Item, Item);
            }

            Assert.False(budget.TryReserve(Item));
            Assert.True(budget.Exhausted);
            Assert.Equal(Item * 3, budget.SpentSeconds, 3);
        }

        [Fact]
        public void ABudgetSmallerThanOneItemStopsAfterOneRatherThanRefusingToStart()
        {
            // The setting stops a run early; it does not veto one. A ceiling below the
            // cost of a single item that admitted nothing would report a completed run
            // over an unchecked library, which reads as "there was nothing to do".
            var budget = new RunBudget(10);

            Assert.True(budget.TryReserve(Item));
            budget.Settle(Item, Item);
            Assert.False(budget.TryReserve(Item));
        }

        [Fact]
        public void LanesHoldWhatTheyAreAboutToSpend()
        {
            // The whole reason the reservation exists. Three lanes take their places
            // under a three-item ceiling before any of them has spent a thing; tested
            // against what has been spent, all three would see an empty budget, and so
            // would the next three.
            var budget = new RunBudget(Item * 3);

            Assert.True(budget.TryReserve(Item));
            Assert.True(budget.TryReserve(Item));
            Assert.True(budget.TryReserve(Item));
            Assert.False(budget.TryReserve(Item));
        }

        [Fact]
        public void AnItemThatCostNothingReleasesItsPlace()
        {
            // An item skipped for having no dialogue, or one that failed before any
            // audio was cut, must not consume a share of the ceiling it never spent.
            var budget = new RunBudget(Item * 2);

            Assert.True(budget.TryReserve(Item));
            budget.Settle(Item, 0);
            Assert.True(budget.TryReserve(Item));
            budget.Settle(Item, 0);

            Assert.True(budget.TryReserve(Item));
            Assert.Equal(0, budget.SpentSeconds, 3);
        }

        [Fact]
        public void TheLedgerSurvivesLanesRunningAtOnce()
        {
            var budget = new RunBudget(Item * 50);
            var admitted = 0;

            Parallel.For(0, 500, _ =>
            {
                if (budget.TryReserve(Item))
                {
                    Interlocked.Increment(ref admitted);
                    budget.Settle(Item, Item);
                }
            });

            // At most one item per lane may be admitted on the line itself, and there
            // is no lane count here to bound it by — but the arithmetic must still add
            // up exactly, and nothing may be admitted long past the ceiling.
            Assert.InRange(admitted, 50, 50 + Environment.ProcessorCount);
            Assert.Equal(admitted * Item, budget.SpentSeconds, 3);
        }
    }
}
