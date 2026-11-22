using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Mp4Merger.Core
{
    /// ffmpeg writes its own progress to stderr as a status line it rewrites with
    /// a carriage return, e.g.
    ///
    ///     frame=  181 fps=0.0 q=-1.0 Lsize=  7958KiB time=00:00:07.46 bitrate=...
    ///
    /// which is enough to drive a progress bar without asking for `-progress`.
    /// Reading the position out of that line is pure text work, so it lives here
    /// on its own and every caller that runs a long ffmpeg shares it.
    public static class FfmpegProgress
    {
        // The hour field is not fixed at two digits: a long enough file prints
        // `time=100:00:00.00`. Before the first frame ffmpeg prints a negative
        // position, so the sign is captured rather than left to be dropped.
        private static readonly Regex TimePattern =
            new(@"time=(-?)(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.Compiled);

        /// The position ffmpeg reports on this line, in seconds, or null when the
        /// line carries no position. A negative position counts as 0.
        public static double? PositionSeconds(string? line)
        {
            if (string.IsNullOrEmpty(line)) return null;

            var match = TimePattern.Match(line);
            if (!match.Success) return null;

            if (!int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours) ||
                !int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) ||
                !double.TryParse(match.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            {
                return null;
            }

            if (match.Groups[1].Value == "-") return 0;

            return hours * 3600.0 + minutes * 60.0 + seconds;
        }

        /// Where to put the bar, as a percentage, for a position inside a job that
        /// may be one of several: `offsetSeconds` is how much was already done and
        /// `totalSeconds` the whole job. Returns null when there is nothing to
        /// scale against, so the caller leaves the bar alone.
        public static double? Percent(string? line, double totalSeconds, double offsetSeconds)
        {
            if (totalSeconds <= 0) return null;
            if (PositionSeconds(line) is not { } position) return null;

            return Math.Clamp((offsetSeconds + position) / totalSeconds * 100.0, 0, 100);
        }
    }
}
