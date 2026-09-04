using System;
using Jellyfin.Plugin.Cicerone.Core.Audio;
using Jellyfin.Plugin.Cicerone.Services;
using Xunit;

namespace Jellyfin.Plugin.Cicerone.Tests
{
    public class AudioPlanTests
    {
        private const string Path = "/media/movies/Film (2019)/Film (2019).mkv";

        private static string Clip(double start, double duration = 30, int index = 1) =>
            AudioPlan.ClipArguments(
                Path, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(duration), index);

        [Fact]
        public void SeeksEarlyAndTrimsBackToTheExactSecond()
        {
            // The fast/accurate idiom. A coarse container seek two seconds early, then
            // an exact decode-and-discard of those two seconds. Either half alone is
            // wrong: the coarse seek lands on a keyframe, and an exact seek from zero
            // decodes an hour of film to reach minute sixty.
            var arguments = Clip(600);

            Assert.Contains("-ss 598 -i", arguments, StringComparison.Ordinal);
            Assert.Contains("-ss 2 -t 30", arguments, StringComparison.Ordinal);
        }

        [Fact]
        public void AWindowNearTheStartTakesWhatPrerollThereIs()
        {
            // Whatever comes off the coarse seek has to come off the trim as well, or
            // the clip starts late by the difference — and that difference would read
            // downstream as a real, confident subtitle offset.
            Assert.Contains("-ss 0 -i", Clip(1), StringComparison.Ordinal);
            Assert.Contains("-ss 1 -t 30", Clip(1), StringComparison.Ordinal);

            Assert.Contains("-ss 0 -i", Clip(0), StringComparison.Ordinal);
            Assert.Contains("-ss 0 -t 30", Clip(0), StringComparison.Ordinal);
        }

        [Fact]
        public void TheSeekComesBeforeTheInputAndTheTrimAfterIt()
        {
            var arguments = Clip(600);

            var coarse = arguments.IndexOf("-ss 598", StringComparison.Ordinal);
            var input = arguments.IndexOf("-i \"", StringComparison.Ordinal);
            var trim = arguments.IndexOf("-ss 2", StringComparison.Ordinal);

            Assert.True(coarse >= 0 && input > coarse && trim > input);
        }

        [Fact]
        public void TakesTheStreamByItsIndexInTheFile()
        {
            // 0:a:1 means "the second audio stream"; the index Jellyfin reports is the
            // stream's position among all streams. Confusing the two reads a
            // commentary track on a disc rip.
            Assert.Contains("-map 0:3", Clip(600, index: 3), StringComparison.Ordinal);
            Assert.DoesNotContain("0:a:", Clip(600, index: 3), StringComparison.Ordinal);
        }

        [Fact]
        public void ResamplesToWhatSpeechModelsActuallyWant()
        {
            var arguments = Clip(600);

            Assert.Contains("-ac 1", arguments, StringComparison.Ordinal);
            Assert.Contains("-ar 16000", arguments, StringComparison.Ordinal);
            Assert.Contains("-vn -sn -dn", arguments, StringComparison.Ordinal);
        }

        [Fact]
        public void QuotesThePathAndWritesToStdout()
        {
            var arguments = Clip(600);

            Assert.Contains("\"" + Path + "\"", arguments, StringComparison.Ordinal);
            Assert.EndsWith("pipe:1", arguments, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(ClipFormat.Opus, "libopus")]
        [InlineData(ClipFormat.Mp3, "libmp3lame")]
        [InlineData(ClipFormat.Wav, "pcm_s16le")]
        public void EncodesInTheRequestedContainer(ClipFormat format, string codec)
        {
            var arguments = AudioPlan.ClipArguments(
                Path, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(30), 1, format);

            Assert.Contains(codec, arguments, StringComparison.Ordinal);
        }

        [Fact]
        public void FormatsTimesInvariantly()
        {
            Assert.Equal("598", AudioPlan.Seconds(598));
            Assert.Equal("598.25", AudioPlan.Seconds(598.25));
            Assert.Equal("0", AudioPlan.Seconds(-5));
        }
    }

    public class AudioTrackChoiceTests
    {
        [Fact]
        public void PrefersTheAudioInTheSubtitlesOwnLanguage()
        {
            // A release carrying a German dub as its default, checked against an
            // English subtitle file, shares almost no words with it — so every anchor
            // returns votes and no agreement, and a perfectly good file is reported as
            // a different cut.
            var tracks = new[]
            {
                new AudioTrack(1, "ger", true, 6, null),
                new AudioTrack(2, "eng", false, 6, null),
            };

            Assert.Equal(2, AudioPlan.ChooseAudio(tracks, "en")!.Index);
        }

        [Fact]
        public void FallsBackToTheDefaultTrackWhenNothingMatches()
        {
            var tracks = new[]
            {
                new AudioTrack(1, "ger", false, 6, null),
                new AudioTrack(2, "jpn", true, 6, null),
            };

            Assert.Equal(2, AudioPlan.ChooseAudio(tracks, "fr")!.Index);
        }

        [Fact]
        public void ACommentaryLosesToTheFeatureMix()
        {
            // Words spoken over the film rather than in it. An alignment against one
            // measures nothing.
            var tracks = new[]
            {
                new AudioTrack(1, "eng", false, 2, "Director's Commentary"),
                new AudioTrack(2, "eng", false, 6, null),
            };

            Assert.Equal(2, AudioPlan.ChooseAudio(tracks, "en")!.Index);
        }

        [Fact]
        public void NoAudioIsNoChoice()
        {
            Assert.Null(AudioPlan.ChooseAudio([], "en"));
        }

        [Fact]
        public void AnUntaggedSubtitleTakesTheDefaultAudio()
        {
            var tracks = new[]
            {
                new AudioTrack(1, "eng", false, 2, null),
                new AudioTrack(2, "eng", true, 6, null),
            };

            Assert.Equal(2, AudioPlan.ChooseAudio(tracks, null)!.Index);
        }
    }

    public class SidecarNameTests
    {
        [Fact]
        public void CarriesTheLanguageJellyfinWillReadBackOut()
        {
            Assert.Equal(
                "Film (2019).en.cicerone.srt",
                RepairWriter.SidecarName("Film (2019).mkv", "eng", "cicerone", false));
        }

        [Fact]
        public void KeepsTheHearingImpairedFlag()
        {
            // Dropped, a repaired SDH track appears as a second, mysteriously
            // duplicated language in the viewer's menu.
            Assert.Equal(
                "Film (2019).en.sdh.cicerone.srt",
                RepairWriter.SidecarName("Film (2019).mkv", "eng", "cicerone", true));
        }

        [Fact]
        public void TheMarkerIsAlwaysLastSoJellyfinReadsTheLanguageFirst()
        {
            var name = RepairWriter.SidecarName("Film.mkv", "spa", ".cicerone.", false);

            Assert.Equal("Film.es.cicerone.srt", name);
        }

        [Fact]
        public void AnUntaggedTrackJustGetsTheMarker()
        {
            Assert.Equal("Film.cicerone.srt", RepairWriter.SidecarName("Film.mkv", "", "cicerone", false));
        }

        [Fact]
        public void AnEmptySuffixFallsBackToTheDefaultMarker()
        {
            // The marker is what makes Cicerone's own output identifiable, so it can
            // never be allowed to vanish — nothing may overwrite a file it did not
            // write.
            Assert.Equal("Film.en.cicerone.srt", RepairWriter.SidecarName("Film.mkv", "en", "  ", false));
        }
    }
}
