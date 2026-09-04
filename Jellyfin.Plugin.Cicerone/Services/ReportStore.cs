using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Cicerone.Core.Reports;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cicerone.Services
{
    /// <summary>
    /// Holds what Cicerone has concluded about each item, one file apiece.
    /// </summary>
    /// <remarks>
    /// A cache, never a write-back. Nothing here is written into the library, so
    /// deleting the directory restores exactly the behaviour of a server without the
    /// plugin — and the reports cost only the audio to rebuild, never anything that
    /// cannot be recovered.
    /// <para>
    /// One file per item rather than one index, because a run is resumable and
    /// <em>will</em> be interrupted: installing any plugin tears Jellyfin's host down
    /// in process. Each item is written the moment it is checked, so a restart picks
    /// up where it left off and nothing already paid for is lost.
    /// </para>
    /// </remarks>
    public sealed class ReportStore
    {
        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly string _directory;
        private readonly ILogger<ReportStore> _logger;

        // Reports are read on every page load of the report tab and written once per
        // item. Keeping them in memory turns a tab that lists a whole library from
        // several thousand file reads into a dictionary lookup.
        private readonly ConcurrentDictionary<Guid, ItemReport> _cache = new();
        private int _loaded;

        /// <summary>Initialises a new instance of the <see cref="ReportStore"/> class.</summary>
        /// <param name="paths">Server paths.</param>
        /// <param name="logger">The logger.</param>
        public ReportStore(IApplicationPaths paths, ILogger<ReportStore> logger)
        {
            ArgumentNullException.ThrowIfNull(paths);
            _directory = Path.Combine(paths.DataPath, "cicerone", "reports");
            _logger = logger;
        }

        /// <summary>Saves one item's report.</summary>
        /// <param name="report">The report.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task.</returns>
        public async Task SaveAsync(ItemReport report, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(report);

            _cache[report.ItemId] = report;

            try
            {
                Directory.CreateDirectory(_directory);
                var path = PathFor(report.ItemId);
                var temporary = path + ".tmp";

                await File.WriteAllTextAsync(
                        temporary, JsonSerializer.Serialize(report, Json), cancellationToken)
                    .ConfigureAwait(false);

                File.Move(temporary, path, overwrite: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The report is still in memory and the run continues. Losing the file
                // costs a re-check later; failing the item costs the audio now.
                _logger.LogWarning(ex, "Cicerone: could not save the report for {Item}", report.Name);
            }
        }

        /// <summary>Reads one item's report.</summary>
        /// <param name="itemId">The item.</param>
        /// <returns>The report, or null.</returns>
        public ItemReport? Get(Guid itemId)
        {
            LoadOnce();
            return _cache.GetValueOrDefault(itemId);
        }

        /// <summary>Reads every stored report.</summary>
        /// <returns>The reports, most recently checked first.</returns>
        public IReadOnlyList<ItemReport> All()
        {
            LoadOnce();
            return _cache.Values.OrderByDescending(r => r.CheckedUtc).ToList();
        }

        /// <summary>Whether an item needs checking again.</summary>
        /// <param name="itemId">The item.</param>
        /// <param name="mediaVersion">The file's current size and modification time.</param>
        /// <returns>True when there is no report, or the file has changed since it was made.</returns>
        /// <remarks>
        /// Keyed on the media file rather than on a timestamp, so a second run over an
        /// unchanged library is free and a replaced release is re-checked without
        /// anybody having to remember that it was replaced. A report made before this
        /// carried a version has an empty one and is treated as current — re-checking
        /// a whole library to fill in a field would cost more than the field is worth.
        /// </remarks>
        public bool NeedsCheck(Guid itemId, string mediaVersion)
        {
            var existing = Get(itemId);
            if (existing is null)
            {
                return true;
            }

            return existing.MediaVersion.Length > 0
                && !string.Equals(existing.MediaVersion, mediaVersion, StringComparison.Ordinal);
        }

        /// <summary>Forgets everything.</summary>
        /// <returns>How many reports were removed.</returns>
        public int Clear()
        {
            _cache.Clear();
            Interlocked.Exchange(ref _loaded, 0);

            try
            {
                if (!Directory.Exists(_directory))
                {
                    return 0;
                }

                var files = Directory.GetFiles(_directory, "*.json");
                foreach (var file in files)
                {
                    File.Delete(file);
                }

                return files.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Cicerone: could not clear the report directory");
                return 0;
            }
        }

        private string PathFor(Guid itemId) =>
            Path.Combine(_directory, itemId.ToString("N", CultureInfo.InvariantCulture) + ".json");

        private void LoadOnce()
        {
            if (Interlocked.CompareExchange(ref _loaded, 1, 0) != 0)
            {
                return;
            }

            try
            {
                if (!Directory.Exists(_directory))
                {
                    return;
                }

                foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
                {
                    try
                    {
                        var report = JsonSerializer.Deserialize<ItemReport>(File.ReadAllText(file), Json);
                        if (report is not null)
                        {
                            _cache[report.ItemId] = report;
                        }
                    }
                    catch (Exception ex) when (ex is JsonException or IOException)
                    {
                        // One unreadable report — a write interrupted by a host
                        // teardown — must not stop the other several hundred loading.
                        _logger.LogDebug(ex, "Cicerone: skipping unreadable report {File}", file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Cicerone: could not read the report directory");
            }
        }
    }
}
