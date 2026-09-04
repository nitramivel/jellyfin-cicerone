using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Cicerone.Core.Language
{
    /// <summary>
    /// Maps between the language codes a media file uses and a name to print.
    /// </summary>
    /// <remarks>
    /// Subtitle tracks in one library are tagged <c>en</c>, <c>eng</c>, <c>en-US</c>
    /// and occasionally <c>English</c>, because the tag comes from whoever muxed the
    /// file. Everything Cicerone compares goes through <see cref="Normalize"/> first,
    /// so no comparison anywhere else has to know that.
    /// </remarks>
    public static class LanguageCodes
    {
        private static readonly Dictionary<string, string> ThreeToTwo = new(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = "en", ["spa"] = "es", ["fre"] = "fr", ["fra"] = "fr", ["ger"] = "de",
            ["deu"] = "de", ["ita"] = "it", ["por"] = "pt", ["dut"] = "nl", ["nld"] = "nl",
            ["swe"] = "sv", ["nor"] = "no", ["nob"] = "no", ["dan"] = "da", ["fin"] = "fi",
            ["pol"] = "pl", ["cze"] = "cs", ["ces"] = "cs", ["slo"] = "sk", ["slk"] = "sk",
            ["rum"] = "ro", ["ron"] = "ro", ["hun"] = "hu", ["tur"] = "tr", ["rus"] = "ru",
            ["ukr"] = "uk", ["gre"] = "el", ["ell"] = "el", ["ara"] = "ar", ["heb"] = "he",
            ["hin"] = "hi", ["tha"] = "th", ["jpn"] = "ja", ["kor"] = "ko", ["chi"] = "zh",
            ["zho"] = "zh", ["vie"] = "vi", ["ind"] = "id", ["may"] = "ms", ["msa"] = "ms",
            ["bul"] = "bg", ["hrv"] = "hr", ["srp"] = "sr", ["slv"] = "sl", ["est"] = "et",
            ["lav"] = "lv", ["lit"] = "lt", ["cat"] = "ca", ["fas"] = "fa", ["per"] = "fa",
        };

        private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "English", ["es"] = "Spanish", ["fr"] = "French", ["de"] = "German",
            ["it"] = "Italian", ["pt"] = "Portuguese", ["nl"] = "Dutch", ["sv"] = "Swedish",
            ["no"] = "Norwegian", ["da"] = "Danish", ["fi"] = "Finnish", ["pl"] = "Polish",
            ["cs"] = "Czech", ["sk"] = "Slovak", ["ro"] = "Romanian", ["hu"] = "Hungarian",
            ["tr"] = "Turkish", ["ru"] = "Russian", ["uk"] = "Ukrainian", ["el"] = "Greek",
            ["ar"] = "Arabic", ["he"] = "Hebrew", ["hi"] = "Hindi", ["th"] = "Thai",
            ["ja"] = "Japanese", ["ko"] = "Korean", ["zh"] = "Chinese", ["vi"] = "Vietnamese",
            ["id"] = "Indonesian", ["ms"] = "Malay", ["bg"] = "Bulgarian", ["hr"] = "Croatian",
            ["sr"] = "Serbian", ["sl"] = "Slovenian", ["et"] = "Estonian", ["lv"] = "Latvian",
            ["lt"] = "Lithuanian", ["ca"] = "Catalan", ["fa"] = "Persian",
        };

        /// <summary>Reduces any spelling of a language tag to a two-letter code.</summary>
        /// <param name="code">The tag as the file has it.</param>
        /// <returns>The two-letter code, lowercased, or empty when there was nothing to read.</returns>
        public static string Normalize(string? code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return string.Empty;
            }

            var trimmed = code.Trim();

            // "pt-BR" and "zh-Hans" carry a region or a script that says nothing about
            // which words are in the file. Cicerone checks whether a track is written
            // in the language it claims, and Brazilian and European Portuguese are the
            // same answer to that question.
            var dash = trimmed.IndexOfAny(['-', '_']);
            if (dash > 0)
            {
                trimmed = trimmed[..dash];
            }

            if (trimmed.Length == 2)
            {
                return trimmed.ToLowerInvariant();
            }

            if (ThreeToTwo.TryGetValue(trimmed, out var two))
            {
                return two;
            }

            // A track tagged with a name rather than a code — rare, and cheap to read.
            foreach (var (key, name) in Names)
            {
                if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return key;
                }
            }

            return trimmed.ToLowerInvariant();
        }

        /// <summary>Names a language for the report.</summary>
        /// <param name="code">Any spelling of the code.</param>
        /// <returns>The English name, or the code itself when it is not one Cicerone knows.</returns>
        public static string Name(string? code)
        {
            var normalized = Normalize(code);
            if (normalized.Length == 0)
            {
                return "untagged";
            }

            return Names.TryGetValue(normalized, out var name) ? name : normalized;
        }

        /// <summary>Whether two tags name the same language.</summary>
        /// <param name="a">One tag.</param>
        /// <param name="b">The other.</param>
        /// <returns>True when they normalise to the same code.</returns>
        public static bool Same(string? a, string? b)
        {
            var left = Normalize(a);
            return left.Length > 0 && left.Equals(Normalize(b), StringComparison.Ordinal);
        }
    }
}
