using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Cicerone.Core.Sync
{
    /// <summary>
    /// How one window's subtitles line up against what was actually said in it.
    /// </summary>
    /// <param name="Offset">
    /// How far the subtitles are from the audio. Positive means the subtitle is
    /// late — the line appears after it is spoken.
    /// </param>
    /// <param name="Confidence">
    /// 0 to 1. The share of the evidence that agrees on <paramref name="Offset"/>,
    /// discounted by how close the strongest rival came.
    /// </param>
    /// <param name="Votes">How many word pairs contributed. Below a handful, ignore the answer.</param>
    /// <param name="RunnerUp">The best offset well away from the winner, or null when there was none.</param>
    public readonly record struct OffsetMeasurement(
        TimeSpan Offset,
        double Confidence,
        int Votes,
        TimeSpan? RunnerUp)
    {
        /// <summary>An unusable measurement: nothing matched.</summary>
        public static OffsetMeasurement None => new(TimeSpan.Zero, 0, 0, null);
    }

    /// <summary>
    /// Finds the shift that best explains a window of subtitles against a
    /// transcript of the same window's audio.
    /// </summary>
    /// <remarks>
    /// <b>This is a vote, not a search.</b> Every word appearing on both sides
    /// proposes one offset — the gap between where the subtitle puts it and where it
    /// was heard — and the offsets are histogrammed. The true shift is the only value
    /// hundreds of unrelated words can agree on by anything other than chance, so it
    /// stands up as a spike while wrong pairings smear out flat. Sliding the whole
    /// track against the transcript and scoring each position would give the same
    /// answer for far more arithmetic, because almost every position it evaluates
    /// has no evidence for it at all.
    /// <para>
    /// It also degrades in the right direction. A transcript that came back garbled,
    /// a window that turned out to be silent, or a subtitle file for a different film
    /// produce many votes and no agreement, which reads as low confidence rather than
    /// as a confident wrong answer.
    /// </para>
    /// </remarks>
    public static class OffsetSearch
    {
        /// <summary>Bin width for the vote histogram.</summary>
        /// <remarks>
        /// 200 ms. Finer than the tolerance a viewer notices (about a third of a
        /// second) and coarser than the error in placing a word inside its own cue,
        /// which is where the real uncertainty lives. Narrowing it does not buy
        /// precision, it splits one spike across two bins.
        /// </remarks>
        public const double BinSeconds = 0.2;

        /// <summary>
        /// How often a word may appear on either side before it is discarded.
        /// </summary>
        /// <remarks>
        /// The stopword list Cicerone does not have. A word occurring twenty times in
        /// a window carries no positional information — every one of its four hundred
        /// pairings is a guess — and it would contribute more raw votes than the
        /// rare words that actually locate the window. Dropping by observed frequency
        /// works in every language, which is the point: an English stopword list does
        /// nothing for a Polish track.
        /// </remarks>
        public const int MaxOccurrences = 6;

        /// <summary>
        /// How far apart two peaks must be before the second counts as a rival.
        /// </summary>
        /// <remarks>
        /// The bins either side of a spike are part of the same spike — a word
        /// misplaced inside its cue lands one bin over — so a rival has to be a
        /// genuinely different explanation, not the shoulder of the winner.
        /// </remarks>
        public const double RivalSeparationSeconds = 2.0;

        /// <summary>
        /// Measures the offset between a window's cues and its transcript.
        /// </summary>
        /// <param name="cueTokens">Words from the subtitle track, timed.</param>
        /// <param name="heardTokens">Words from the transcript, timed.</param>
        /// <param name="maxOffsetSeconds">
        /// How far out the subtitles may be assumed to be. Pairs further apart than
        /// this are not considered at all, which is what keeps the vote from
        /// degenerating into every word against every other word.
        /// </param>
        /// <returns>The measurement, or <see cref="OffsetMeasurement.None"/>.</returns>
        public static OffsetMeasurement Measure(
            IReadOnlyList<TimedToken> cueTokens,
            IReadOnlyList<TimedToken> heardTokens,
            double maxOffsetSeconds)
        {
            ArgumentNullException.ThrowIfNull(cueTokens);
            ArgumentNullException.ThrowIfNull(heardTokens);

            if (cueTokens.Count == 0 || heardTokens.Count == 0 || maxOffsetSeconds <= 0)
            {
                return OffsetMeasurement.None;
            }

            var heardByWord = Index(heardTokens);
            var cuesByWord = Index(cueTokens);

            var bins = new Dictionary<int, double>();
            var votes = 0;

            foreach (var (word, cueTimes) in cuesByWord)
            {
                if (cueTimes.Count > MaxOccurrences
                    || !heardByWord.TryGetValue(word, out var heardTimes)
                    || heardTimes.Count > MaxOccurrences)
                {
                    continue;
                }

                // Every pairing of one word type carries the same total weight
                // however many times it occurs, so a word said three times cannot
                // outvote three different words said once. Rarity is the whole
                // signal: a word appearing once on each side is one unambiguous
                // reading of where this window sits.
                var weight = 1.0 / (cueTimes.Count * heardTimes.Count);

                foreach (var cueTime in cueTimes)
                {
                    foreach (var heardTime in heardTimes)
                    {
                        var delta = (cueTime - heardTime).TotalSeconds;
                        if (Math.Abs(delta) > maxOffsetSeconds)
                        {
                            continue;
                        }

                        var bin = (int)Math.Round(delta / BinSeconds, MidpointRounding.AwayFromZero);
                        bins[bin] = bins.GetValueOrDefault(bin) + weight;
                        votes++;
                    }
                }
            }

            if (votes == 0)
            {
                return OffsetMeasurement.None;
            }

            var total = bins.Values.Sum();
            if (total <= 0)
            {
                return OffsetMeasurement.None;
            }

            // The peak is located on a smoothed histogram, because a spike straddling
            // a bin boundary arrives as two half-height neighbours and would otherwise
            // lose to a solid bin of noise elsewhere.
            var smoothed = Smooth(bins);
            var peak = smoothed.MaxBy(kv => kv.Value);
            var peakSeconds = peak.Key * BinSeconds;

            // It is then *measured* on the raw histogram, over the spike's own width.
            // Smoothing triples the mass it distributes, so a share read off it would
            // cap a flawless match at one half — and every threshold downstream would
            // silently be calibrated against a scale whose maximum is not one.
            var agreeing = bins.GetValueOrDefault(peak.Key - 1)
                + bins.GetValueOrDefault(peak.Key)
                + bins.GetValueOrDefault(peak.Key + 1);

            var rivalBins = smoothed
                .Where(kv => Math.Abs((kv.Key * BinSeconds) - peakSeconds) >= RivalSeparationSeconds)
                .ToList();

            var rival = rivalBins.Count > 0 ? rivalBins.MaxBy(kv => kv.Value) : default;
            var rivalWeight = rivalBins.Count > 0 ? rival.Value : 0.0;

            // Two independent doubts, multiplied. The first asks how much of the
            // evidence chose this offset at all; the second asks whether anything
            // else came close. A window can fail either — a broad smear with a
            // slightly tall bin, or two equally good answers — and both mean the
            // same thing downstream: do not trust this anchor.
            var share = agreeing / total;
            var margin = 1.0 - (rivalWeight / peak.Value);
            var confidence = Math.Clamp(share * margin, 0, 1);

            return new OffsetMeasurement(
                TimeSpan.FromSeconds(peakSeconds),
                confidence,
                votes,
                rivalBins.Count > 0 ? TimeSpan.FromSeconds(rival.Key * BinSeconds) : null);
        }

        private static Dictionary<string, List<TimeSpan>> Index(IReadOnlyList<TimedToken> tokens)
        {
            var index = new Dictionary<string, List<TimeSpan>>(StringComparer.Ordinal);
            foreach (var token in tokens)
            {
                // One-character words are punctuation survivors and CJK fragments
                // that pair with everything. They cost more in noise than they add.
                if (token.Word.Length < 2)
                {
                    continue;
                }

                if (!index.TryGetValue(token.Word, out var times))
                {
                    times = [];
                    index[token.Word] = times;
                }

                times.Add(token.At);
            }

            return index;
        }

        /// <summary>Adds each bin's neighbours at half weight.</summary>
        private static Dictionary<int, double> Smooth(Dictionary<int, double> bins)
        {
            var smoothed = new Dictionary<int, double>(bins.Count);
            foreach (var (bin, weight) in bins)
            {
                smoothed[bin] = smoothed.GetValueOrDefault(bin) + weight;
                smoothed[bin - 1] = smoothed.GetValueOrDefault(bin - 1) + (weight / 2);
                smoothed[bin + 1] = smoothed.GetValueOrDefault(bin + 1) + (weight / 2);
            }

            return smoothed;
        }
    }
}
