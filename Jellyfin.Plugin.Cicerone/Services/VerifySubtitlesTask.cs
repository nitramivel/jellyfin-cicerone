using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Cicerone.Services
{
    /// <summary>The scheduled task that checks the library.</summary>
    /// <remarks>
    /// <b>No default trigger.</b> Every other task in this family runs weekly out of
    /// the box; this one does not, because it is the only one that spends money the
    /// first time it fires. A plugin that quietly transcribed a thousand films the
    /// night it was installed would be indefensible however useful the answers were.
    /// Set a schedule once the first run's bill is a known quantity.
    /// </remarks>
    public sealed class VerifySubtitlesTask : IScheduledTask
    {
        private readonly VerifyRunService _runs;

        /// <summary>Initialises a new instance of the <see cref="VerifySubtitlesTask"/> class.</summary>
        /// <param name="runs">The run service.</param>
        public VerifySubtitlesTask(VerifyRunService runs)
        {
            _runs = runs;
        }

        /// <inheritdoc />
        public string Name => "Verify Subtitles";

        /// <inheritdoc />
        public string Key => "CiceroneVerifySubtitles";

        /// <inheritdoc />
        public string Description =>
            "Listens to a few seconds of dialogue at several points in each item and reports whether its "
            + "subtitles match, are out of sync, or drift.";

        /// <inheritdoc />
        public string Category => "Cicerone";

        /// <inheritdoc />
        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
            _runs.RunAsync("Scheduled task", progress, cancellationToken);

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
    }
}
