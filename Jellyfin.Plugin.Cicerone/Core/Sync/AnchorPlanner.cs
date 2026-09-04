using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Cicerone.Core.Subtitles;

namespace Jellyfin.Plugin.Cicerone.Core.Sync
{
    /// <summary>A window of the media to listen to.</summary>
    /// <param name="Start">Where the window begins.</param>
    /// <param name="Duration">How long it runs.</param>
    /// <param name="ExpectedWords">
    /// How many distinct words the subtitles put in this window. The planner's own
    /// estimate of how much there is to align against, kept so a window that came
    /// back with nothing can be told apart from one that was never going to work.
    /// </param>
    public readonly record struct Anchor(TimeSpan Start, TimeSpan Duration, int ExpectedWords)
    {
        /// <summary>Gets where the window ends.</summary>
        public TimeSpan End => Start + Duration;
    }

    /// <summary>
    /// Chooses which few seconds of a film to actually listen to.
    /// </summary>
    /// <remarks>
    /// <b>Cost here is bounded by the number of anchors, not by the runtime.</b>
    /// Five thirty-second windows is two and a half minutes of audio whether the item
    /// is a twenty-minute episode or a three-hour film, so a library costs the same
    /// per item throughout and transcribing a whole film — which is the obvious
    /// approach and forty times the bill — buys nothing. The question being asked is
    /// only "where does this track sit", and a few seconds of speech answers it.
    /// <para>
    /// Windows avoid the head and tail of the runtime. Distributor logos, a cold open
    /// over music and the credit crawl are the parts of a film least likely to
    /// contain plain dialogue, and the head is also where a ripper's signature sits.
    /// </para>
    /// <para>
    /// They are placed where the <em>subtitles</em> are talkative, which is a proxy
    /// and worth being honest about: if the file is badly out of sync, cue density
    /// describes a moment other than the one the window will record. It survives that
    /// because talkative stretches of a film are minutes long rather than seconds, so
    /// a window chosen from cues an offset away still lands in conversation. When it
    /// does not, that anchor returns nothing and the fit uses the others — which is
    /// the reason there are five of them and not one.
    /// </para>
    /// </remarks>
    public static class AnchorPlanner
    {
        /// <summary>How finely a window is slid inside its slot when looking for speech.</summary>
        private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);

        /// <summary>Plans the windows to listen to.</summary>
        /// <param name="cues">The cleaned subtitle track.</param>
        /// <param name="runtime">The item's runtime.</param>
        /// <param name="count">How many windows to place.</param>
        /// <param name="window">How long each window is.</param>
        /// <param name="headTrimPercent">Share of the runtime skipped at the start.</param>
        /// <param name="tailTrimPercent">Share of the runtime skipped at the end.</param>
        /// <returns>The windows, in order, never overlapping.</returns>
        public static IReadOnlyList<Anchor> Plan(
            IReadOnlyList<CleanCue> cues,
            TimeSpan runtime,
            int count,
            TimeSpan window,
            double headTrimPercent = 5,
            double tailTrimPercent = 8)
        {
            ArgumentNullException.ThrowIfNull(cues);

            var anchors = new List<Anchor>();
            if (count <= 0 || window <= TimeSpan.Zero || cues.Count == 0)
            {
                return anchors;
            }

            // With no runtime from the metadata, the last cue is a lower bound on it
            // and a good enough one: subtitles run to the end of the dialogue, and
            // what comes after is credits this would have trimmed anyway.
            var total = runtime > TimeSpan.Zero ? runtime : cues[^1].End;
            if (total <= TimeSpan.Zero)
            {
                return anchors;
            }

            var head = total * (Math.Clamp(headTrimPercent, 0, 45) / 100.0);
            var tail = total * (Math.Clamp(tailTrimPercent, 0, 45) / 100.0);
            var from = head;
            var to = total - tail;

            // A very short item cannot spare the trims. Better to sample a title
            // sequence than to return no anchors and report the item unverifiable.
            if (to - from < window)
            {
                from = TimeSpan.Zero;
                to = total;
            }

            if (to - from < window)
            {
                // Still too short: one window over everything there is.
                anchors.Add(new Anchor(from, to - from, DistinctWords(cues, from, to)));
                return anchors;
            }

            // A slot narrower than the window cannot hold one, and placing them
            // anyway makes consecutive anchors overlap — which double-counts the same
            // seconds of audio as two independent measurements and lets one stretch of
            // dialogue vote twice in the fit. A short item gets fewer anchors instead,
            // and the fit reports how many it actually had.
            var room = (int)((to - from).TotalSeconds / window.TotalSeconds);
            count = Math.Max(1, Math.Min(count, room));

            var slot = (to - from) / count;
            for (var i = 0; i < count; i++)
            {
                var slotStart = from + (slot * i);
                var slotEnd = slotStart + slot;

                var best = slotStart;
                var bestScore = -1;

                // Slid rather than centred. The middle of a slot is as likely to be a
                // chase or a landscape as a conversation, and a window with four words
                // in it produces a measurement nobody should act on.
                for (var at = slotStart; at + window <= slotEnd; at += Step)
                {
                    var score = DistinctWords(cues, at, at + window);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = at;
                    }
                }

                if (best + window > to)
                {
                    best = to - window;
                }

                anchors.Add(new Anchor(best, window, Math.Max(bestScore, 0)));
            }

            return anchors;
        }

        /// <summary>Counts the distinct words the subtitles place inside a window.</summary>
        /// <param name="cues">The cleaned track.</param>
        /// <param name="from">Window start.</param>
        /// <param name="to">Window end.</param>
        /// <returns>How many different words are said in it.</returns>
        /// <remarks>
        /// Distinct rather than total, because the aligner only counts a word once
        /// per pairing and drops the over-frequent ones outright. A window of one
        /// character repeating a name scores badly here for exactly the reason it
        /// would align badly.
        /// </remarks>
        public static int DistinctWords(IReadOnlyList<CleanCue> cues, TimeSpan from, TimeSpan to)
        {
            ArgumentNullException.ThrowIfNull(cues);

            var words = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cue in cues)
            {
                if (cue.End < from)
                {
                    continue;
                }

                if (cue.Start > to)
                {
                    break;
                }

                foreach (var word in Tokenizer.Words(cue.Text))
                {
                    if (word.Length >= 2)
                    {
                        words.Add(word);
                    }
                }
            }

            return words.Count;
        }

        /// <summary>Collects the cue words falling inside a window, timed.</summary>
        /// <param name="cues">The cleaned track.</param>
        /// <param name="anchor">The window.</param>
        /// <param name="slackSeconds">
        /// How far outside the window cues may still be read from. This must be at
        /// least the largest offset being searched for, or the words that would prove
        /// a large offset are the exact ones excluded from the evidence.
        /// </param>
        /// <returns>The words with their subtitle times.</returns>
        public static IReadOnlyList<TimedToken> CueTokens(
            IReadOnlyList<CleanCue> cues,
            Anchor anchor,
            double slackSeconds)
        {
            ArgumentNullException.ThrowIfNull(cues);

            var slack = TimeSpan.FromSeconds(Math.Max(slackSeconds, 0));
            var from = anchor.Start - slack;
            var to = anchor.End + slack;

            var tokens = new List<TimedToken>();
            foreach (var cue in cues)
            {
                if (cue.End < from)
                {
                    continue;
                }

                if (cue.Start > to)
                {
                    break;
                }

                tokens.AddRange(Tokenizer.Spread(cue.Text, cue.Start, cue.End));
            }

            return tokens;
        }
    }
}
