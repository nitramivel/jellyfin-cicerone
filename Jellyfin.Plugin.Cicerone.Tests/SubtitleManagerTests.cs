using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Cicerone.Core.Subtitles;
using Jellyfin.Plugin.Cicerone.Core.Sync;
using Jellyfin.Plugin.Cicerone.Services.Transcription;
using Xunit;

namespace Jellyfin.Plugin.Cicerone.Tests
{
    public class SidecarNamingTests
    {
        private const string Media = "The Conversation (1974).mkv";

        [Theory]
        [InlineData("The Conversation (1974).srt", true)]
        [InlineData("The Conversation (1974).en.srt", true)]
        [InlineData("The Conversation (1974).en.sdh.srt", true)]
        [InlineData("The Conversation (1974).en.cicerone.srt", true)]
        [InlineData("The Conversation (1974).ass", true)]
        [InlineData("The Conversation (1974).mkv", false)]
        [InlineData("The Conversation (1974).nfo", false)]
        [InlineData("Another Film.en.srt", false)]
        [InlineData("The Conversation (1974) Extended.en.srt", false)]
        public void PairsASidecarToItsFilmTheWayJellyfinDoes(string name, bool expected)
        {
            Assert.Equal(expected, SidecarNaming.BelongsTo(name, Media));
        }

        [Theory]
        [InlineData("The Conversation (1974).en.srt", "en")]
        [InlineData("The Conversation (1974).eng.sdh.srt", "en")]
        [InlineData("The Conversation (1974).pt-BR.srt", "pt")]
        [InlineData("The Conversation (1974).srt", "")]
        public void ReadsTheLanguageOutOfTheName(string name, string expected)
        {
            Assert.Equal(expected, SidecarNaming.LanguageOf(name, Media));
        }

        [Fact]
        public void TellsCiceronesOwnFilesApartFromEverybodyElses()
        {
            // The line that governs what the manager will overwrite without asking.
            Assert.Equal(
                SourceKind.External,
                SidecarNaming.Classify("Film.en.srt", "cicerone", "cicerone-heard"));

            Assert.Equal(
                SourceKind.Repaired,
                SidecarNaming.Classify("Film.en.cicerone.srt", "cicerone", "cicerone-heard"));

            Assert.Equal(
                SourceKind.Heard,
                SidecarNaming.Classify("Film.en.cicerone-heard.srt", "cicerone", "cicerone-heard"));
        }

        [Fact]
        public void ATranscriptIsNotFiledAsARepair()
        {
            // The default markers share a prefix, so testing the shorter one first
            // would file every transcribed track as a repaired one — and then offer to
            // overwrite it with a repair.
            Assert.True(SidecarNaming.IsOurs("Film.en.cicerone-heard.srt", "cicerone", "cicerone-heard"));
            Assert.Equal(
                SourceKind.Heard,
                SidecarNaming.Classify("Film.en.cicerone-heard.srt", "cicerone", "cicerone-heard"));
        }

        [Fact]
        public void AFileNamedLikeOursButWrittenByAnybodyElseIsNotOurs()
        {
            // "cicerone" has to be its own token in the name. A film actually called
            // something with the marker inside a word must not be mistaken for output.
            Assert.False(SidecarNaming.IsOurs("Ciceronewatch.en.srt", "cicerone", "cicerone-heard"));
        }

        [Fact]
        public void TheTokenIsStableAndCarriesNoPath()
        {
            var token = SidecarNaming.Token("/films/The Conversation (1974)/The Conversation (1974).en.srt");

            Assert.Equal(token, SidecarNaming.Token("/films/The Conversation (1974)/The Conversation (1974).en.srt"));
            Assert.DoesNotContain("/", token, StringComparison.Ordinal);
            Assert.DoesNotContain(" ", token, StringComparison.Ordinal);
            Assert.NotEqual(token, SidecarNaming.Token("/films/Other.en.srt"));
        }
    }

    public class TranscriptToCuesTests
    {
        private static TranscriptSegment Said(double from, double to, string text) =>
            new(TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(to), text);

        [Fact]
        public void AnOrdinaryUtteranceBecomesOneCue()
        {
            var cues = TranscriptToCues.Build([Said(10, 13, "I know what you did.")]);

            Assert.Single(cues);
            Assert.Equal("I know what you did.", cues[0].Text);
            Assert.Equal(10, cues[0].Start.TotalSeconds, 2);
            Assert.Equal(13, cues[0].End.TotalSeconds, 2);
        }

        [Fact]
        public void AParagraphIsCutIntoReadableCues()
        {
            // A speech model happily returns forty seconds in one segment. Put on
            // screen unaltered that is a wall of text nobody can read in the time it
            // is up, which is the difference between a transcript and subtitles.
            var text = string.Join(" ", Enumerable.Repeat("This is a sentence of some length.", 8));
            var cues = TranscriptToCues.Build([Said(0, 40, text)]);

            Assert.True(cues.Count >= 4, $"only cut into {cues.Count} cues");
            Assert.All(cues, c => Assert.True(
                c.Text.Replace("\n", " ", StringComparison.Ordinal).Length <= TranscriptToCues.MaxCharacters,
                $"a cue was {c.Text.Length} characters"));
        }

        [Fact]
        public void NoCueOutlastsItsWelcome()
        {
            var cues = TranscriptToCues.Build([Said(0, 60, "A short line.")]);

            Assert.All(cues, c => Assert.True(
                (c.End - c.Start).TotalSeconds <= TranscriptToCues.MaxCueSeconds + 0.01,
                "a cue stayed up too long"));
        }

        [Fact]
        public void CuesNeverOverlap()
        {
            // Two on screen at once is a player's decision to make badly, so it is not
            // offered one. Short cues are stretched to be readable, which is exactly
            // what can push one into the next.
            var cues = TranscriptToCues.Build([
                Said(0, 0.2, "Hey."),
                Said(0.3, 0.5, "What?"),
                Said(0.6, 4.0, "I said, are you coming or not?"),
            ]);

            for (var i = 0; i < cues.Count - 1; i++)
            {
                Assert.True(
                    cues[i].End <= cues[i + 1].Start,
                    $"cue {i} ran to {cues[i].End} over one starting at {cues[i + 1].Start}");
            }
        }

        [Fact]
        public void FragmentsOfOneBreathAreJoined()
        {
            var cues = TranscriptToCues.Build([
                Said(5.0, 5.6, "I was going to"),
                Said(5.7, 6.4, "tell you tomorrow."),
            ]);

            Assert.Single(cues);
            Assert.Equal("I was going to tell you tomorrow.", cues[0].Text);
        }

        [Fact]
        public void ASentenceEndIsNotJoinedAcross()
        {
            var cues = TranscriptToCues.Build([
                Said(5.0, 5.6, "That is all."),
                Said(5.7, 6.4, "Goodnight."),
            ]);

            Assert.Equal(2, cues.Count);
        }

        [Fact]
        public void LongLinesAreBrokenNearTheMiddle()
        {
            var wrapped = TranscriptToCues.Wrap(
                "The thing about the whole business is that nobody was ever going to admit it.");

            var lines = wrapped.Split('\n');
            Assert.Equal(2, lines.Length);

            // Split evenly, because the eye travels the width of the longest line
            // either way and a long line beside a short one reads worse.
            Assert.True(Math.Abs(lines[0].Length - lines[1].Length) < 20, wrapped);
        }

        [Fact]
        public void NothingHeardIsNoTrackRatherThanAnEmptyOne()
        {
            Assert.Empty(TranscriptToCues.Build([]));
            Assert.Empty(TranscriptToCues.Build([Said(0, 5, "   ")]));
        }
    }

    public class FullTranscriberPiecesTests
    {
        [Fact]
        public void ThePiecesCoverTheWholeRuntimeExactly()
        {
            var pieces = FullTranscriber.Pieces(TimeSpan.FromMinutes(95), 600);

            Assert.Equal(10, pieces.Count);
            Assert.Equal(TimeSpan.Zero, pieces[0].Start);

            // Butted up against one another: an overlap transcribes the same speech
            // twice and puts two cues on screen for it, a gap loses whatever was said
            // across the join.
            for (var i = 1; i < pieces.Count; i++)
            {
                Assert.Equal(pieces[i - 1].End, pieces[i].Start);
            }

            Assert.Equal(95 * 60, pieces[^1].End.TotalSeconds, 3);
        }

        [Fact]
        public void ThereIsNothingToTranscribeWithoutARuntime()
        {
            Assert.Empty(FullTranscriber.Pieces(TimeSpan.Zero, 600));
        }

        [Fact]
        public void AShortEpisodeIsOnePiece()
        {
            var pieces = FullTranscriber.Pieces(TimeSpan.FromMinutes(8), 600);

            Assert.Single(pieces);
            Assert.Equal(8 * 60, pieces[0].Duration.TotalSeconds, 3);
        }
    }
}
