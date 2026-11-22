using System;

namespace Mp4Merger.Core
{
    /// Which build of ffmpeg is in use.
    ///
    /// This matters more than it looks. The app downloads ffmpeg from BtbN's
    /// "latest" tag, whose contents are replaced in place, so the binary that
    /// misbehaved is gone by the time anyone looks: the NVENC freeze of
    /// 2026-11-08 could not be investigated because the build had been swapped
    /// out by 11-14 (see CLAUDE.md). A failure log that names the build is the
    /// difference between a lead and a dead end.
    ///
    /// Reading it costs nothing: ffmpeg prints its banner to stderr on every
    /// run that does not pass -hide_banner, and the encoder detection at the
    /// start of a job already makes such a run.
    public static class FfmpegVersion
    {
        /// The banner's first line, e.g.
        /// "ffmpeg version N-129166-gae9f4540cb-20261121 Copyright (c) ...",
        /// or null when the output carries no banner.
        public static string? FromBanner(string? output)
        {
            if (string.IsNullOrEmpty(output)) return null;

            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("ffmpeg version ", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("ffprobe version ", StringComparison.OrdinalIgnoreCase))
                {
                    return line;
                }
            }
            return null;
        }

        /// The build recorded when ffmpeg was last downloaded, read back from
        /// `ffmpeg_version.txt`. A fallback for when the banner could not be
        /// read: the encoder-detection run may have timed out, ffmpeg may not
        /// have started, or someone may have added -hide_banner to it. Null when
        /// the file is not there -- it is only written by a download, so a user
        /// who installed ffmpeg before this existed has nothing here until the
        /// next update.
        public static string? Recorded()
        {
            try
            {
                string path = System.IO.Path.Combine(AppPaths.DataDir, "ffmpeg_version.txt");
                if (!System.IO.File.Exists(path)) return null;
                return BuildOf(System.IO.File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        /// Just the build's own name, e.g. "N-129166-gae9f4540cb-20261121" or
        /// "8.0.1-essentials_build-www.gyan.dev". Null when it cannot be read.
        public static string? BuildOf(string? output)
        {
            if (FromBanner(output) is not { } line) return null;

            int start = line.IndexOf(" version ", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;
            start += " version ".Length;

            int end = line.IndexOf(" Copyright", start, StringComparison.OrdinalIgnoreCase);
            string build = end < 0 ? line.Substring(start) : line.Substring(start, end - start);
            build = build.Trim();
            return build.Length == 0 ? null : build;
        }
    }
}
