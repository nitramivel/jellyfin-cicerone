using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Cicerone.Core.Runs
{
    /// <summary>How a run ended, or that it has not.</summary>
    public enum RunStatus
    {
        /// <summary>Still going.</summary>
        Running = 0,

        /// <summary>Finished.</summary>
        Completed = 1,

        /// <summary>Stopped by the owner or by the task manager.</summary>
        Cancelled = 2,

        /// <summary>Threw.</summary>
        Failed = 3,

        /// <summary>
        /// The file still says running and no process is behind it.
        /// </summary>
        /// <remarks>
        /// Worked out when the file is read, never written. Installing any plugin
        /// tears Jellyfin's host down in process, so a run started beforehand simply
        /// stops existing — and the one thing a dead process cannot do is update its
        /// own file to say it died. Reporting this as what it is beats reporting a
        /// run that has been "running" for nine days.
        /// </remarks>
        Abandoned = 4,
    }

    /// <summary>What one item cost and what came of it.</summary>
    /// <param name="ItemId">The Jellyfin item.</param>
    /// <param name="Name">Its name at the time of the run.</param>
    /// <param name="Tracks">One line per subtitle track examined.</param>
    /// <param name="ClipsTranscribed">How many audio windows were actually sent.</param>
    /// <param name="AudioSeconds">How much audio was transcribed, in seconds.</param>
    /// <param name="CostUsd">What that came to, or null when no price was configured.</param>
    /// <param name="ElapsedMs">Wall clock for the item.</param>
    /// <param name="Error">Why it failed, when it did.</param>
    public sealed record RunItem(
        Guid ItemId,
        string Name,
        IReadOnlyList<RunTrack> Tracks,
        int ClipsTranscribed,
        double AudioSeconds,
        decimal? CostUsd,
        long ElapsedMs,
        string? Error);

    /// <summary>One track's line in the run log.</summary>
    /// <param name="Index">The stream index.</param>
    /// <param name="Language">The language it claims.</param>
    /// <param name="Verdict">What Cicerone concluded, as a name.</param>
    /// <param name="Reason">The sentence behind the verdict.</param>
    /// <param name="OffsetSeconds">The fitted constant offset.</param>
    /// <param name="Scale">The fitted time-scale factor.</param>
    /// <param name="WorstErrorSeconds">The largest error anywhere in the runtime.</param>
    /// <param name="Repaired">Where the repaired copy was written, when one was.</param>
    public sealed record RunTrack(
        int Index,
        string Language,
        string Verdict,
        string Reason,
        double OffsetSeconds,
        double Scale,
        double WorstErrorSeconds,
        string? Repaired);

    /// <summary>A whole run, as it is written to disk.</summary>
    /// <param name="Id">The run's identifier, which is also its file name.</param>
    /// <param name="StartedUtc">When it began.</param>
    /// <param name="FinishedUtc">When it ended, or null while it is going.</param>
    /// <param name="Status">How it ended.</param>
    /// <param name="Trigger">What started it — a schedule, a button, one item.</param>
    /// <param name="Planned">How many items the run intends to visit.</param>
    /// <param name="Items">Everything it has visited so far.</param>
    /// <param name="TotalCostUsd">The run's bill, or null when no price was configured.</param>
    /// <param name="Error">Why the run failed, when it did.</param>
    public sealed record RunLogDocument(
        string Id,
        DateTime StartedUtc,
        DateTime? FinishedUtc,
        RunStatus Status,
        string Trigger,
        int Planned,
        IReadOnlyList<RunItem> Items,
        decimal? TotalCostUsd,
        string? Error);
}
