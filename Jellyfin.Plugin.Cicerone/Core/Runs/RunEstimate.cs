using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Cicerone.Core.Runs
{
    /// <summary>
    /// Works out how long is left in a run from how fast it has been going.
    /// </summary>
    /// <remarks>
    /// Measured from <b>throughput</b> — completions per wall-clock second over a
    /// recent window — rather than from an average item duration. Items are checked
    /// several at a time, so a mean duration overstates the answer by the
    /// concurrency factor, and the whole point of the number is that somebody is
    /// deciding whether to wait for it.
    /// <para>
    /// Windowed because a library is not uniform: a run genuinely changes pace when
    /// it crosses from feature films into half-hour episodes, and an average over the
    /// whole run would take an hour to notice.
    /// </para>
    /// <para>
    /// Measured against <b>now</b> rather than against the last completion, so a run
    /// stalled on one enormous file shows a growing estimate instead of counting down
    /// through a hang.
    /// </para>
    /// </remarks>
    public static class RunEstimate
    {
        /// <summary>How many recent completions the rate is read from.</summary>
        public const int Window = 20;

        /// <summary>How many must have finished before any estimate is offered.</summary>
        /// <remarks>
        /// The first item pays for a cold HTTP handler and whatever the filesystem
        /// had not cached, so an estimate drawn from it is wrong by a factor and
        /// visibly so. Two is not enough to notice; three is.
        /// </remarks>
        public const int MinimumCompletions = 3;

        /// <summary>Estimates the time left.</summary>
        /// <param name="completionsUtc">When each finished item finished, oldest first.</param>
        /// <param name="remaining">How many items are left.</param>
        /// <param name="nowUtc">The current time.</param>
        /// <returns>The estimate, or null when there is not enough to go on.</returns>
        public static TimeSpan? TimeLeft(
            IReadOnlyList<DateTime> completionsUtc,
            int remaining,
            DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(completionsUtc);

            if (remaining <= 0)
            {
                return TimeSpan.Zero;
            }

            if (completionsUtc.Count < MinimumCompletions)
            {
                return null;
            }

            var recent = completionsUtc.Skip(Math.Max(0, completionsUtc.Count - Window)).ToList();

            // The span runs to now, not to the last completion. Those are the same
            // number while a run is healthy and diverge exactly when the owner most
            // wants the estimate to react.
            var span = (nowUtc - recent[0]).TotalSeconds;
            if (span <= 0)
            {
                return null;
            }

            // recent.Count - 1 intervals, not recent.Count: n completions bound n-1
            // gaps, and counting the extra one overstates the rate on a short window.
            var rate = (recent.Count - 1) / span;
            if (rate <= 0)
            {
                return null;
            }

            return TimeSpan.FromSeconds(remaining / rate);
        }
    }
}
