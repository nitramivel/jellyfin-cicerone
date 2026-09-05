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
        private readonly SubtitleMaker _maker;
        private readonly ILogger<VerifyRunService> _logger;
        private readonly SemaphoreSlim _one = new(1, 1);

        /// <summary>Initialises a new instance of the <see cref="VerifyRunService"/> class.</summary>
        /// <param name="library">Library access.</param>
        /// <param name="checks">The per-item checker.</param>
        /// <param name="reports">Where results are kept.</param>
        /// <param name="runs">Run history and live progress.</param>
        /// <param name="maker">Writes a track for an item that has none.</param>
        /// <param name="logger">The logger.</param>
        public VerifyRunService(
            ILibraryManager library,
            CheckService checks,
            ReportStore reports,
            RunLogStore runs,
            SubtitleMaker maker,
            ILogger<VerifyRunService> logger)
        {
            _library = library;
            _checks = checks;
            _reports = reports;
            _runs = runs;
            _maker = maker;
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

                // What a lane holds while it works. Under the speech-activity method a
                // check sends no audio anywhere and the estimate is zero — the budget
                // exists now to bound transcription, which is the only thing left that
                // spends anything, and a lane about to transcribe holds a whole runtime
                // rather than a couple of windows.
                var estimate = config.SyncMethod == SyncMethod.Transcript
                    ? config.AudioSecondsPerItem(1)
                    : 0;
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

                        if (config.TranscribeWhenMissing)
                        {
                            report = await FillGapsAsync(item, report, config, budget, ct).ConfigureAwait(false);
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

        /// <summary>Transcribes the languages an item turned out not to have.</summary>
        /// <remarks>
        /// <b>The only part of a run that can still spend money, and it is opt-in.</b>
        /// It runs after the check rather than instead of it, because the check is what
        /// establishes that the language is genuinely missing: an item can carry an
        /// English track that turns out to be an image, or forced, or forty lines of
        /// signage, and all three look like coverage until something reads them.
        /// <para>
        /// A whole runtime is reserved against the budget before the transcriber is
        /// called, not after. Reserving what a transcription actually costs is the only
        /// reason the ceiling means anything here: an item is a hundred times a check,
        /// and finding out afterwards is finding out too late.
        /// </para>
        /// </remarks>
        private async Task<ItemReport> FillGapsAsync(
            BaseItem item,
            ItemReport report,
            PluginConfiguration config,
            RunBudget budget,
            CancellationToken cancellationToken)
        {
            if (report.MissingLanguages.Count == 0 || report.Error is not null)
            {
                return report;
            }

            var runtime = report.RuntimeSeconds;
            var spent = 0.0;

            foreach (var language in report.MissingLanguages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!budget.TryReserve(runtime))
                {
                    _logger.LogInformation(
                        "Cicerone: not transcribing {Item} in {Language} — the run is at its audio budget",
                        item.Name, language);
                    break;
                }

                MadeSubtitle made;
                try
                {
                    made = await _maker.MakeAsync(item, language, config, null, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    budget.Settle(runtime, 0);
                    throw;
                }
                catch (Exception ex)
                {
                    budget.Settle(runtime, 0);
                    _logger.LogWarning(ex, "Cicerone: could not transcribe {Item}", item.Name);
                    continue;
                }

                budget.Settle(runtime, made.AudioSeconds);
                spent += made.AudioSeconds;

                _logger.LogInformation(
                    "Cicerone: {Item} in {Language} — {Message}", item.Name, language, made.Message);
            }

            // Folded into the item's own audio total so the run's cost, the report and
            // the coverage tally all agree about what was spent on it.
            return spent > 0 ? report with { AudioSeconds = report.AudioSeconds + spent } : report;
        }

        /// <summary>Transcribes one item now, as a run of its own.</summary>
        /// <param name="itemId">The item.</param>
        /// <param name="language">The language to write.</param>
        /// <param name="progress">Where progress is reported.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What happened.</returns>
        /// <remarks>
        /// Takes the same one-run-at-a-time lock as a library walk and reports through
        /// the same run log, so the settings page shows it in the progress panel like
        /// anything else. It is a run because it takes minutes: an HTTP request held
        /// open for the length of a feature film is not a request, it is a mistake.
        /// </remarks>
        public async Task<MadeSubtitle> TranscribeOneAsync(
            Guid itemId,
            string language,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            var item = _library.GetItemById(itemId);
            if (item is null)
            {
                return MadeSubtitle.No("that item is not in the library");
            }

            if (!await _one.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                return MadeSubtitle.No("a run is already going — wait for it to finish, or stop it first");
            }

            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var stopwatch = Stopwatch.StartNew();

            try
            {
                _runs.Begin("transcribe " + (item.Name ?? "one item"), 1);
                _runs.Working(item.Name);

                var made = await _maker.MakeAsync(item, language, config, progress, cancellationToken)
                    .ConfigureAwait(false);

                _runs.Left(item.Name);
                _runs.Finished(new RunItem(
                    item.Id,
                    item.Name ?? string.Empty,
                    [],
                    0,
                    made.AudioSeconds,
                    Price(config, made.AudioSeconds),
                    stopwatch.ElapsedMilliseconds,
                    made.Ok ? null : made.Message));

                _runs.End(made.Ok ? RunStatus.Completed : RunStatus.Failed, made.Ok ? null : made.Message);
                return made;
            }
            catch (OperationCanceledException)
            {
                _runs.End(RunStatus.Cancelled);
                throw;
            }
            catch (Exception ex)
            {
                _runs.End(RunStatus.Failed, ex.Message);
                return MadeSubtitle.No(ex.Message);
            }
            finally
            {
                _one.Release();
            }
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

        private static decimal? Price(PluginConfiguration config, double audioSeconds)
        {
            var price = config.ResolveProfile()?.CostPerAudioMinute ?? 0;
            return price > 0 && audioSeconds > 0 ? price * (decimal)(audioSeconds / 60.0) : null;
        }

        private static RunItem Record(ItemReport report, PluginConfiguration config, long elapsedMs)
        {
            var cost = Price(config, report.AudioSeconds);

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
