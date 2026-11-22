using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mp4Merger.Core
{
    /// Which files this app will take in.
    ///
    /// The drop zone used to accept only .mp4 and .mov, which turned away
    /// footage ffmpeg reads perfectly well -- an .mpg from a camcorder, an .avi
    /// off an old drive. There was never a reason for the merge itself: an input
    /// that cannot be stream-copied simply falls through to the re-encode tier,
    /// which is what that tier is for.
    ///
    /// The list stays deliberate rather than "anything ffmpeg opens". A file
    /// picked up by mistake -- a .lrf sidecar sitting beside a drone clip, say --
    /// costs the user a failed run and a confusing message.
    public static class VideoFormats
    {
        /// Containers the app accepts. Output is always .mp4.
        public static readonly IReadOnlyList<string> Extensions = new[]
        {
            ".mp4", ".mov", ".m4v",                 // what it always took
            ".mpg", ".mpeg", ".m2v", ".mts", ".m2ts", ".ts",  // camcorders, discs, broadcast
            ".avi", ".wmv", ".mkv", ".webm", ".flv", ".3gp",  // everything else common
        };

        /// Sidecars and companions that sit next to real footage and are not it.
        /// A drone writes .lrf low-resolution proxies beside every clip.
        private static readonly string[] NeverVideo = { ".lrf", ".thm", ".xml", ".srt" };

        /// True when this looks like footage the app should try to open.
        public static bool IsSupported(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            string name;
            try { name = Path.GetFileName(path); }
            catch { return false; }
            if (name.Length == 0) return false;

            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext.Length == 0) return false;
            if (NeverVideo.Contains(ext)) return false;

            return Extensions.Contains(ext);
        }

        /// The filter string an open dialog wants, e.g.
        /// "Video files (*.mp4;*.mov)|*.mp4;*.mov|All files (*.*)|*.*".
        public static string DialogFilter(string videoFilesLabel, string allFilesLabel)
        {
            string patterns = string.Join(";", Extensions.Select(e => "*" + e));
            return $"{videoFilesLabel} ({patterns})|{patterns}|{allFilesLabel} (*.*)|*.*";
        }
    }
}
