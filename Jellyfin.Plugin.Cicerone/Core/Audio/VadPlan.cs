using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Cicerone.Core.Audio
{
    /// <summary>A stretch of the media where somebody is speaking.</summary>
    /// <param name="Start">When it begins.</param>
    /// <param name="End">When it ends.</param>
    public readonly record struct SpeechSpan(TimeSpan Start, TimeSpan End)
    {
        /// <summary>Gets how long the span runs.</summary>
        public TimeSpan Duration => End - Start;
    }

    /// <summary>
    /// Builds the ffmpeg command that reports where a file has speech in it, and
    /// reads the answer back.
    /// </summary>
    /// <remarks>
    /// <b>This is what replaced buying a transcript to answer a timing question.</b>
    /// Knowing <em>when</em> somebody spoke is enough to align a subtitle track
    /// against the dialogue, and unlike knowing <em>what</em> they said it costs
    /// nothing, runs locally, and can be done over the whole file rather than over
    /// the few windows a bill would stretch to. A speech-to-text model is still the
    /// only thing that can read a track's language or write one from scratch; it is
    /// no longer the thing that decides whether a file is in sync.
    /// <para>
    /// The detection is ffmpeg's own <c>silencedetect</c>, which reports the silences
    /// and leaves the speech as what is left between them. Two filters run in front of
    /// it: a high-pass at 200 Hz and a low-pass at 3 kHz, which is roughly the band a
    /// voice occupies. Without them a film's score and its explosions register as
    /// activity exactly like a voice does, and the signal being correlated stops being
    /// about dialogue at all.
    /// </para>
    /// <para>
    /// The command is built as a pure string and asserted on in the tests, for the
    /// same reason as <see cref="AudioPlan"/>: the arguments are the part that can be
    /// wrong silently, and there is no ffmpeg on the machine this was written on.
    /// </para>
    /// </remarks>
    public static class VadPlan
    {
        /// <summary>The level below which audio counts as silence, in dBFS.</summary>
        /// <remarks>
        /// Read against a band-limited signal rather than the raw mix, which is what
        /// makes one number work across films. -30 dB is well under conversational
        /// dialogue and well over room tone.
        /// </remarks>
        public const double DefaultNoiseFloorDb = -30;

        /// <summary>How long a quiet stretch must run before it counts as a silence.</summary>
        /// <remarks>
        /// A third of a second. Shorter than the gap between two sentences and longer
        /// than the stop in the middle of a word, so a spoken line comes out as one
        /// span instead of one per syllable.
        /// </remarks>
        public const double DefaultMinSilenceSeconds = 0.3;

        /// <summary>Builds the ffmpeg arguments that report a file's silences.</summary>
        /// <param name="path">The media file.</param>
        /// <param name="audioStreamIndex">The audio stream to listen to.</param>
        /// <param name="noiseFloorDb">The level below which audio counts as silence.</param>
        /// <param name="minSilenceSeconds">How long a quiet stretch must run to count.</param>
        /// <returns>The complete argument string.</returns>
        public static string DetectArguments(
            string path,
            int audioStreamIndex,
            double noiseFloorDb = DefaultNoiseFloorDb,
            double minSilenceSeconds = DefaultMinSilenceSeconds)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);

            var filter = string.Create(
                CultureInfo.InvariantCulture,
                $"highpass=f=200,lowpass=f=3000,silencedetect=noise={Db(noiseFloorDb)}dB:d={AudioPlan.Seconds(minSilenceSeconds)}");

            var arguments = new List<string>
            {
                "-nostdin",
                "-hide_banner",
                "-nostats",

                // silencedetect reports through the log at info level, so unlike every
                // other ffmpeg call here the log cannot be quietened — the log *is* the
                // output. Nothing is written to stdout at all.
                "-loglevel", "info",
                "-i", Quote(path),
                "-map", "0:" + audioStreamIndex.ToString(CultureInfo.InvariantCulture),
                "-vn", "-sn", "-dn",
                "-ac", "1",

                // 16 kHz is plenty under a 3 kHz low-pass and turns the decode into the
                // cheapest thing ffmpeg can be asked to do over a whole film.
                "-ar", AudioPlan.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-af", "\"" + filter + "\"",
                "-f", "null",
                "-",
            };

            return string.Join(' ', arguments);
        }

        /// <summary>Reads speech spans out of what silencedetect logged.</summary>
        /// <param name="log">Everything ffmpeg wrote to stderr.</param>
        /// <param name="duration">How long the audio runs, for the final span.</param>
        /// <returns>The stretches that are not silence, in order.</returns>
        /// <remarks>
        /// The complement of the silences, which is the only reason this is not simply
        /// a regex: silencedetect says where the quiet is, and a file that opens or
        /// closes in speech has no marker at either end. A file it found no silence in
        /// at all is one continuous span — that is a legitimate reading of a
        /// wall-to-wall commentary track, and a useless signal to correlate, which is
        /// what <see cref="Sync.SpeechSignal.Coverage"/> exists to notice.
        /// </remarks>
        public static IReadOnlyList<SpeechSpan> ParseSilences(string? log, TimeSpan duration)
        {
            var total = Math.Max(duration.TotalSeconds, 0);
            if (total <= 0)
            {
                return [];
            }

            var silences = new List<(double Start, double End)>();
            double? open = null;

            foreach (var line in (log ?? string.Empty).Split('\n'))
            {
                if (Read(line, "silence_start:") is { } start)
                {
                    // A second start before an end means a line was lost or the log was
                    // interleaved from two filters. Keeping the earlier one is the safe
                    // reading: it makes the silence longer, never the speech.
                    open ??= Math.Max(start, 0);
                    continue;
                }

                if (Read(line, "silence_end:") is { } end && open is { } began)
                {
                    silences.Add((began, Math.Min(Math.Max(end, began), total)));
                    open = null;
                }
            }

            if (open is { } dangling)
            {
                // The file ended without the silence closing, which is what a film that
                // fades out on its credits looks like.
                silences.Add((Math.Min(dangling, total), total));
            }

            var speech = new List<SpeechSpan>();
            var cursor = 0.0;

            foreach (var (start, end) in silences.OrderBy(s => s.Start))
            {
                if (start > cursor)
                {
                    speech.Add(new SpeechSpan(TimeSpan.FromSeconds(cursor), TimeSpan.FromSeconds(start)));
                }

                cursor = Math.Max(cursor, end);
            }

            if (cursor < total)
            {
                speech.Add(new SpeechSpan(TimeSpan.FromSeconds(cursor), TimeSpan.FromSeconds(total)));
            }

            return speech;
        }

        private static double? Read(string line, string key)
        {
            var at = line.IndexOf(key, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            var rest = line[(at + key.Length)..].AsSpan().TrimStart();
            var length = 0;
            while (length < rest.Length && (char.IsAsciiDigit(rest[length]) || rest[length] is '.' or '-' or '+'))
            {
                length++;
            }

            return double.TryParse(rest[..length], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        private static string Db(double value) =>
            Math.Round(value, 1).ToString("0.###", CultureInfo.InvariantCulture);

        private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
