using System;
using System.Linq;
using Jellyfin.Plugin.Cicerone.Core.Subtitles;
using Xunit;

namespace Jellyfin.Plugin.Cicerone.Tests
{
    public class SrtParserTests
    {
        private const string Simple = """
            1
            00:00:10,500 --> 00:00:12,000
            You talking to me?

            2
            00:00:14,000 --> 00:00:16,250
            Well, I'm the only one here.
            """;

        [Fact]
        public void ReadsCuesAndTimings()
        {
            var cues = SrtParser.Parse(Simple);

            Assert.Equal(2, cues.Count);
            Assert.Equal(TimeSpan.FromSeconds(10.5), cues[0].Start);
            Assert.Equal(TimeSpan.FromSeconds(12), cues[0].End);
            Assert.Equal("You talking to me?", cues[0].Text);
        }

        [Fact]
        public void SurvivesABom()
        {
            var cues = SrtParser.Parse("\uFEFF" + Simple);
            Assert.Equal(2, cues.Count);
        }

        [Fact]
        public void AcceptsPeriodDecimalSeparator()
        {
            var cues = SrtParser.Parse("1\n00:00:01.250 --> 00:00:02.000\nHello.\n");
            Assert.Equal(TimeSpan.FromSeconds(1.25), cues[0].Start);
        }

        [Fact]
        public void AcceptsTimingsWithNoHours()
        {
            var cues = SrtParser.Parse("1\n01:05,000 --> 01:07,000\nHello.\n");
            Assert.Equal(TimeSpan.FromSeconds(65), cues[0].Start);
        }

        [Fact]
        public void IgnoresTrailingPositionData()
        {
            var cues = SrtParser.Parse("1\n00:00:01,000 X1:100 --> 00:00:02,000 X2:200\nHello.\n");
            Assert.Single(cues);
            Assert.Equal(TimeSpan.FromSeconds(1), cues[0].Start);
        }

        [Fact]
        public void RecoversWhenBlankLinesAreMissing()
        {
            // The sequence number of the next cue gets swallowed by this one's body.
            var cues = SrtParser.Parse(
                "1\n00:00:01,000 --> 00:00:02,000\nFirst.\n2\n00:00:03,000 --> 00:00:04,000\nSecond.\n");

            Assert.Equal(2, cues.Count);
            Assert.Equal("First.", cues[0].Text);
            Assert.Equal("Second.", cues[1].Text);
        }

        [Fact]
        public void ParsesDespiteBrokenSequenceNumbers()
        {
            var cues = SrtParser.Parse(
                "\n00:00:01,000 --> 00:00:02,000\nFirst.\n\nnot-a-number\n00:00:03,000 --> 00:00:04,000\nSecond.\n");

            Assert.Equal(2, cues.Count);
        }

        [Fact]
        public void JoinsMultiLineBodiesWithSpaces()
        {
            var cues = SrtParser.Parse("1\n00:00:01,000 --> 00:00:02,000\n- Yes.\n- No.\n");
            Assert.Equal("- Yes. - No.", cues[0].Text);
        }

        [Fact]
        public void ClampsAnEndBeforeItsStart()
        {
            var cues = SrtParser.Parse("1\n00:00:05,000 --> 00:00:01,000\nHello.\n");
            Assert.Equal(cues[0].Start, cues[0].End);
        }

        [Fact]
        public void ReturnsNothingForNothing()
        {
            Assert.Empty(SrtParser.Parse(null));
            Assert.Empty(SrtParser.Parse("   "));
            Assert.Empty(SrtParser.Parse("this is not a subtitle file"));
        }
    }

    public class CueCleanerTests
    {
        [Theory]
        [InlineData("[door creaks]", "")]
        [InlineData("(SIRENS WAILING)", "")]
        [InlineData("<i>Hello there.</i>", "Hello there.")]
        [InlineData("{\\an8}Hello there.", "Hello there.")]
        [InlineData("VINCENT: Say what again.", "Say what again.")]
        [InlineData("- Get out.", "Get out.")]
        [InlineData("[sighs] I'm tired.", "I'm tired.")]
        [InlineData("♪ ominous music ♪", "")]
        [InlineData("Subtitles by explosiveskull", "")]
        [InlineData("www.opensubtitles.org", "")]
        public void StripsWhatNobodySaid(string raw, string expected)
        {
            Assert.Equal(expected, CueCleaner.CleanLine(raw));
        }

        [Fact]
        public void KeepsLyricsWhileDroppingMusicDescriptions()
        {
            // The distinction the plugin has to make: a transcript will contain sung
            // words and will never contain the phrase "ominous music".
            Assert.Equal("Let it go, let it go, can't hold it back anymore",
                CueCleaner.CleanLine("♪ Let it go, let it go, can't hold it back anymore ♪"));

            Assert.Equal(string.Empty, CueCleaner.CleanLine("♪ dramatic music playing ♪"));
        }

        [Fact]
        public void DropsConsecutiveDuplicates()
        {
            var cues = new[]
            {
                new Cue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "Run."),
                new Cue(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), "Run."),
                new Cue(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6), "Now."),
            };

            var cleaned = CueCleaner.Clean(cues);

            Assert.Equal(2, cleaned.Count);
            Assert.Equal("Run.", cleaned[0].Text);
            Assert.Equal("Now.", cleaned[1].Text);
        }

        [Fact]
        public void KeepsTheOriginalTextAlongsideTheCleanedOne()
        {
            var cues = new[] { new Cue(TimeSpan.Zero, TimeSpan.FromSeconds(2), "<i>[gasps] Run.</i>") };
            var cleaned = CueCleaner.Clean(cues);

            Assert.Equal("Run.", cleaned[0].Text);
            Assert.Equal("<i>[gasps] Run.</i>", cleaned[0].Raw);
        }

        [Fact]
        public void DropsCuesLeftWithNoWords()
        {
            var cues = new[] { new Cue(TimeSpan.Zero, TimeSpan.FromSeconds(2), "[thunder] ...") };
            Assert.Empty(CueCleaner.Clean(cues));
        }
    }

    public class SrtWriterTests
    {
        [Fact]
        public void FormatsStampsWithCommas()
        {
            Assert.Equal("01:02:03,456", SrtWriter.Stamp(new TimeSpan(0, 1, 2, 3, 456)));
            Assert.Equal("00:00:00,000", SrtWriter.Stamp(TimeSpan.Zero));
        }

        [Fact]
        public void RoundTripsThroughTheParser()
        {
            var original = SrtParser.Parse("""
                1
                00:00:10,500 --> 00:00:12,000
                <i>You talking to me?</i>

                2
                00:00:14,000 --> 00:00:16,250
                Well, I'm the only one here.
                """);

            var reparsed = SrtParser.Parse(SrtWriter.Write(original));

            Assert.Equal(original.Count, reparsed.Count);
            Assert.Equal(original.Select(c => c.Text), reparsed.Select(c => c.Text));
            Assert.Equal(original.Select(c => c.Start), reparsed.Select(c => c.Start));
        }

        [Fact]
        public void ClampsCuesPushedBeforeZero()
        {
            var cues = new[] { new Cue(TimeSpan.FromSeconds(-3), TimeSpan.FromSeconds(-1), "Early.") };
            var written = SrtWriter.Write(cues);

            Assert.Contains("00:00:00,000 --> 00:00:00,000", written, StringComparison.Ordinal);
            Assert.Contains("Early.", written, StringComparison.Ordinal);
        }

        [Fact]
        public void RenumbersAndReordersCues()
        {
            var cues = new[]
            {
                new Cue(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(21), "Second."),
                new Cue(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11), "First."),
            };

            var written = SrtWriter.Write(cues);
            var reparsed = SrtParser.Parse(written);

            Assert.Equal("First.", reparsed[0].Text);
            Assert.StartsWith("1\r\n", written, StringComparison.Ordinal);
        }
    }
}
