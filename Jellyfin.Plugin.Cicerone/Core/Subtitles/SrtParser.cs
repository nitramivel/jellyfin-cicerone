using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.Cicerone.Core.Subtitles
{
    /// <summary>
    /// Parses SRT, and only SRT.
    /// </summary>
    /// <remarks>
    /// <b>Cicerone parses exactly one subtitle format, forever.</b> Jellyfin's
    /// <c>ISubtitleEncoder</c> is always asked for <c>"srt"</c> and converts ASS/SSA,
    /// mov_text, WebVTT and external files into it on the way out, so a multi-format
    /// parser would be a second implementation of something the server already does.
    /// <para>
    /// Forgiving on purpose. Files in the wild have missing blank lines, stray BOMs,
    /// comma-versus-period decimal separators and sequence numbers that restart
    /// halfway through. A cue that cannot be read is skipped rather than failing the
    /// file: losing one line costs an alignment nothing, and refusing the file costs
    /// the whole verdict.
    /// </para>
    /// </remarks>
    public static class SrtParser
    {
        /// <summary>Parses an SRT document.</summary>
        /// <param name="content">The file's text.</param>
        /// <returns>The cues, in file order. Never throws.</returns>
        public static IReadOnlyList<Cue> Parse(string? content)
        {
            var cues = new List<Cue>();
            if (string.IsNullOrWhiteSpace(content))
            {
                return cues;
            }

            // A BOM survives most reads and would otherwise break the first cue,
            // silently costing the opening of every file.
            var lines = content.TrimStart('﻿')
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');

            var i = 0;
            while (i < lines.Length)
            {
                // Anchor on the arrow rather than the sequence number, so a file with
                // broken or missing numbering still parses completely.
                var arrow = lines[i].IndexOf("-->", StringComparison.Ordinal);
                if (arrow < 0)
                {
                    i++;
                    continue;
                }

                var timing = lines[i];
                i++;

                if (!TryParseTime(timing[..arrow], out var start)
                    || !TryParseTime(timing[(arrow + 3)..], out var end))
                {
                    continue;
                }

                var body = new List<string>();
                while (i < lines.Length
                    && !string.IsNullOrWhiteSpace(lines[i])
                    && lines[i].IndexOf("-->", StringComparison.Ordinal) < 0)
                {
                    body.Add(lines[i].Trim());
                    i++;
                }

                // A trailing bare number is what a file with no blank-line separators
                // looks like: the next cue's sequence number, swallowed by this one.
                if (body.Count > 0 && body[^1].Length <= 5 && int.TryParse(body[^1], out _))
                {
                    body.RemoveAt(body.Count - 1);
                }

                if (body.Count == 0)
                {
                    continue;
                }

                // An end before its start is a corrupt timing, and it must not reach
                // the aligner: a negative-duration cue has a midpoint outside itself.
                if (end < start)
                {
                    end = start;
                }

                cues.Add(new Cue(start, end, string.Join(' ', body).Trim()));
            }

            return cues;
        }

        /// <summary>
        /// Parses an SRT timestamp: <c>00:01:23,456</c>, and the period-separated
        /// spelling some tools emit.
        /// </summary>
        private static bool TryParseTime(string value, out TimeSpan time)
        {
            time = default;
            var trimmed = value.Trim().Replace(',', '.');

            // Trailing position data — "00:00:01.000 X1:100 X2:200" — is legal and
            // must not defeat the parse.
            var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0)
            {
                trimmed = trimmed[..space];
            }

            if (trimmed.Length == 0)
            {
                return false;
            }

            var parts = trimmed.Split(':');
            if (parts.Length is < 2 or > 3)
            {
                return false;
            }

            var hours = 0;
            var offset = 0;
            if (parts.Length == 3)
            {
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hours))
                {
                    return false;
                }

                offset = 1;
            }

            if (!int.TryParse(parts[offset], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                || !double.TryParse(
                    parts[offset + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return false;
            }

            time = new TimeSpan(hours, minutes, 0) + TimeSpan.FromSeconds(seconds);
            return true;
        }
    }
}
