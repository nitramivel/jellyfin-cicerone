using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.Cicerone.Core.Sync
{
    /// <summary>What Cicerone concluded about one subtitle track.</summary>
    public enum Verdict
    {
        /// <summary>Nothing could be decided. The report says why.</summary>
        Unknown = 0,

        /// <summary>The track matches the dialogue and holds its timing throughout.</summary>
        InSync = 1,

        /// <summary>The whole track is out by a constant amount, correctable by shifting it.</summary>
        Offset = 2,

        /// <summary>The track runs at the wrong speed: right in one place, wrong in another.</summary>
        Drifting = 3,

        /// <summary>The track is not this dialogue — a different cut, or the wrong film.</summary>
        Mismatched = 4,

        /// <summary>The track is not written in the language it claims.</summary>
        WrongLanguage = 5,

        /// <summary>The track holds no dialogue to check — forced, image-only, or empty.</summary>
        NoDialogue = 6,
    }

    /// <summary>
    /// A finished assessment of one track.
    /// </summary>
    /// <param name="Verdict">The conclusion.</param>
    /// <param name="Correction">The fit behind it.</param>
    /// <param name="Anchors">The measurements the fit was made from.</param>
    /// <param name="WorstErrorSeconds">
    /// The largest error anywhere in the runtime. This is the number a viewer
    /// experiences, and it is not the offset: a drifting track has an offset near
    /// zero and is unwatchable by the end.
    /// </param>
    /// <param name="Reason">One sentence for the report.</param>
    public sealed record SyncAssessment(
        Verdict Verdict,
        Correction Correction,
        IReadOnlyList<AnchorPoint> Anchors,
        double WorstErrorSeconds,
        string Reason)
    {
        /// <summary>Gets whether a repair would improve this track.</summary>
        public bool Repairable => Verdict is Verdict.Offset or Verdict.Drifting;
    }

    /// <summary>
    /// Turns a fit into a conclusion.
    /// </summary>
    /// <remarks>
    /// Kept apart from the fitting because the two answer different questions and
    /// change for different reasons. <see cref="DriftFit"/> answers "what line do
    /// these points make"; this answers "should the owner care", which is a question
    /// about thresholds and taste, and every threshold in it is a setting.
    /// <para>
    /// The order the tests run in is the design. Whether a straight line described
    /// the anchors at all is asked <em>before</em> what the line says, because a fit
    /// through points that do not lie on a line still produces a slope and an
    /// intercept, and acting on those would retime a perfectly good file into
    /// nonsense.
    /// </para>
    /// </remarks>
    public static class SyncVerdictBuilder
    {
        /// <summary>Assesses a track.</summary>
        /// <param name="anchors">Every anchor measured, including the ones that failed.</param>
        /// <param name="correction">The fit.</param>
        /// <param name="runtime">The item's runtime, over which the worst error is worked out.</param>
        /// <param name="toleranceSeconds">How far out a track may be and still count as in sync.</param>
        /// <param name="maxResidualSeconds">
        /// How far the anchors may sit from the fitted line before the line is
        /// disbelieved and the track called mismatched.
        /// </param>
        /// <returns>The assessment.</returns>
        public static SyncAssessment Assess(
            IReadOnlyList<AnchorPoint> anchors,
            Correction correction,
            TimeSpan runtime,
            double toleranceSeconds = 0.35,
            double maxResidualSeconds = 1.5)
        {
            ArgumentNullException.ThrowIfNull(anchors);

            var confident = anchors.Count(a => a.Confidence >= DriftFit.MinConfidence);

            if (anchors.Count == 0)
            {
                return new SyncAssessment(
                    Verdict.Unknown, correction, anchors, 0, "nothing was listened to");
            }

            if (confident == 0)
            {
                // Every window produced words and none of them agreed on anything.
                // That is what a subtitle file for another film looks like; it is also
                // what a failed transcription looks like, and the two cannot be told
                // apart from here, so the reason says both.
                return new SyncAssessment(
                    Verdict.Mismatched,
                    correction,
                    anchors,
                    0,
                    Plural(anchors.Count, "window", "windows")
                        + " listened to and none matched the subtitles — a different cut, the wrong "
                        + "file, or audio the transcriber could not read");
            }

            if (confident == 1)
            {
                var single = Math.Abs(correction.OffsetSeconds);
                return new SyncAssessment(
                    single <= toleranceSeconds ? Verdict.InSync : Verdict.Offset,
                    correction,
                    anchors,
                    single,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"only one window matched, so the offset is {Format(correction.OffsetSeconds)} "
                        + "and drift could not be measured at all"));
            }

            if (correction.ResidualSeconds > maxResidualSeconds)
            {
                return new SyncAssessment(
                    Verdict.Mismatched,
                    correction,
                    anchors,
                    Math.Abs(correction.OffsetSeconds),
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"the windows disagree by {correction.ResidualSeconds:0.0}s about how far out the "
                        + "track is, which no single shift can explain — most likely a different cut"));
            }

            var atStart = correction.ErrorAt(TimeSpan.Zero);
            var atEnd = correction.ErrorAt(runtime > TimeSpan.Zero ? runtime : anchors[^1].At);
            var worst = Math.Max(Math.Abs(atStart), Math.Abs(atEnd));
            var spread = Math.Abs(atEnd - atStart);

            if (worst <= toleranceSeconds)
            {
                return new SyncAssessment(
                    Verdict.InSync,
                    correction,
                    anchors,
                    worst,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"within {Format(worst)} of the dialogue across "
                        + Plural(confident, "window", "windows")));
            }

            if (spread > toleranceSeconds)
            {
                var named = correction.FrameRate is { } pair
                    ? $" — {pair.Label}, so the file was timed against a different transfer"
                    : string.Empty;

                return new SyncAssessment(
                    Verdict.Drifting,
                    correction,
                    anchors,
                    worst,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"drifts from {Format(atStart)} at the start to {Format(atEnd)} at the end{named}"));
            }

            return new SyncAssessment(
                Verdict.Offset,
                correction,
                anchors,
                worst,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the whole track is {Format(correction.OffsetSeconds)} out, evenly"));
        }

        /// <summary>Renders a signed number of seconds the way the report reads it.</summary>
        /// <param name="seconds">The error, positive meaning the subtitle is late.</param>
        /// <returns>Something like <c>1.4s late</c>.</returns>
        public static string Format(double seconds)
        {
            var magnitude = Math.Abs(seconds);
            var word = seconds >= 0 ? "late" : "early";
            return string.Create(CultureInfo.InvariantCulture, $"{magnitude:0.0}s {word}");
        }

        private static string Plural(int count, string one, string many)
            => string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many)}");
    }
}
