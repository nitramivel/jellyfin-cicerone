using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Cicerone.Configuration;
using Jellyfin.Plugin.Cicerone.Core.Reports;
using Jellyfin.Plugin.Cicerone.Core.Runs;
using Jellyfin.Plugin.Cicerone.Core.Sync;
using Jellyfin.Plugin.Cicerone.Services.Runs;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cicerone.Services
{
    /// <summary>
    /// Walks the library, checking items, and keeps the run honest about what it is
    /// spending.
    /// </summary>
    /// <remarks>
    /// One run at a time, always. Two runs over the same library would check the same
    /// items twice, write the same repairs twice, and bill for both.
    /// </remarks>
    public sealed class VerifyRunService
    {
        private readonly ILibraryManager _library;
        private readonly CheckService _checks;
        private readonly ReportStore _reports;
        private readonly RunLogStore _runs;
        private readonly ILogger<VerifyRunService> _logger;
        private readonly SemaphoreSlim _one = new(1, 1);

        /// <summary>Initialises a new instance of the <see cref="VerifyRunService"/> class.</summary>
        /// <param name="library">Library access.</param>
        /// <param name="checks">The per-item checker.</param>
        /// <param name="reports">Where results are kept.</param>
        /// <param name="runs">Run history and live progress.</param>
        /// <param name="logger">The logger.</param>
        public VerifyRunService(
            ILibraryManager library,
            CheckService checks,
            ReportStore reports,
            RunLogStore runs,
            ILogger<VerifyRunService> logger)
        {
            _library = library;
            _checks = checks;
            _reports = reports;
            _runs = runs;
            _logger = logger;
        }

        /// <summary>Gets whether a run is going.</summary>
        public bool Busy => _runs.Busy;

        /// <summary>Checks every eligible item.</summary>
        /// <param name="trigger">What started the run, for the log.</param>
        /// <param name="progress">Where progress is reported, for the task manager.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        public async Task RunAsync(
            string trigger,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

            if (!await _one.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("Cicerone: a run is already going, skipping this one");
                return;
            }

            try
            {
                var items = Eligible(config);
                var due = items.Where(i => config.RecheckExisting || Due(i)).ToList();

                var lanes = config.Lanes();

                // The estimate a lane holds while it works. One track's worth, which
                // is what the run is quoted at below; the real figure replaces it the
                // moment the report comes back.
                var estimate = config.AudioSecondsPerItem(1);
                var budget = new RunBudget(config.AudioMinuteBudget * 60.0);
                var done = 0;

                _runs.Begin(trigger, due.Count);
                _logger.LogInformation(
                    "Cicerone: checking {Due} of {Total} items, {Lanes} at a time "
                    + "({Minutes:0} minutes of audio at most)",
                    due.Count,
                    items.Count,
                    lanes,
                    due.Count * estimate / 60);

                await Parallel.ForEachAsync(
                    due,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = lanes,
                        CancellationToken = cancellationToken,
                    },
                    async (item, ct) =>
                    {
                        if (!budget.TryReserve(estimate))
                        {
                            // Returned rather than thrown, so the lanes still working
                            // finish the items they are holding. What remains costs a
                            // predicate each and no audio at all.
                            return;
                        }

                        _runs.Working(item.Name);

                        var stopwatch = Stopwatch.StartNew();
                        ItemReport report;

                        try
                        {
                            report = await _checks.CheckAsync(item, config, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // One item's failure is one item. A run over a library will
                            // meet an unreadable file, a provider outage and a container
                            // ffmpeg refuses, and stopping on any of them would mean a
                            // library is only ever checked as far as its first bad file.
                            _logger.LogWarning(ex, "Cicerone: failed to check {Item}", item.Name);
                            report = new ItemReport(
                                item.Id, item.Name ?? string.Empty, item is MediaBrowser.Controller.Entities.TV.Episode
                                    ? "Episode" : "Movie",
                                null, DateTime.UtcNow, string.Empty, 0, [], config.Languages(), 0, null, ex.Message);
                        }
                        finally
                        {
                            _runs.Left(item.Name);
                        }

                        stopwatch.Stop();
                        budget.Settle(estimate, report.AudioSeconds);

                        await _reports.SaveAsync(report, ct).ConfigureAwait(false);
                        _runs.Finished(Record(report, config, stopwatch.ElapsedMilliseconds));

                        // due.Count cannot be zero here: an empty list runs no body.
                        var finished = Interlocked.Increment(ref done);
                        progress?.Report(finished * 100.0 / due.Count);
                    }).ConfigureAwait(false);

                if (budget.Exhausted)
                {
                    _logger.LogInformation(
                        "Cicerone: the run reached its {Budget}-minute audio budget and stopped early",
                        config.AudioMinuteBudget);
                }

                _runs.End(RunStatus.Completed);
                _logger.LogInformation(
                    "Cicerone: run finished — {Done} items, {Minutes:0.0} minutes of audio",
                    done,
                    budget.SpentSeconds / 60);
            }
            catch (OperationCanceledException)
            {
                _runs.End(RunStatus.Cancelled);
                throw;
            }
            catch (Exception ex)
            {
                _runs.End(RunStatus.Failed, ex.Message);
                throw;
            }
            finally
            {
                _one.Release();
            }
        }

        /// <summary>Checks one item now, outside a run.</summary>
        /// <param name="itemId">The item.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The report, or null when the item is not in the library.</returns>
        public async Task<ItemReport?> CheckOneAsync(Guid itemId, CancellationToken cancellationToken)
        {
            var item = _library.GetItemById(itemId);
            if (item is null)
            {
                return null;
            }

            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var report = await _checks.CheckAsync(item, config, cancellationToken).ConfigureAwait(false);
            await _reports.SaveAsync(report, cancellationToken).ConfigureAwait(false);
            return report;
        }

        /// <summary>Every item a run would visit.</summary>
        /// <param name="config">The settings.</param>
        /// <returns>The items.</returns>
        public IReadOnlyList<BaseItem> Eligible(PluginConfiguration config)
        {
            ArgumentNullException.ThrowIfNull(config);

            var kinds = new List<BaseItemKind>();
            if (config.IncludeMovies)
            {
                kinds.Add(BaseItemKind.Movie);
            }

            if (config.IncludeEpisodes)
            {
                kinds.Add(BaseItemKind.Episode);
            }

            if (kinds.Count == 0)
            {
                return [];
            }

            return _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [.. kinds],
                Recursive = true,
                IsVirtualItem = false,
                HasSubtitles = true,
            })
                .Where(i => !string.IsNullOrEmpty(i.Path))
                .ToList();
        }

        private bool Due(BaseItem item)
        {
            try
            {
                var file = new FileInfo(item.Path);
                var version = string.Create(
                    CultureInfo.InvariantCulture, $"{file.Length}-{file.LastWriteTimeUtc.Ticks}");

                return _reports.NeedsCheck(item.Id, version);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Unreadable now is not the same as unchanged. Letting it through
                // means the check itself reports the file as unreadable, which is a
                // line in the report rather than an item silently never looked at.
                return true;
            }
        }

        private static RunItem Record(ItemReport report, PluginConfiguration config, long elapsedMs)
        {
            var price = config.ResolveProfile()?.CostPerAudioMinute ?? 0;
            var cost = price > 0 ? (decimal?)(price * (decimal)(report.AudioSeconds / 60.0)) : null;

            var tracks = report.Tracks.Select(t => new RunTrack(
                t.Track.Index,
                t.Track.Language,
                t.Verdict.ToString(),
                t.Sync?.Reason ?? t.Skipped ?? Describe(t.Verdict),
                t.Sync?.Correction.OffsetSeconds ?? 0,
                t.Sync?.Correction.Scale ?? 1,
                t.Sync?.WorstErrorSeconds ?? 0,
                null)).ToList();

            return new RunItem(
                report.ItemId,
                report.Name,
                tracks,
                report.Tracks.Count(t => t.Sync is not null),
                report.AudioSeconds,
                cost,
                elapsedMs,
                report.Error);
        }

        private static string Describe(Verdict verdict) => verdict switch
        {
            Verdict.WrongLanguage => "the text is not the language the track claims",
            Verdict.NoDialogue => "no dialogue to check",
            _ => "no conclusion",
        };
    }
}
