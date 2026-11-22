using System;

namespace Mp4Merger.Core
{
    /// Cutting a failure log down to what fits on screen.
    ///
    /// A broken file makes ffmpeg repeat the same complaint thousands of times,
    /// so the log has to be trimmed -- but from the middle, not the front. The
    /// head is what makes it worth reading:
    ///
    ///     ffmpeg: N-129166-gae9f4540cb-20261121
    ///     (blank)
    ///     #1 a.mp4: video h264 ... / audio aac ...
    ///     #2 b.mp4: ...
    ///     (blank)
    ///     ...thousands of lines, ending with how it ended...
    ///
    /// The build on the first line is the one thing that cannot be recovered
    /// afterwards: it comes from a release tag whose contents are replaced in
    /// place, so once the tag moves on, nothing can say which binary failed.
    /// It is kept always. The per-input summary is kept when there is room.
    ///
    /// This lives on its own because getting it wrong is invisible: the first
    /// attempt kept only the tail and silently dropped the build line, and the
    /// second looked for the first blank line -- which is the one right after
    /// the build line, so the summary was never kept at any input count.
    public static class FailureLog
    {
        public const int DefaultLimit = 4000;
        public const int DefaultHeadRoom = 800;

        public static string ForDisplay(string? log, int limit = DefaultLimit, int headRoom = DefaultHeadRoom)
        {
            if (string.IsNullOrEmpty(log)) return "";
            if (log.Length <= limit) return log;

            string head = HeadOf(log, headRoom);
            int tail = limit - head.Length;
            if (tail <= 0) return head;          // cannot happen while headRoom < limit
            return head + "..." + log.Substring(log.Length - tail);
        }

        /// The leading block worth keeping, never longer than `headRoom` plus the
        /// blank line that closes it.
        private static string HeadOf(string log, int headRoom)
        {
            int afterBuild = log.IndexOf("\n\n", StringComparison.Ordinal);
            if (afterBuild > 0)
            {
                // The summary, if it fits. Its block ends at the SECOND blank
                // line: the first one closes the build line.
                int afterSummary = log.IndexOf("\n\n", afterBuild + 2, StringComparison.Ordinal);
                if (afterSummary > 0 && afterSummary <= headRoom)
                {
                    return log.Substring(0, afterSummary + 2);
                }
                if (afterBuild <= headRoom)
                {
                    return log.Substring(0, afterBuild + 2);
                }
            }

            // No blank line near the top: keep the first line on its own, which
            // is still where the build is named.
            int firstBreak = log.IndexOf('\n');
            if (firstBreak > 0 && firstBreak + 1 <= headRoom)
            {
                return log.Substring(0, firstBreak + 1);
            }

            return "";
        }
    }
}
