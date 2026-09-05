using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Cicerone.Configuration;
using Jellyfin.Plugin.Cicerone.Core.Language;
using Jellyfin.Plugin.Cicerone.Core.Reports;
using Jellyfin.Plugin.Cicerone.Core.Subtitles;
using Jellyfin.Plugin.Cicerone.Core.Sync;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cicerone.Services
{
    /// <summary>What came of asking to change a subtitle file.</summary>
    /// <param name="Ok">Whether it happened.</param>
    /// <param name="Message">What happened, in a sentence, for the manager to show.</param>
    /// <param name="Path">The file involved, when there is one.</param>
    public sealed record SubtitleOutcome(bool Ok, string Message, string? Path = null)
    {
        /// <summary>A refusal.</summary>
        /// <param name="why">The reason, which is shown to the owner verbatim.</param>
        /// <returns>The outcome.</returns>
        public static SubtitleOutcome No(string why) => new(false, why);
    }

    /// <summary>
    /// Everything the subtitle manager does to the files on disk.
    /// </summary>
    /// <remarks>
    /// <b>Two rules govern this whole class, and they are not the same rule.</b>
    /// <list type="number">
    /// <item>Cicerone never overwrites a file it did not write. An automated repair
    /// goes to a new sidecar, and so does a copy saved out of the manager: the source
    /// track survives whatever anybody does to the copy.</item>
    /// <item>The owner may delete anything, and that is not a contradiction. The rule
    /// above is about what the plugin does <em>on its own initiative</em>; a manager
    /// exists so that somebody can look at the six subtitle files that have
    /// accumulated beside a film and remove four of them. Refusing that would not be
    /// caution, it would be making the owner open a file manager to do the same thing
    /// less safely.</item>
    /// </list>
    /// Every path that leaves this class is checked against the files actually listed
    /// for the item first. The API deals in opaque tokens for that reason: an endpoint
    /// that took a path would take <em>any</em> path, and this one runs as an
    /// administrator.
    /// </remarks>
    public sealed class SubtitleLibrary
    {
        private readonly ILibraryManager _library;
        private readonly IMediaSourceManager _mediaSources;
        private readonly SubtitleReader _subtitles;
        private readonly ReportStore _reports;
        private readonly RepairWriter _writer;
        private readonly ILogger<SubtitleLibrary> _logger;

        /// <summary>Initialises a new instance of the <see cref="SubtitleLibrary"/> class.</summary>
        /// <param name="library">Library access.</param>
        /// <param name="mediaSources">Where an item's streams are read from.</param>
        /// <param name="subtitles">Subtitle extraction.</param>
        /// <param name="reports">Stored verdicts, to colour the listing.</param>
        /// <param name="writer">Where files are written.</param>
        /// <param name="logger">The logger.</param>
        public SubtitleLibrary(
            ILibraryManager library,
            IMediaSourceManager mediaSources,
            SubtitleReader subtitles,
            ReportStore reports,
            RepairWriter writer,
            ILogger<SubtitleLibrary> logger)
        {
            _library = library;
            _mediaSources = mediaSources;
            _subtitles = subtitles;
            _reports = reports;
            _writer = writer;
            _logger = logger;
        }

        /// <summary>Lists every subtitle an item has.</summary>
        /// <param name="itemId">The item.</param>
        /// <param name="config">The settings.</param>
        /// <returns>The inventory, or null when the item is not in the library.</returns>
        public SubtitleInventory? Inventory(Guid itemId, PluginConfiguration config) =>
            Inventory(itemId, config, probeWritable: true);

        /// <summary>Lists every subtitle an item has.</summary>
        /// <param name="itemId">The item.</param>
        /// <param name="config">The settings.</param>
        /// <param name="probeWritable">
        /// Whether to find out if the media's folder can be written to, which is done
        /// by trying it. Skipped when the listing is only being built to look one
        /// source up: an action would otherwise create and delete a probe file in
        /// somebody's library folder every time a button is pressed.
        /// </param>
        /// <returns>The inventory, or null when the item is not in the library.</returns>
        private SubtitleInventory? Inventory(Guid itemId, PluginConfiguration config, bool probeWritable)
        {
            ArgumentNullException.ThrowIfNull(config);

            var item = _library.GetItemById(itemId);
            if (item is null)
            {
                return null;
            }

            var report = _reports.Get(itemId);
            var streams = _mediaSources.GetMediaStreams(itemId);
            var sources = new List<SubtitleSource>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var track in SubtitleReader.Tracks(streams))
            {
                var stored = report?.Tracks.FirstOrDefault(t => t.Track.Index == track.Index);

                // An external track the server already knows about is listed from the
                // stream rather than from the directory sweep below, because the stream
                // is where its flags and its verdict are. The path is remembered so the
                // sweep does not list it a second time.
                if (!string.IsNullOrEmpty(track.Path))
                {
                    seen.Add(track.Path);
                }

                sources.Add(Describe(track, stored, config, report?.CheckedUtc));
            }

            if (!string.IsNullOrWhiteSpace(item.Path))
            {
                foreach (var file in Beside(item.Path, seen))
                {
                    sources.Add(FromFile(file, item.Path, config));
                }
            }

            var audio = AudioSampler.AudioTracks(streams)
                .Select(a => LanguageCodes.Normalize(a.Language))
                .Where(l => l.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return new SubtitleInventory(
                itemId,
                item.Name ?? string.Empty,
                (item as Episode)?.SeriesName,
                item.Path,
                item.RunTimeTicks is { } ticks ? TimeSpan.FromTicks(ticks).TotalSeconds : 0,
                sources.OrderBy(s => s.Kind).ThenBy(s => s.Language, StringComparer.Ordinal)
                    .ThenBy(s => s.StreamIndex ?? int.MaxValue).ToList(),
                audio,
                config.Languages(),
                probeWritable && CanWriteBeside(item.Path));
        }

        /// <summary>Reads one source out as SRT text.</summary>
        /// <param name="itemId">The item.</param>
        /// <param name="sourceId">Which source, as the inventory named it.</param>
        /// <param name="config">The settings.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The text, or why there is none.</returns>
        /// <remarks>
        /// Everything comes back as SRT whatever it started as, because it goes through
        /// the server's own subtitle encoder — which is the same route the checker
        /// uses, so what the manager shows is exactly what Cicerone measured.
        /// </remarks>
        public async Task<(string? Text, string? Error)> ReadAsync(
            Guid itemId,
            string sourceId,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var (item, source, error) = Find(itemId, sourceId, config);
            if (item is null || source is null)
            {
                return (null, error);
            }

            if (!source.IsText)
            {
                return (null, "this is an image track — reading it would need OCR");
            }

            if (source.StreamIndex is { } index)
            {
                var content = await _subtitles.ReadAsync(item, index, cancellationToken).ConfigureAwait(false);
                return content.Error is not null
                    ? (null, content.Error)
                    : (SrtWriter.Write(content.Raw), null);
            }

            try
            {
                var text = await File.ReadAllTextAsync(source.Path!, cancellationToken).ConfigureAwait(false);
                var cues = SrtParser.Parse(text);

                // Re-emitted rather than handed back raw, so the editor always sees one
                // format with one set of conventions — and so a file with CRLF, a BOM
                // and inconsistent numbering comes back tidy.
                return cues.Count > 0
                    ? (SrtWriter.Write(cues), null)
                    : (null, "the file parsed to no cues. If it is not SRT, it becomes readable here once "
                        + "the server has scanned it — the scan is what makes the conversion available");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (null, "the file could not be read: " + ex.Message);
            }
        }

        /// <summary>Saves edited text over a file Cicerone owns, or beside one it does not.</summary>
        /// <param name="itemId">The item.</param>
        /// <param name="sourceId">Which source is being saved.</param>
        /// <param name="text">The SRT to write.</param>
        /// <param name="config">The settings.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What happened.</returns>
        public async Task<SubtitleOutcome> SaveAsync(
            Guid itemId,
            string sourceId,
            string text,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var (item, source, error) = Find(itemId, sourceId, config);
            if (item is null || source is null)
            {
                return SubtitleOutcome.No(error ?? "that subtitle is no longer there");
            }

            var cues = SrtParser.Parse(text ?? string.Empty);
            if (cues.Count == 0)
            {
                return SubtitleOutcome.No("that is not a subtitle file Cicerone can read — nothing was saved");
            }

            // Cicerone's own file is written over; anything else gets a copy, and the
            // owner keeps the original. This is the rule the whole plugin is built on
            // and the manager does not get an exemption from it.
            if (source.Ours && source.IsFile)
            {
                var ok = await _writer.WriteTextAsync(source.Path!, SrtWriter.Write(cues), cancellationToken)
                    .ConfigureAwait(false);

                return ok
                    ? new SubtitleOutcome(true, $"saved {cues.Count} cues to {Path.GetFileName(source.Path)}", source.Path)
                    : SubtitleOutcome.No("the file could not be written — check the folder is writable by Jellyfin");
            }

            var written = await _writer.WriteCuesAsync(
                    item, cues, source.Language, config.RepairSuffix, source.IsHearingImpaired,
                    true, config, cancellationToken)
                .ConfigureAwait(false);

            return written is null
                ? SubtitleOutcome.No("the copy could not be written — check the folder is writable by Jellyfin")
                : new SubtitleOutcome(
                    true,
                    $"the original was left alone and a copy was saved as {Path.GetFileName(written)}",
                    written);
        }

        /// <summary>Saves a source out as a new sidecar Jellyfin will offer to viewers.</summary>
        /// <param name="itemId">The item.</param>
        /// <param name="sourceId">Which source to copy.</param>
        /// <param name="config">The settings.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What happened.</returns>
        /// <remarks>
        /// The one operation that turns an embedded stream into a file. An embedded
        /// track cannot be edited, retimed or moved without remuxing the container —
        /// which Cicerone will not do — so extracting it is the step that makes
        /// everything else in the manager available for it.
        /// </remarks>
        public async Task<SubtitleOutcome> ExtractAsync(
            Guid itemId,
            string sourceId,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var (item, source, error) = Find(itemId, sourceId, config);
            if (item is null || source is null)
            {
                return SubtitleOutcome.No(error ?? "that subtitle is no longer there");
            }

            var (text, why) = await ReadAsync(itemId, sourceId, config, cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                return SubtitleOutcome.No(why ?? "the track could not be read");
            }

            var written = await _writer.WriteCuesAsync(
                    item, SrtParser.Parse(text), source.Language, config.RepairSuffix,
                    source.IsHearingImpaired, true, config, cancellationToken)
                .ConfigureAwait(false);

            return written is null
                ? SubtitleOutcome.No("the file could not be written — check the folder is writable by Jellyfin")
                : new SubtitleOutcome(true, $"saved as {Path.GetFileName(written)}", written);
        }

        /// <summary>Shifts and stretches a track's timings.</summary>
        /// <param name="itemId">The item.</param>
        /// <param name="sourceId">Which source to retime.</param>
        /// <param name="offsetSeconds">How far to move it. Positive delays it.</param>
        /// <param name="scale">The factor to stretch its clock by.</param>
        /// <param name="config">The settings.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>What happened.</returns>
        public async Task<SubtitleOutcome> RetimeAsync(
            Guid itemId,
            string sourceId,
            double offsetSeconds,
            double scale,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            if (!double.IsFinite(offsetSeconds) || !double.IsFinite(scale) || scale <= 0)
            {
                return SubtitleOutcome.No("that is not a timing change Cicerone can apply");
            }

            var (text, why) = await ReadAsync(itemId, sourceId, config, cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                return SubtitleOutcome.No(why ?? "the track could not be read");
            }

            // The same Retiming the automated repair uses, applied to the same shape of
            // Correction. A hand-made adjustment and a measured one produce byte-
            // identical output for the same numbers, which is the point of there being
            // only one of it.
            var moved = Retiming.Apply(
                SrtParser.Parse(text),
                new Correction(offsetSeconds, scale, 0, 0, null));

            return await SaveAsync(itemId, sourceId, SrtWriter.Write(moved), config, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>Removes a subtitle file.</summary>
        /// <param name="itemId">The item.</param>
        /// <param name="sourceId">Which source to remove.</param>
        /// <param name="config">The settings.</param>
        /// <returns>What happened.</returns>
        /// <remarks>
        /// Only ever a file, and only ever one listed for this item. An embedded track
        /// is part of the container and removing it would mean rewriting the film,
        /// which is not something a subtitle plugin should ever be in a position to do
        /// by accident.
        /// </remarks>
        public SubtitleOutcome Delete(Guid itemId, string sourceId, PluginConfiguration config)
        {
            var (item, source, error) = Find(itemId, sourceId, config);
            if (item is null || source is null)
            {
                return SubtitleOutcome.No(error ?? "that subtitle is no longer there");
            }

            if (!source.IsFile)
            {
                return SubtitleOutcome.No(
                    "this track lives inside the media file. Removing it would mean rewriting the film, "
                    + "which Cicerone will not do — extract it to a file first if you want a copy you can manage");
            }

            try
            {
                File.Delete(source.Path!);
                _logger.LogInformation("Cicerone: deleted {Path} at the owner's request", source.Path);

                // Asked for rather than awaited. Until the server rescans, the deleted
                // track stays in a viewer's language menu and fails to load.
                _writer.RequestRefresh(item);

                return new SubtitleOutcome(true, $"deleted {Path.GetFileName(source.Path)}", source.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return SubtitleOutcome.No("the file could not be deleted: " + ex.Message);
            }
        }

        /// <summary>Finds one source by the identifier the inventory gave it.</summary>
        private (BaseItem? Item, SubtitleSource? Source, string? Error) Find(
            Guid itemId,
            string sourceId,
            PluginConfiguration config)
        {
            var item = _library.GetItemById(itemId);
            if (item is null)
            {
                return (null, null, "that item is not in the library any more");
            }

            var inventory = Inventory(itemId, config, probeWritable: false);
            var source = inventory?.Sources.FirstOrDefault(s =>
                string.Equals(s.Id, sourceId, StringComparison.Ordinal));

            // Looked up against a freshly built listing every time, which is what makes
            // the token safe: nothing is reachable through the API that is not a
            // subtitle belonging to the item named in the same request.
            return source is null
                ? (item, null, "that subtitle is no longer listed for this item")
                : (item, source, null);
        }

        private static SubtitleSource Describe(
            TrackCandidate track,
            TrackReport? stored,
            PluginConfiguration config,
            DateTime? checkedUtc)
        {
            var kind = string.IsNullOrEmpty(track.Path)
                ? SourceKind.Embedded
                : SidecarNaming.Classify(Path.GetFileName(track.Path), config.RepairSuffix, config.HeardSuffix);

            long size = 0;
            DateTime? modified = null;

            if (!string.IsNullOrEmpty(track.Path))
            {
                try
                {
                    var file = new FileInfo(track.Path);
                    if (file.Exists)
                    {
                        size = file.Length;
                        modified = file.LastWriteTimeUtc;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // A stream whose file has gone is still worth listing: it is what
                    // the server believes it has, and the manager saying so is more use
                    // than the row silently missing.
                }
            }

            return new SubtitleSource(
                "stream_" + track.Index.ToString(CultureInfo.InvariantCulture),
                kind,
                track.Index,
                LanguageCodes.Normalize(track.Language),
                track.Title,
                string.IsNullOrEmpty(track.Path) ? null : Path.GetFileName(track.Path),
                track.Path,
                size,
                modified,
                track.IsText,
                track.IsForced,
                track.IsHearingImpaired,
                track.IsDefault,
                stored?.CueCount,
                stored?.Verdict,
                stored?.Sync?.Reason ?? stored?.Skipped,
                stored is null ? null : checkedUtc);
        }

        private static SubtitleSource FromFile(FileInfo file, string mediaPath, PluginConfiguration config)
        {
            var extension = file.Extension.ToLowerInvariant();

            return new SubtitleSource(
                SidecarNaming.Token(file.FullName),
                SidecarNaming.Classify(file.Name, config.RepairSuffix, config.HeardSuffix),
                null,
                SidecarNaming.LanguageOf(file.Name, Path.GetFileName(mediaPath)),
                null,
                file.Name,
                file.FullName,
                file.Length,
                file.LastWriteTimeUtc,
                SidecarNaming.Editable.Contains(extension, StringComparer.OrdinalIgnoreCase),
                file.Name.Contains(".forced.", StringComparison.OrdinalIgnoreCase),
                file.Name.Contains(".sdh.", StringComparison.OrdinalIgnoreCase),
                false,
                null,
                null,
                null,
                null);
        }

        /// <summary>Every subtitle file beside the media the server has not already listed.</summary>
        private IEnumerable<FileInfo> Beside(string mediaPath, HashSet<string> seen)
        {
            FileInfo[] files;

            try
            {
                var directory = Path.GetDirectoryName(mediaPath);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    yield break;
                }

                files = new DirectoryInfo(directory).GetFiles();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogDebug(ex, "Cicerone: could not list the folder beside {Path}", mediaPath);
                yield break;
            }

            var media = Path.GetFileName(mediaPath);

            foreach (var file in files)
            {
                // The sweep exists for the gap between Cicerone writing a file and the
                // server scanning it: a repair made two minutes ago is a real subtitle
                // the owner should be able to see and manage, and until the next scan
                // the server has never heard of it.
                if (!seen.Contains(file.FullName) && SidecarNaming.BelongsTo(file.Name, media))
                {
                    yield return file;
                }
            }
        }

        private bool CanWriteBeside(string? mediaPath)
        {
            try
            {
                var directory = Path.GetDirectoryName(mediaPath);
                if (string.IsNullOrEmpty(directory))
                {
                    return false;
                }

                // Asked by trying rather than by reading permissions, which are a poor
                // guide inside a container: the mount is what usually says no, and it
                // says so at write time.
                var probe = Path.Combine(directory, ".cicerone-write-test");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                _logger.LogDebug(ex, "Cicerone: the folder beside {Path} is not writable", mediaPath);
                return false;
            }
        }
    }
}
