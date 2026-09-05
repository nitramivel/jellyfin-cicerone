using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Cicerone.Configuration;
using Jellyfin.Plugin.Cicerone.Core.Language;
using Jellyfin.Plugin.Cicerone.Core.Subtitles;
using Jellyfin.Plugin.Cicerone.Core.Sync;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Cicerone.Services
{
    /// <summary>
    /// Writes a corrected copy of a subtitle track.
    /// </summary>
    /// <remarks>
    /// <b>Cicerone never edits a subtitle file.</b> Not the embedded stream, which it
    /// could not write to without remuxing the container, and not the external
    /// sidecar, which it could. A correction is a measurement, measurements have
    /// error, and the failure mode of editing in place is destroying a file somebody
    /// may have spent an evening timing by hand. So a repair is always a new file
    /// beside the original, and removing everything Cicerone has written restores
    /// exactly the library that was there before it was installed.
    /// <para>
    /// The name carries the language and a marker: <c>Film.en.cicerone.srt</c>.
    /// Jellyfin's scanner reads the language from the name and picks it up as a
    /// selectable track on the next scan, and the marker is what makes Cicerone's own
    /// output identifiable — nothing here will ever overwrite a file it did not
    /// write.
    /// </para>
    /// </remarks>
    public sealed class RepairWriter
    {
        private readonly IApplicationPaths _paths;
        private readonly ILibraryManager _library;
        private readonly ILogger<RepairWriter> _logger;

        /// <summary>Initialises a new instance of the <see cref="RepairWriter"/> class.</summary>
        /// <param name="paths">Server paths, for the data directory fallback.</param>
        /// <param name="library">Used to ask for a rescan once a repair lands.</param>
        /// <param name="logger">The logger.</param>
        public RepairWriter(IApplicationPaths paths, ILibraryManager library, ILogger<RepairWriter> logger)
        {
            _paths = paths;
            _library = library;
            _logger = logger;
        }

        /// <summary>Builds the file name a repair is written under.</summary>
        /// <param name="mediaFileName">The media file's name, with extension.</param>
        /// <param name="language">The track's language.</param>
        /// <param name="suffix">The marker, from the settings.</param>
        /// <param name="hearingImpaired">Whether the source track was SDH.</param>
        /// <returns>The sidecar's file name.</returns>
        /// <remarks>
        /// Pure, and tested, because Jellyfin's scanner parses this name to work out
        /// what the file is. Getting the ordering wrong produces a track labelled
        /// "cicerone" in a viewer's language menu.
        /// </remarks>
        public static string SidecarName(
            string mediaFileName,
            string language,
            string suffix,
            bool hearingImpaired)
        {
            ArgumentException.ThrowIfNullOrEmpty(mediaFileName);

            var stem = Path.GetFileNameWithoutExtension(mediaFileName);
            var code = LanguageCodes.Normalize(language);
            var marker = string.IsNullOrWhiteSpace(suffix) ? "cicerone" : suffix.Trim().Trim('.');

            var parts = new List<string> { stem };
            if (code.Length > 0)
            {
                parts.Add(code);
            }

            // Jellyfin reads "sdh" and "forced" as flags from the name. Keeping the
            // flag means a repaired hearing-impaired track stays labelled as one
            // instead of appearing as a second, mysteriously duplicated language.
            if (hearingImpaired)
            {
                parts.Add("sdh");
            }

            parts.Add(marker);
            return string.Join('.', parts) + ".srt";
        }

        /// <summary>Writes a corrected copy of a track.</summary>
        /// <param name="item">The item.</param>
        /// <param name="track">The track being corrected.</param>
        /// <param name="cues">Its cues, raw and unmodified.</param>
        /// <param name="correction">The correction to apply.</param>
        /// <param name="config">The settings.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Where it was written, or null when it could not be.</returns>
        public async Task<string?> WriteAsync(
            BaseItem item,
            TrackCandidate track,
            IReadOnlyList<Cue> cues,
            Correction correction,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(track);
            ArgumentNullException.ThrowIfNull(cues);
            ArgumentNullException.ThrowIfNull(config);

            if (cues.Count == 0 || correction.IsIdentity || string.IsNullOrWhiteSpace(item.Path))
            {
                return null;
            }

            return await WriteCuesAsync(
                    item,
                    Retiming.Apply(cues, correction),
                    track.Language,
                    config.RepairSuffix,
                    track.IsHearingImpaired,
                    config.RepairMode == RepairMode.WriteSidecarAndRefresh,
                    config,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>Writes a set of cues out as a sidecar beside the media.</summary>
        /// <param name="item">The item.</param>
        /// <param name="cues">The cues to write.</param>
        /// <param name="language">The language to name the file for.</param>
        /// <param name="suffix">The marker that identifies the file as Cicerone's.</param>
        /// <param name="hearingImpaired">Whether to keep the SDH flag in the name.</param>
        /// <param name="refresh">Whether to ask the server to rescan afterwards.</param>
        /// <param name="config">The settings.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Where it was written, or null when it could not be.</returns>
        /// <remarks>
        /// Shared by every route that produces a subtitle file — a repair, a
        /// transcription, and a copy saved out of the manager — because all three have
        /// exactly the same two things to get right: the name Jellyfin's scanner will
        /// parse, and the write-to-a-temporary-name-and-move that stops an interrupted
        /// run leaving half a file a player will load.
        /// </remarks>
        public async Task<string?> WriteCuesAsync(
            BaseItem item,
            IReadOnlyList<Cue> cues,
            string language,
            string suffix,
            bool hearingImpaired,
            bool refresh,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(cues);
            ArgumentNullException.ThrowIfNull(config);

            if (cues.Count == 0 || string.IsNullOrWhiteSpace(item.Path))
            {
                return null;
            }

            var name = SidecarName(Path.GetFileName(item.Path), language, suffix, hearingImpaired);
            var text = SrtWriter.Write(cues);

            if (config.WriteBesideMedia)
            {
                var beside = Path.Combine(Path.GetDirectoryName(item.Path) ?? string.Empty, name);
                if (await WriteTextAsync(beside, text, cancellationToken).ConfigureAwait(false))
                {
                    if (refresh)
                    {
                        Refresh(item);
                    }

                    return beside;
                }
            }

            // A read-only bind mount is a common arrangement, and a file in the data
            // directory is still something the owner can copy out. It is explicitly
            // not equivalent: Jellyfin will not pick a subtitle up from there, so the
            // report says where the file went.
            var fallback = Path.Combine(DataDirectory(), item.Id.ToString("N", CultureInfo.InvariantCulture), name);
            return await WriteTextAsync(fallback, text, cancellationToken).ConfigureAwait(false)
                ? fallback
                : null;
        }

        /// <summary>Writes text to a path, atomically.</summary>
        /// <param name="path">Where to write.</param>
        /// <param name="text">What to write.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Whether it landed.</returns>
        /// <remarks>
        /// Public because the subtitle manager saves through it too, and the atomic
        /// move is the whole reason this is not a call to
        /// <see cref="File.WriteAllTextAsync(string, string?, CancellationToken)"/>.
        /// <b>It applies no ownership rule of its own</b> — deciding whether a
        /// particular file may be written is the caller's job, and for anything the
        /// owner did not explicitly ask for the answer is no.
        /// </remarks>
        public Task<bool> WriteTextAsync(string path, string text, CancellationToken cancellationToken)
        {
            // UTF-8 without a BOM. Some players treat a BOM as the first character of
            // the first cue's sequence number and refuse the whole file.
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            return TryWriteAsync(path, text, encoding, cancellationToken);
        }

        /// <summary>Asks the server to notice a file that has just appeared.</summary>
        /// <param name="item">The item it belongs to.</param>
        public void RequestRefresh(BaseItem item) => Refresh(item);

        /// <summary>The directory Cicerone keeps its own files in.</summary>
        /// <returns>The path.</returns>
        public string DataDirectory() => Path.Combine(_paths.DataPath, "cicerone");

        private async Task<bool> TryWriteAsync(
            string path,
            string text,
            Encoding encoding,
            CancellationToken cancellationToken)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Written to a temporary name and moved into place. A run interrupted
                // mid-write — which happens, because installing any plugin tears the
                // host down — must not leave half a subtitle file that a player will
                // happily load and truncate the film at.
                var temporary = path + ".tmp";
                await File.WriteAllTextAsync(temporary, text, encoding, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
            {
                _logger.LogDebug(ex, "Cicerone: could not write a repaired subtitle to {Path}", path);
                return false;
            }
        }

        private void Refresh(BaseItem item)
        {
            try
            {
                // A metadata refresh is what makes the new sidecar appear as a
                // selectable track. Queued rather than awaited: the run has hundreds
                // of items left and a full refresh of this one can take a while.
                _library.QueueLibraryScan();
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                _logger.LogDebug(ex, "Cicerone: could not queue a library scan after repairing {Item}", item.Name);
            }
        }
    }
}
