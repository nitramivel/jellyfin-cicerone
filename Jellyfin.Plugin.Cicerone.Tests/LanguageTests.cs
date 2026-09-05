using System.Linq;
using Jellyfin.Plugin.Cicerone.Core.Language;
using Xunit;

namespace Jellyfin.Plugin.Cicerone.Tests
{
    public class LanguageCodesTests
    {
        [Theory]
        [InlineData("eng", "en")]
        [InlineData("ENG", "en")]
        [InlineData("en", "en")]
        [InlineData("en-US", "en")]
        [InlineData("pt-BR", "pt")]
        [InlineData("zh-Hans", "zh")]
        [InlineData("fre", "fr")]
        [InlineData("fra", "fr")]
        [InlineData("English", "en")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void ReducesEverySpellingToOneCode(string? tag, string expected)
        {
            Assert.Equal(expected, LanguageCodes.Normalize(tag));
        }

        [Fact]
        public void RegionsAndScriptsAreNotDifferentLanguages()
        {
            // Brazilian and European Portuguese are the same answer to "is this
            // written in the language the track claims".
            Assert.True(LanguageCodes.Same("pt-BR", "pt-PT"));
            Assert.True(LanguageCodes.Same("en", "eng"));
            Assert.False(LanguageCodes.Same("en", "es"));
        }

        [Fact]
        public void AnUntaggedTrackMatchesNothing()
        {
            Assert.False(LanguageCodes.Same(null, "en"));
            Assert.False(LanguageCodes.Same("", ""));
        }

        [Fact]
        public void NamesLanguagesForTheReport()
        {
            Assert.Equal("English", LanguageCodes.Name("eng"));
            Assert.Equal("Japanese", LanguageCodes.Name("ja"));
            Assert.Equal("untagged", LanguageCodes.Name(""));
            Assert.Equal("xx", LanguageCodes.Name("xx"));
        }
    }

    public class LanguageProfileTests
    {
        private const string English =
            "The thing about the whole business is that you were never going to be able to fix it, "
            + "and you knew that from the start. I have not seen anything like this before, but what "
            + "I can tell you is that we have to leave now, with or without the others.";

        private const string Spanish =
            "No sé qué es lo que quieres de mí, pero no voy a dejar que te lleves a la niña. "
            + "Ella no es tuya. El coche está en la calle, con las llaves puestas. "
            + "Vete de aquí, por favor, antes de que llame a la policía.";

        private const string German =
            "Ich weiß nicht, was du von mir willst, aber ich werde dich nicht gehen lassen. "
            + "Das ist nicht dein Auto und der Schlüssel ist nicht dein Schlüssel. "
            + "Wir müssen jetzt gehen, mit oder ohne die anderen, und zwar sofort. "
            + "Ich habe so etwas noch nie gesehen, aber ich kann dir nicht sagen, was jetzt passiert.";

        [Fact]
        public void ReadsEnglish()
        {
            Assert.Equal("en", LanguageProfile.Identify(English)[0].Code);
        }

        [Fact]
        public void ReadsSpanish()
        {
            Assert.Equal("es", LanguageProfile.Identify(Spanish)[0].Code);
        }

        [Fact]
        public void ReadsGerman()
        {
            Assert.Equal("de", LanguageProfile.Identify(German)[0].Code);
        }

        [Theory]
        [InlineData("안녕하세요. 저는 오늘 학교에 갔습니다. 날씨가 정말 좋았어요. 친구들과 함께 점심을 먹었습니다.", "ko")]
        [InlineData("こんにちは。今日は学校に行きました。天気がとても良かったです。友達と一緒に昼ご飯を食べました。", "ja")]
        [InlineData("今天我去了学校。天气非常好。我和朋友一起吃了午饭。我们聊了很久然后回家了。真的很开心。", "zh")]
        [InlineData("สวัสดีครับ วันนี้ผมไปโรงเรียนมา อากาศดีมากเลยครับ ผมกินข้าวกลางวันกับเพื่อน", "th")]
        public void ScriptDecidesWhereItCan(string text, string expected)
        {
            // Text in Hangul is Korean and no word list is going to argue.
            var guesses = LanguageProfile.Identify(text);

            Assert.NotEmpty(guesses);
            Assert.Equal(expected, guesses[0].Code);
        }

        [Fact]
        public void KanaSeparatesJapaneseFromChinese()
        {
            // Japanese is written with both kana and han; Chinese uses no kana at all.
            // Any appreciable kana means Japanese however much han sits beside it.
            Assert.Equal("ja", LanguageProfile.Identify(
                "彼は学校に行きました。天気がとても良かったです。友達と一緒に帰りました。")[0].Code);

            Assert.Equal("zh", LanguageProfile.Identify(
                "他去了学校。天气非常好。他和朋友一起回家了。今天真的很开心。我们明天再见。")[0].Code);
        }

        [Fact]
        public void TooLittleTextIsNoAnswer()
        {
            Assert.Empty(LanguageProfile.Identify("Hello there."));
            Assert.Empty(LanguageProfile.Identify(""));
            Assert.Empty(LanguageProfile.Identify(null));
        }

        [Fact]
        public void CatchesATrackThatIsNotTheLanguageItClaims()
        {
            // The fault this exists to find: the player shows the language you picked,
            // the words are wrong, and no amount of sync checking explains it.
            Assert.True(LanguageProfile.Contradicts(Spanish, "eng", out var detected));
            Assert.Equal("es", detected.Code);
        }

        [Fact]
        public void LeavesACorrectlyTaggedTrackAlone()
        {
            Assert.False(LanguageProfile.Contradicts(English, "eng", out _));
            Assert.False(LanguageProfile.Contradicts(Spanish, "spa", out _));
            Assert.False(LanguageProfile.Contradicts(German, "ger", out _));
        }

        [Fact]
        public void NeverContradictsATrackThatMadeNoClaim()
        {
            Assert.False(LanguageProfile.Contradicts(Spanish, null, out _));
            Assert.False(LanguageProfile.Contradicts(Spanish, "", out _));
        }

        [Fact]
        public void NeverContradictsALanguageItHasNoProfileFor()
        {
            // The winner is only the best of the languages actually tested. A track
            // tagged with one Cicerone cannot read scores zero however well the text
            // fits it, and calling that wrong would be a false accusation.
            Assert.False(LanguageProfile.Contradicts(Spanish, "swa", out _));
        }

        [Fact]
        public void TooLittleTextIsNeverAContradiction()
        {
            Assert.False(LanguageProfile.Contradicts("Sí.", "eng", out _));
        }

        [Fact]
        public void DescribesAGuessForTheReport()
        {
            var guess = LanguageProfile.Identify(Spanish).First();
            Assert.Contains("Spanish", LanguageProfile.Describe(guess), System.StringComparison.Ordinal);
        }
    }
}
