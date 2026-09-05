using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Jellyfin.Plugin.Cicerone.Core.Runs;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cicerone.Services.Runs
{
    /// <summary>What the settings page shows while a run is going.</summary>
    /// <param name="Id">The run.</param>
    /// <param name="StartedUtc">When it began.</param>
    /// <param name="Trigger">What started it.</param>
    /// <param name="Planned">How many items it means to visit.</param>
    /// <param name="Done">How many it has finished.</param>
    /// <param name="CurrentItem">What it is working on right now.</param>
    /// <param name="AudioMinutes">How much audio it has transcribed.</param>
    /// <param name="CostUsd">What that has come to.</param>
    /// <param name="TimeLeft">The estimate, or null when there is not enough to go on.</param>
    public sealed record RunProgress(
        string Id,
        DateTime StartedUtc,
        string Trigger,
        int Planned,
        int Done,
        string? CurrentItem,
        double AudioMinutes,
        decimal? CostUsd,
        TimeSpan? TimeLeft);

    /// <summary>
    /// Holds the live run in memory and finished runs on disk.
    /// </summary>
    /// <remarks>
    /// <b>Memory answers the progress panel; the file answers history.</b> An open
    /// settings page polls every couple of seconds while a run is going, and serving
    /// that by re-reading and re-parsing a file which grows with every item would
    /// make the run slower the longer it went on. The live snapshot is a lock and a
    /// small allocation.
    /// <para>
    /// The file is written as the run progresses rather than at the end, so a run
    /// killed by a host teardown still leaves everything up to the point it stopped.
    /// </para>
    /// </remarks>
    public sealed class RunLogStore
    {
        /// <summary>How many finished runs are kept.</summary>
        public const int Keep = 20;

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly string _directory;
        private readonly ILogger<RunLogStore> _logger;
        private readonly Lock _gate = new();

        private RunLogDocument? _current;
        private List<RunItem> _items = [];
        private List<DateTime> _completions = [];
        private List<string> _working = [];
        private DateTime _lastWrite = DateTime.MinValue;

        /// <summary>Initialises a new instance of the <see cref="RunLogStore"/> class.</summary>
        /// <param name="paths">Server paths.</param>
        /// <param name="logger">The logger.</param>
        public RunLogStore(IApplicationPaths paths, ILogger<RunLogStore> logger)
        {
            ArgumentNullException.ThrowIfNull(paths);
            _directory = Path.Combine(paths.DataPath, "cicerone", "runs");
            _logger = logger;
        }

        /// <summary>Gets whether a run is going.</summary>
        public bool Busy
        {
            get
            {
                lock (_gate)
                {
                    return _current is { Status: RunStatus.Running };
                }
            }
        }

        /// <summary>Starts a run.</summary>
        /// <param name="trigger">What started it.</param>
        /// <param name="planned">How many items it means to visit.</param>
        /// <returns>The run's identifier.</returns>
        public string Begin(string trigger, int planned)
        {
            lock (_gate)
            {
                var id = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                _items = [];
                _completions = [];
                _working = [];
                _lastWrite = DateTime.MinValue;
                _current = new RunLogDocument(
                    id, DateTime.UtcNow, null, RunStatus.Running, trigger, planned, _items, null, null);

                Persist();
                return id;
            }
        }

        /// <summary>Says the run has started on an item.</summary>
        /// <param name="name">The item's name.</param>
        /// <remarks>
        /// A list rather than a single name because a run walks several lanes at
        /// once, and a field the last lane to start overwrites would show an item
        /// that finished minutes ago while three others are still being listened to.
        /// Every call must be paired with <see cref="Left"/>.
        /// </remarks>
        public void Working(string? name)
        {
            lock (_gate)
            {
                // Never persisted. It changes several times a minute and exists only
                // for the progress panel, which reads it from memory.
                _working.Add(name ?? string.Empty);
            }
        }

        /// <summary>Says the run has left an item, however it went.</summary>
        /// <param name="name">The item's name, as it was passed to <see cref="Working"/>.</param>
        public void Left(string? name)
        {
            lock (_gate)
            {
                _working.Remove(name ?? string.Empty);
            }
        }

        /// <summary>Records a finished item.</summary>
        /// <param name="item">What happened to it.</param>
        public void Finished(RunItem item)
        {
            ArgumentNullException.ThrowIfNull(item);

            lock (_gate)
            {
                _items.Add(item);
                _completions.Add(DateTime.UtcNow);

                // Debounced. Nothing reads the file during a run — the panel reads
                // memory — so writing it on every item would be pure I/O for an
                // audience of nobody. Five seconds bounds what a host teardown loses.
                if ((DateTime.UtcNow - _lastWrite).TotalSeconds >= 5)
                {
                    Persist();
                }
            }
        }

        /// <summary>Ends the run.</summary>
        /// <param name="status">How it ended.</param>
        /// <param name="error">Why, when it failed.</param>
        public void End(RunStatus status, string? error = null)
        {
            lock (_gate)
            {
                if (_current is null)
                {
                    return;
                }

                _current = _current with
                {
                    FinishedUtc = DateTime.UtcNow,
                    Status = status,
                    Error = error,
                    TotalCostUsd = Total(),
                };

                Persist();
                Rotate();
                _current = null;
                _working = [];
            }
        }

        /// <summary>The live run, or null.</summary>
        /// <returns>Progress, recomputed at the moment of the call.</returns>
        public RunProgress? Current()
        {
            lock (_gate)
            {
                if (_current is not { Status: RunStatus.Running } run)
                {
                    return null;
                }

                return new RunProgress(
                    run.Id,
                    run.StartedUtc,
                    run.Trigger,
                    run.Planned,
                    _items.Count,
                    _working.Count == 0 ? null : string.Join(", ", _working),
                    _items.Sum(i => i.AudioSeconds) / 60.0,
                    Total(),
                    RunEstimate.TimeLeft(_completions, run.Planned - _items.Count, DateTime.UtcNow));
            }
        }

        /// <summary>Reads finished runs.</summary>
        /// <param name="limit">How many to return.</param>
        /// <returns>The runs, newest first by when each started.</returns>
        public IReadOnlyList<RunLogDocument> Recent(int limit)
        {
            var runs = new List<RunLogDocument>();

            try
            {
                if (!Directory.Exists(_directory))
                {
                    return runs;
                }

                foreach (var file in Directory.EnumerateFiles(_directory, "run_*.json"))
                {
                    try
                    {
                        var run = JsonSerializer.Deserialize<RunLogDocument>(File.ReadAllText(file), Json);
                        if (run is null)
                        {
                            continue;
                        }

                        // A file that still says running with nothing behind it is
                        // reported as abandoned, worked out on read because the one
                        // thing a dead process cannot do is write that it died.
                        runs.Add(run.Status == RunStatus.Running && !IsLive(run.Id)
                            ? run with { Status = RunStatus.Abandoned }
                            : run);
                    }
                    catch (Exception ex) when (ex is JsonException or IOException)
                    {
                        _logger.LogDebug(ex, "Cicerone: skipping unreadable run log {File}", file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Cicerone: could not read the run directory");
            }

            return runs
                .OrderByDescending(r => r.StartedUtc)
                .Take(Math.Max(limit, 1))
                .ToList();
        }

        /// <summary>Reads one run in full.</summary>
        /// <param name="id">The run.</param>
        /// <returns>The run, or null.</returns>
        public RunLogDocument? Read(string id)
        {
            try
            {
                var path = Path.Combine(_directory, "run_" + Sanitize(id) + ".json");
                if (!File.Exists(path))
                {
                    return null;
                }

                var run = JsonSerializer.Deserialize<RunLogDocument>(File.ReadAllText(path), Json);
                return run is { Status: RunStatus.Running } && !IsLive(id)
                    ? run with { Status = RunStatus.Abandoned }
                    : run;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
                or ArgumentException)
            {
                _logger.LogDebug(ex, "Cicerone: could not read run {Id}", id);
                return null;
            }
        }

        private bool IsLive(string id)
        {
            lock (_gate)
            {
                return _current is { Status: RunStatus.Running } run
                    && string.Equals(run.Id, id, StringComparison.Ordinal);
            }
        }

        private decimal? Total()
        {
            var priced = _items.Where(i => i.CostUsd.HasValue).ToList();

            // Null rather than zero when nothing was priced. A run that cost money
            // must never read as free, and zero is what "free" looks like.
            return priced.Count == 0 ? null : priced.Sum(i => i.CostUsd!.Value);
        }

        private void Persist()
        {
            if (_current is null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_directory);
                var document = _current with { Items = _items.ToList(), TotalCostUsd = Total() };
                var path = Path.Combine(_directory, "run_" + Sanitize(document.Id) + ".json");
                File.WriteAllText(path, JsonSerializer.Serialize(document, Json));
                _lastWrite = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Cicerone: could not write the run log");
            }
        }

        private void Rotate()
        {
            try
            {
                if (!Directory.Exists(_directory))
                {
                    return;
                }

                var stale = Directory.GetFiles(_directory, "run_*.json")
                    .OrderByDescending(f => f, StringComparer.Ordinal)
                    .Skip(Keep);

                foreach (var file in stale)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Cicerone: could not rotate run logs");
            }
        }

        private static string Sanitize(string id) =>
            new(id.Where(char.IsLetterOrDigit).Take(32).ToArray());
    }
}
