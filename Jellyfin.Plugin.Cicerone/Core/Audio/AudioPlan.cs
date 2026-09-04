using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Cicerone.Core.Language;

namespace Jellyfin.Plugin.Cicerone.Core.Audio
{
    /// <summary>The container a clip is handed to the transcriber in.</summary>
    public enum ClipFormat
    {
        /// <summary>Opus in Ogg. Tiny, and every hosted transcriber accepts it.</summary>
        Opus = 0,

        /// <summary>MP3. For endpoints that will not take Opus.</summary>
        Mp3 = 1,

        /// <summary>16-bit PCM in WAV. Uncompressed, for local servers that would rather not decode.</summary>
        Wav = 2,
    }

    /// <summary>One audio track, described without Jellyfin's types.</summary>
    /// <param name="Index">The stream index within the file.</param>
    /// <param name="Language">The language it is tagged with.</param>
    /// <param name="IsDefault">Whether the container marks it default.</param>
    /// <param name="Channels">How many channels it carries.</param>
    /// <param name="Title">Its title, when it has one.</param>
    public sealed record AudioTrack(int Index, string Language, bool IsDefault, int Channels, string? Title);

    /// <summary>
    /// Builds the ffmpeg command line that cuts one window of audio out of a file.
    /// </summary>
    /// <remarks>
    /// <b>Everything about the sync measurement rests on this seek landing where it
    /// was asked to.</b> Cicerone's whole output is a difference between two clocks,
    /// and the audio clock is defined by where ffmpeg actually started reading. A
    /// seek that quietly lands on the nearest keyframe two seconds early does not
    /// produce a worse measurement — it produces a confident measurement that is two
    /// seconds wrong, in the same direction, at every anchor, which fits a perfectly
    /// straight line and reads as a real constant offset. There is no way to catch
    /// that downstream. So it is caught here.
    /// <para>
    /// The command is built as a pure string and asserted on in the tests, because
    /// there is no ffmpeg on the machine this was written on and the arguments are
    /// the part that can be got wrong silently.
    /// </para>
    /// </remarks>
    public static class AudioPlan
    {
        /// <summary>
        /// How far before the window ffmpeg is told to seek, before trimming back.
        /// </summary>
        /// <remarks>
        /// The fast/accurate idiom, and the reason it is used rather than either half
        /// alone. <c>-ss</c> before <c>-i</c> seeks the container in constant time
        /// instead of decoding an hour of film to reach minute sixty, and <c>-ss</c>
        /// after <c>-i</c> trims by decoding, which is exact. Doing the coarse seek
        /// two seconds early and then discarding those two seconds gets both: the
        /// clip begins at the requested sample, and getting there took milliseconds.
        /// </remarks>
        public const double PrerollSeconds = 2.0;

        /// <summary>The sample rate every clip is resampled to.</summary>
        /// <remarks>
        /// 16 kHz mono, which is what speech recognition models are trained on and
        /// what they resample to anyway. Sending 48 kHz stereo means paying to move
        /// six times the bytes so that the far end can throw five sixths of them
        /// away.
        /// </remarks>
        public const int SampleRate = 16000;

        /// <summary>Builds the ffmpeg arguments for one clip, written to stdout.</summary>
        /// <param name="path">The media file.</param>
        /// <param name="start">Where the window begins.</param>
        /// <param name="duration">How long the window is.</param>
        /// <param name="audioStreamIndex">The audio stream to take, as its index within the file.</param>
        /// <param name="format">The container to emit.</param>
        /// <returns>The complete argument string.</returns>
        public static string ClipArguments(
            string path,
            TimeSpan start,
            TimeSpan duration,
            int audioStreamIndex,
            ClipFormat format = ClipFormat.Opus)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);

            // A window near the very beginning has less than the preroll available in
            // front of it, and asking ffmpeg to seek to a negative time is undefined.
            // Whatever is taken off the coarse seek has to come off the trim as well,
            // or the clip starts late by the difference.
            var preroll = Math.Min(PrerollSeconds, Math.Max(start.TotalSeconds, 0));
            var coarse = Math.Max(start.TotalSeconds - preroll, 0);

            var arguments = new List<string>
            {
                "-nostdin",
                "-hide_banner",
                "-loglevel", "error",
                "-accurate_seek",
                "-ss", Seconds(coarse),
                "-i", Quote(path),
                "-ss", Seconds(preroll),
                "-t", Seconds(duration.TotalSeconds),

                // By stream index rather than by audio-stream ordinal: 0:a:1 means
                // "the second audio stream", and the index Jellyfin reports is the
                // stream's position among all streams in the file. Mixing the two
                // reads a different track than the one that was chosen, which on a
                // disc rip is a commentary.
                "-map", "0:" + audioStreamIndex.ToString(CultureInfo.InvariantCulture),
                "-vn", "-sn", "-dn",
                "-ac", "1",
                "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
            };

            arguments.AddRange(format switch
            {
                // 16 kbit/s mono Opus is about 60 KB for a thirty-second window and
                // is transparent for speech. Nothing downstream is listening to it.
                ClipFormat.Opus => (string[])["-c:a", "libopus", "-b:a", "16k", "-f", "ogg"],
                ClipFormat.Mp3 => ["-c:a", "libmp3lame", "-b:a", "48k", "-f", "mp3"],
                _ => ["-c:a", "pcm_s16le", "-f", "wav"],
            });

            arguments.Add("pipe:1");
            return string.Join(' ', arguments);
        }

        /// <summary>
        /// Chooses which audio track to listen to for a given subtitle language.
        /// </summary>
        /// <param name="tracks">Every audio track on the item.</param>
        /// <param name="subtitleLanguage">The language of the subtitle track being checked.</param>
        /// <returns>The track, or null when the item carries no audio at all.</returns>
        /// <remarks>
        /// <b>Matching the subtitle's language is not a nicety.</b> A release carrying
        /// a German dub as its default track, checked against an English subtitle
        /// file, shares almost no words with it — so every anchor comes back with
        /// votes and no agreement, and a perfectly good subtitle file is reported as
        /// belonging to a different cut. Preferring the audio track in the subtitle's
        /// own language turns that from a wrong answer into a right one, and costs a
        /// comparison.
        /// <para>
        /// A dub is still the same timeline, so it would serve for measuring drift if
        /// it shared any vocabulary. It does not, which is why this is a language
        /// match rather than a fallback to the default track.
        /// </para>
        /// </remarks>
        public static AudioTrack? ChooseAudio(IReadOnlyList<AudioTrack> tracks, string? subtitleLanguage)
        {
            ArgumentNullException.ThrowIfNull(tracks);

            if (tracks.Count == 0)
            {
                return null;
            }

            var wanted = LanguageCodes.Normalize(subtitleLanguage);

            var matching = wanted.Length > 0
                ? tracks.Where(t => LanguageCodes.Same(t.Language, wanted)).ToList()
                : [];

            var pool = matching.Count > 0 ? matching : tracks;

            // Within the pool: the default track first, then the one with the most
            // channels. A commentary track is usually stereo beside a 5.1 feature
            // mix, and it is words spoken over the film rather than in it — an
            // alignment against one measures nothing.
            return pool
                .OrderByDescending(t => t.IsDefault)
                .ThenByDescending(t => t.Channels)
                .ThenBy(t => t.Index)
                .First();
        }

        /// <summary>Formats a duration the way ffmpeg wants it.</summary>
        /// <param name="seconds">The value.</param>
        /// <returns>Seconds to millisecond precision, invariant.</returns>
        public static string Seconds(double seconds) =>
            Math.Round(Math.Max(seconds, 0), 3).ToString("0.###", CultureInfo.InvariantCulture);

        private static string Quote(string path) => "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
