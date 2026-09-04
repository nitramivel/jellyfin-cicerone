using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Cicerone.Configuration;
using Jellyfin.Plugin.Cicerone.Core.Reports;
using Jellyfin.Plugin.Cicerone.Core.Runs;
using Jellyfin.Plugin.Cicerone.Core.Sync;
using Jellyfin.Plugin.Cicerone.Services;
using Jellyfin.Plugin.Cicerone.Services.Runs;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cicerone.Api
{
    /// <summary>What a run would cost before it is started.</summary>
    /// <param name="Items">How many items it would visit.</param>
    /// <param name="AudioMinutes">How much audio that is.</param>
    /// <param name="CostUsd">What that comes to at the current profile's price, or null when unpriced.</param>
    /// <param name="Budgeted">Whether the audio budget would stop it early.</param>
    public sealed record RunEstimateResponse(int Items, double AudioMinutes, decimal? CostUsd, bool Budgeted);

    /// <summary>Cicerone's HTTP surface.</summary>
    /// <remarks>
    /// Every endpoint is admin-only. Unlike a plugin that draws something on a detail
    /// page, nothing here is read by a viewer's browser — the whole surface exists to
    /// serve one settings page and whatever scripts the owner points at it.
    /// </remarks>
    [ApiController]
    [Route("Cicerone")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public class CiceroneController : ControllerBase
    {
        private readonly VerifyRunService _runService;
        private readonly ReportStore _reports;
        private readonly RunLogStore _runs;
        private readonly Services.Transcription.TranscriptionProviderFactory _providers;
        private readonly ILogger<CiceroneController> _logger;

        /// <summary>Initialises a new instance of the <see cref="CiceroneController"/> class.</summary>
        /// <param name="runService">The run service.</param>
        /// <param name="reports">Stored reports.</param>
        /// <param name="runs">Run history.</param>
        /// <param name="providers">Transcription backends, for the connection test.</param>
        /// <param name="logger">The logger.</param>
        public CiceroneController(
            VerifyRunService runService,
            ReportStore reports,
            RunLogStore runs,
            Services.Transcription.TranscriptionProviderFactory providers,
            ILogger<CiceroneController> logger)
        {
            _runService = runService;
            _reports = reports;
            _runs = runs;
            _providers = providers;
            _logger = logger;
        }

        /// <summary>The running plugin version.</summary>
        /// <returns>The version.</returns>
        /// <remarks>
        /// Its absence is the diagnostic. A settings page cached from before an
        /// upgrade cannot render a version it never knew to ask for, so a blank badge
        /// means a stale page rather than a stale server.
        /// </remarks>
        [HttpGet("Version")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<object> GetVersion() => Ok(new
        {
            Version = Plugin.Instance?.Version?.ToString() ?? "unknown",
        });

        /// <summary>What the plugin is doing right now.</summary>
        /// <returns>The live run, or that there is none.</returns>
        [HttpGet("Status")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<object> GetStatus()
        {
            var current = _runs.Current();
            return Ok(new
            {
                Running = current is not null,
                Run = current,
            });
        }

        /// <summary>What a full run would cost, before anything is spent.</summary>
        /// <returns>The estimate.</returns>
        /// <remarks>
        /// Exact rather than indicative, which is the payoff of anchoring instead of
        /// transcribing whole files: the audio a run will send is decided by the
        /// anchor count and window length, both of which are settings, so the figure
        /// is arithmetic and not a projection.
        /// </remarks>
        [HttpGet("Estimate")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<RunEstimateResponse> GetEstimate()
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var items = _runService.Eligible(config).Count;

            // One track per item. A disc rip with three English tracks costs the same
            // as one — they share the transcript — so counting tracks would overstate
            // it, and counting items understates only the rare item wanting two
            // different languages checked.
            var minutes = items * config.AudioSecondsPerItem(1) / 60.0;
            var price = config.ResolveProfile()?.CostPerAudioMinute ?? 0;

            return Ok(new RunEstimateResponse(
                items,
                minutes,
                price > 0 ? price * (decimal)minutes : null,
                config.AudioMinuteBudget > 0 && minutes > config.AudioMinuteBudget));
        }

        /// <summary>Queues a full run.</summary>
        /// <returns>Whether it started.</returns>
        [HttpPost("Verify")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public ActionResult<object> Verify()
        {
            if (_runService.Busy)
            {
                return Conflict(new { Message = "A run is already going." });
            }

            // Fire and forget, with its own cancellation token: the HTTP request must
            // not hold a run that takes hours, and the request's token is cancelled
            // the moment the response is written.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _runService.RunAsync("Settings page", null, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Cicerone: the run failed");
                }
            });

            return Ok(new { Started = true });
        }

        /// <summary>Checks one item now and returns what was found.</summary>
        /// <param name="itemId">The item.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The report.</returns>
        /// <remarks>
        /// Awaited rather than queued, because this is the button somebody presses to
        /// find out whether any of this works before turning it loose on a library.
        /// An answer that arrives is the whole point of it.
        /// </remarks>
        [HttpPost("Verify/{itemId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<ItemReport>> VerifyOne(
            [FromRoute] Guid itemId,
            CancellationToken cancellationToken)
        {
            var report = await _runService.CheckOneAsync(itemId, cancellationToken).ConfigureAwait(false);
            return report is null ? NotFound() : Ok(report);
        }

        /// <summary>Tests that a transcription profile answers at all.</summary>
        /// <param name="profileId">The profile to test, or empty for the default.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What happened.</returns>
        /// <remarks>
        /// Sends a fraction of a second of silence, which every backend accepts and
        /// none charges meaningfully for. It answers the question the settings page
        /// cannot: without it, "the key is wrong", "the local server is not running"
        /// and "this library has nothing to check" all look identical.
        /// </remarks>
        [HttpPost("TestProfile")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<ActionResult<object>> TestProfile(
            [FromQuery] string? profileId,
            CancellationToken cancellationToken)
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

            var profile = string.IsNullOrWhiteSpace(profileId)
                ? config.ResolveProfile()
                : config.Profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.Ordinal));

            if (profile is null)
            {
                return Ok(new { Ok = false, Message = "No transcription profile is configured." });
            }

            try
            {
                var provider = _providers.Create(profile);
                var result = await provider.TranscribeAsync(
                        SilentWav(TimeSpan.FromSeconds(0.5)),
                        Core.Audio.ClipFormat.Wav,
                        TimeSpan.FromSeconds(0.5),
                        null,
                        cancellationToken)
                    .ConfigureAwait(false);

                // Silence transcribing to nothing is a pass, not a failure: the round
                // trip completed, which is the only thing being tested. A backend that
                // invents words for half a second of silence is a different problem
                // and one worth seeing here.
                var reachable = result.Ok || result.Error?.Contains("nothing was said", StringComparison.Ordinal) == true;

                return Ok(new
                {
                    Ok = reachable,
                    Model = provider.ModelId,
                    Message = reachable
                        ? "The transcriber answered."
                        : result.Error ?? "The transcriber did not answer.",
                    Timestamped = result.Timestamped,
                });
            }
            catch (Exception ex)
            {
                return Ok(new { Ok = false, Message = ex.Message });
            }
        }

        /// <summary>Everything Cicerone has concluded, with a tally over the top.</summary>
        /// <param name="verdict">Only items whose best track landed on this verdict.</param>
        /// <param name="limit">How many items to return.</param>
        /// <returns>The coverage and the items.</returns>
        [HttpGet("Reports")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<object> GetReports([FromQuery] string? verdict, [FromQuery] int limit = 200)
        {
            var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var languages = config.Languages();
            var all = _reports.All();

            var filtered = all.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(verdict) && Enum.TryParse<Verdict>(verdict, true, out var wanted))
            {
                filtered = filtered.Where(r =>
                    languages.Any(l => r.Best(l)?.Verdict == wanted)
                    || (languages.Count == 0 && r.Tracks.Any(t => t.Verdict == wanted)));
            }

            return Ok(new
            {
                Coverage = Coverage.Build(all, languages),
                Languages = languages,
                Items = filtered.Take(Math.Clamp(limit, 1, 2000)).ToList(),
            });
        }

        /// <summary>One item's report.</summary>
        /// <param name="itemId">The item.</param>
        /// <returns>The report.</returns>
        [HttpGet("Report/{itemId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<ItemReport> GetReport([FromRoute] Guid itemId)
        {
            var report = _reports.Get(itemId);
            return report is null ? NotFound() : Ok(report);
        }

        /// <summary>Forgets every stored report.</summary>
        /// <returns>How many were removed.</returns>
        /// <remarks>
        /// Nothing here was written into the library, so this is genuinely reversible
        /// — at the cost of the audio to rebuild it. Repaired subtitle files are
        /// <em>not</em> removed: those are files in library folders and deleting them
        /// is the owner's decision to make with a file manager, not a side effect of
        /// clearing a cache.
        /// </remarks>
        [HttpDelete("Reports")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<object> ClearReports() => Ok(new { Removed = _reports.Clear() });

        /// <summary>Recent runs, newest first.</summary>
        /// <param name="limit">How many.</param>
        /// <returns>The runs.</returns>
        [HttpGet("Runs")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IReadOnlyList<RunLogDocument>> GetRuns([FromQuery] int limit = 5) =>
            Ok(_runs.Recent(Math.Clamp(limit, 1, RunLogStore.Keep)));

        /// <summary>One run in full.</summary>
        /// <param name="runId">The run.</param>
        /// <returns>Every item it touched.</returns>
        [HttpGet("Runs/{runId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<RunLogDocument> GetRun([FromRoute] string runId)
        {
            var run = _runs.Read(runId);
            return run is null ? NotFound() : Ok(run);
        }

        /// <summary>Builds a silent WAV of a given length.</summary>
        /// <remarks>
        /// Written by hand rather than shipped as a resource, because a 44-byte header
        /// and a block of zeroes is less code than an embedded file plus the plumbing
        /// to read it.
        /// </remarks>
        private static byte[] SilentWav(TimeSpan duration)
        {
            const int Rate = 16000;
            var samples = (int)(duration.TotalSeconds * Rate);
            var dataBytes = samples * 2;

            using var stream = new System.IO.MemoryStream(44 + dataBytes);
            using var writer = new System.IO.BinaryWriter(stream);

            writer.Write("RIFF"u8.ToArray());
            writer.Write(36 + dataBytes);
            writer.Write("WAVE"u8.ToArray());
            writer.Write("fmt "u8.ToArray());
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(Rate);
            writer.Write(Rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8.ToArray());
            writer.Write(dataBytes);
            writer.Write(new byte[dataBytes]);

            writer.Flush();
            return stream.ToArray();
        }
    }
}
