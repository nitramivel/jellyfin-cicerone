using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Cicerone.Core.Sync;

namespace Jellyfin.Plugin.Cicerone.Core.Language
{
    /// <summary>One candidate language and how well the text fits it.</summary>
    /// <param name="Code">Two-letter code.</param>
    /// <param name="Score">Share of the sampled words that are this language's function words, 0 to 1.</param>
    public readonly record struct LanguageGuess(string Code, double Score);

    /// <summary>
    /// Works out what language a subtitle track is actually written in.
    /// </summary>
    /// <remarks>
    /// <b>This is the cheapest useful thing Cicerone does and it runs first.</b>
    /// A track tagged English and written in Spanish is a common and completely
    /// invisible fault — the player shows the language you picked, the words are
    /// wrong, and no amount of sync checking explains it. Asking a model would cost
    /// a call per track to answer the one question in the plugin that a few hundred
    /// bytes of table answers exactly, so it is answered here, for nothing, before
    /// any audio is touched.
    /// <para>
    /// Two stages. Script decides first and decides absolutely: text in Hangul is
    /// Korean and no word list is going to argue. Within the Latin and Cyrillic
    /// scripts, where dozens of languages share an alphabet, the signal is function
    /// words — <i>the</i>, <i>und</i>, <i>que</i>, <i>ale</i>. They are the most
    /// frequent words in any language and almost never shared across two, which is
    /// exactly the property a short list needs to be decisive.
    /// </para>
    /// <para>
    /// It reports a ranked list rather than an answer because the honest output is
    /// sometimes ambiguous. Norwegian and Danish share most of their function words;
    /// Portuguese and Spanish share many. A verdict is only raised to
    /// <see cref="Sync.Verdict.WrongLanguage"/> when the tagged language is not in
    /// the running at all, never when it merely came second.
    /// </para>
    /// </remarks>
    public static class LanguageProfile
    {
        /// <summary>
        /// How far ahead of the tagged language the winner must be before the tag is
        /// called wrong.
        /// </summary>
        public const double ContradictionRatio = 2.5;

        /// <summary>How many words must be seen before any guess is offered.</summary>
        public const int MinimumWords = 40;

        /// <summary>
        /// Function words, by language. Short on purpose.
        /// </summary>
        /// <remarks>
        /// Every entry here is a word that is both very frequent in its own language
        /// and rare or absent in the others on this list. Adding more words does not
        /// improve the answer and does make the lists start to overlap, which is the
        /// one thing that breaks the method: a word shared by two languages
        /// contributes to both scores and separates nothing.
        /// </remarks>
        private static readonly Dictionary<string, string[]> FunctionWords = new(StringComparer.Ordinal)
        {
            ["en"] = ["the", "and", "you", "that", "was", "with", "this", "have", "not", "for", "but", "what"],
            ["es"] = ["que", "de", "no", "la", "el", "en", "es", "por", "una", "con", "para", "está"],
            ["pt"] = ["que", "não", "de", "uma", "com", "por", "para", "você", "está", "isso", "mas", "ele"],
            ["fr"] = ["que", "les", "des", "est", "pas", "vous", "pour", "dans", "une", "qui", "avec", "elle"],
            ["it"] = ["che", "non", "per", "una", "con", "sono", "come", "questo", "della", "anche", "più", "cosa"],
            ["de"] = ["und", "der", "die", "das", "ist", "nicht", "sie", "mit", "ich", "auf", "ein", "wir"],
            ["nl"] = ["het", "een", "van", "dat", "niet", "voor", "met", "maar", "zijn", "heb", "wat", "naar"],
            ["sv"] = ["och", "att", "det", "som", "inte", "för", "med", "har", "jag", "vi", "men", "här"],
            ["da"] = ["og", "det", "ikke", "til", "med", "har", "jeg", "var", "hun", "men", "hvad", "kan"],
            ["no"] = ["og", "det", "ikke", "til", "med", "har", "jeg", "var", "hun", "men", "hva", "kan"],
            ["fi"] = ["että", "ei", "on", "ja", "se", "mutta", "niin", "hän", "kun", "olen", "sinä", "tämä"],
            ["pl"] = ["nie", "się", "jest", "tak", "ale", "jak", "tego", "przez", "czy", "tylko", "wszystko", "bardzo"],
            ["cs"] = ["ale", "jsem", "jak", "tak", "co", "když", "jsi", "byl", "všechno", "protože", "jenom", "tady"],
            ["sk"] = ["som", "ale", "ako", "keď", "všetko", "prečo", "tam", "bol", "iba", "veľmi", "musíme", "tu"],
            ["ro"] = ["este", "care", "pentru", "din", "sunt", "dar", "cu", "nu", "așa", "acum", "ceva", "bine"],
            ["hu"] = ["hogy", "nem", "egy", "van", "meg", "csak", "már", "még", "ez", "mit", "kell", "volt"],
            ["tr"] = ["bir", "bu", "için", "ama", "değil", "çok", "ne", "gibi", "daha", "olarak", "var", "şey"],
            ["el"] = ["και", "να", "που", "δεν", "για", "με", "θα", "είναι", "στο", "από", "αλλά", "μου"],
            ["ru"] = ["не", "что", "это", "как", "но", "тебя", "меня", "мы", "они", "так", "все", "если"],
            ["uk"] = ["не", "що", "це", "як", "але", "тебе", "мене", "ми", "вони", "так", "все", "якщо"],
            ["bg"] = ["не", "да", "се", "на", "ще", "аз", "той", "но", "как", "това", "тук", "защо"],
            ["id"] = ["yang", "tidak", "dan", "ini", "untuk", "dengan", "itu", "kamu", "saya", "kita", "sudah", "bisa"],
            ["vi"] = ["không", "của", "một", "những", "được", "người", "này", "tôi", "chúng", "anh", "cho", "đã"],
            ["ca"] = ["que", "amb", "això", "però", "una", "els", "per", "és", "molt", "aquest", "aquí", "res"],
        };

        /// <summary>
        /// Letters that only a few languages use, which break ties the word lists
        /// cannot.
        /// </summary>
        /// <remarks>
        /// Ukrainian and Russian share almost every function word — the lists above
        /// are nearly identical because the languages genuinely are, in the words
        /// that occur most. What separates them is the alphabet: Ukrainian has
        /// і, ї, є and ґ, and Russian has ы, э and ъ. The same trick settles
        /// Norwegian against Danish, and Spanish against Portuguese.
        /// </remarks>
        private static readonly Dictionary<string, char[]> Signatures = new(StringComparer.Ordinal)
        {
            ["uk"] = ['і', 'ї', 'є', 'ґ'],
            ["ru"] = ['ы', 'э', 'ъ'],
            ["pl"] = ['ł', 'ż', 'ę', 'ą', 'ś', 'ń'],
            ["cs"] = ['ř', 'ů', 'ě'],
            ["sk"] = ['ľ', 'ĺ', 'ŕ'],
            ["hu"] = ['ő', 'ű'],
            ["ro"] = ['ș', 'ț', 'ă'],
            ["tr"] = ['ğ', 'ı', 'ş'],
            ["es"] = ['ñ', '¿', '¡'],
            ["pt"] = ['ã', 'õ', 'ç'],
            ["de"] = ['ß'],
            ["da"] = ['æ', 'ø'],
            ["no"] = ['æ', 'ø'],
            ["sv"] = ['å', 'ä', 'ö'],
            ["fi"] = ['ä', 'ö'],
            ["vi"] = ['ơ', 'ư', 'đ'],
        };

        /// <summary>Reads the language of a block of subtitle text.</summary>
        /// <param name="text">The cleaned dialogue, joined.</param>
        /// <param name="take">How many candidates to return.</param>
        /// <returns>The candidates, best first. Empty when there was too little text.</returns>
        public static IReadOnlyList<LanguageGuess> Identify(string? text, int take = 3)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            if (DetectScript(text) is { } scripted)
            {
                return [new LanguageGuess(scripted, 1.0)];
            }

            // Words rather than characters, and lowercased through the same tokenizer
            // the aligner uses — a function word list only matches text normalised the
            // same way it was written. Accents are stripped by that tokenizer, so the
            // signature letters are counted from the raw text below instead.
            var words = Tokenizer.Words(text);
            if (words.Count < MinimumWords)
            {
                return [];
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var word in words)
            {
                counts[word] = counts.GetValueOrDefault(word) + 1;
            }

            var lowered = text.ToLowerInvariant();
            var scores = new List<LanguageGuess>();

            foreach (var (code, markers) in FunctionWords)
            {
                var hits = 0;
                foreach (var marker in markers)
                {
                    // Through the tokenizer too, so "está" is looked up as "esta" —
                    // which is what stripping accents left in the counts.
                    var normalized = Tokenizer.Words(marker);
                    if (normalized.Count == 1)
                    {
                        hits += counts.GetValueOrDefault(normalized[0]);
                    }
                }

                var score = (double)hits / words.Count;

                // The signature letters are a multiplier rather than a score of their
                // own: they are decisive when present and mean nothing when absent,
                // since a page of Polish dialogue can genuinely contain no ł.
                if (Signatures.TryGetValue(code, out var signature) && lowered.IndexOfAny(signature) >= 0)
                {
                    score *= 1.6;
                }

                if (score > 0)
                {
                    scores.Add(new LanguageGuess(code, score));
                }
            }

            return scores
                .OrderByDescending(g => g.Score)
                .ThenBy(g => g.Code, StringComparer.Ordinal)
                .Take(Math.Max(take, 1))
                .ToList();
        }

        /// <summary>
        /// Decides whether a track's tag contradicts what is written in it.
        /// </summary>
        /// <param name="text">The cleaned dialogue.</param>
        /// <param name="taggedLanguage">The language the track claims to be.</param>
        /// <param name="detected">The winning guess, when there was one.</param>
        /// <returns>True only when the text is confidently some other language.</returns>
        /// <remarks>
        /// Deliberately reluctant. The cost of a false positive is telling somebody
        /// their perfectly good subtitle file is the wrong language, which is worse
        /// than missing one — so the tag has to be beaten by a clear margin, not
        /// merely beaten, and an untagged track is never contradicted at all because
        /// it never made a claim.
        /// </remarks>
        public static bool Contradicts(string? text, string? taggedLanguage, out LanguageGuess detected)
        {
            detected = default;

            var tagged = LanguageCodes.Normalize(taggedLanguage);
            var guesses = Identify(text, 5);
            if (guesses.Count == 0)
            {
                return false;
            }

            detected = guesses[0];

            if (tagged.Length == 0)
            {
                return false;
            }

            if (string.Equals(detected.Code, tagged, StringComparison.Ordinal))
            {
                return false;
            }

            // A language Cicerone has no profile for cannot be contradicted: the
            // winner is only the best of the languages that were actually tested, and
            // an unlisted one scores zero however well the text fits it.
            if (!FunctionWords.ContainsKey(tagged))
            {
                return false;
            }

            var taggedScore = guesses.FirstOrDefault(g => string.Equals(g.Code, tagged, StringComparison.Ordinal)).Score;
            return detected.Score >= taggedScore * ContradictionRatio;
        }

        /// <summary>Identifies text by its writing system, where that settles it.</summary>
        /// <returns>A language code, or null when the text is in a shared alphabet.</returns>
        private static string? DetectScript(string text)
        {
            int han = 0, kana = 0, hangul = 0, thai = 0, arabic = 0, hebrew = 0, devanagari = 0, letters = 0;

            foreach (var c in text)
            {
                if (!char.IsLetter(c))
                {
                    continue;
                }

                letters++;

                if (c is >= '\u3040' and <= '\u30FF')
                {
                    kana++;
                }
                else if (c is >= '\u4E00' and <= '\u9FFF')
                {
                    han++;
                }
                else if (c is >= '\uAC00' and <= '\uD7AF' or >= '\u1100' and <= '\u11FF')
                {
                    hangul++;
                }
                else if (c is >= '\u0E00' and <= '\u0E7F')
                {
                    thai++;
                }
                else if (c is >= '\u0600' and <= '\u06FF')
                {
                    arabic++;
                }
                else if (c is >= '\u0590' and <= '\u05FF')
                {
                    hebrew++;
                }
                else if (c is >= '\u0900' and <= '\u097F')
                {
                    devanagari++;
                }
            }

            if (letters < 20)
            {
                return null;
            }

            // A tenth is plenty. Subtitle files carry names, song titles and
            // occasional English in every language, so demanding a clean sweep would
            // fail on ordinary files; nothing else on this list appears by accident.
            var floor = letters / 10;

            if (hangul > floor)
            {
                return "ko";
            }

            // Kana before Han, and this order is the whole test: Japanese is written
            // with both and Chinese uses no kana at all, so any appreciable kana means
            // Japanese however much Han sits beside it.
            if (kana > floor)
            {
                return "ja";
            }

            if (han > floor)
            {
                return "zh";
            }

            if (thai > floor)
            {
                return "th";
            }

            if (arabic > floor)
            {
                return "ar";
            }

            if (hebrew > floor)
            {
                return "he";
            }

            if (devanagari > floor)
            {
                return "hi";
            }

            return null;
        }

        /// <summary>Renders a guess for the report.</summary>
        /// <param name="guess">The guess.</param>
        /// <returns>Something like <c>Spanish (31%)</c>.</returns>
        public static string Describe(LanguageGuess guess) => string.Create(
            CultureInfo.InvariantCulture,
            $"{LanguageCodes.Name(guess.Code)} ({guess.Score:P0})");
    }
}
