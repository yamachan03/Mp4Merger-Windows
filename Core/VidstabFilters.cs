using System;

namespace Mp4Merger.Core
{
    /// The two vidstab filter strings, built so no path ever goes inside one.
    ///
    /// Gimbal stabilisation never worked on Windows. vidstab takes the transform
    /// file as a filter option, and ffmpeg's filter parser splits on ':' and ','
    /// and unescapes twice, so "C:/Users/..." is read as an option named "C" and
    /// the run dies with "No option name near '/Users/...'". Measured 2026-11-22
    /// against ffmpeg 8.0.1 and N-129166, identically on both:
    ///
    ///     result=C:/x      failed      result=C\:/x    worked
    ///     result=C\:/x     failed      result=C\\:/x   worked
    ///     result='C:/x'    failed      result=C\\:/x  failed
    ///
    /// So it was never a change in ffmpeg: the one backslash the app used had
    /// always been wrong. Two would work, but the user's temp folder can also
    /// hold a space, a comma or an apostrophe, and those break a filter string
    /// too. Passing a bare file name and running ffmpeg in the folder that holds
    /// it keeps every one of them out of the filter, and was measured working on
    /// both builds as well.
    public static class VidstabFilters
    {
        /// Pass 1: measure the shake and write it to `transformFileName`.
        public static string Detect(string transformFileName, int shakiness, int accuracy)
        {
            EnsureBareName(transformFileName);
            return $"vidstabdetect=shakiness={shakiness}:accuracy={accuracy}:result={transformFileName}";
        }

        /// Pass 2: apply what pass 1 wrote to `transformFileName`.
        public static string Transform(string transformFileName, int smoothing)
        {
            EnsureBareName(transformFileName);
            return $"vidstabtransform=input={transformFileName}:smoothing={smoothing}:crop=black:optzoom=1";
        }

        /// Refuses anything that is not a plain file name. The whole point is
        /// that ffmpeg's working directory carries the location, so a path
        /// arriving here means someone has undone the fix.
        private static void EnsureBareName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("The transform file needs a name.", nameof(name));
            }

            foreach (char c in new[] { ':', '\\', '/', ',', ';', '=', '\'', '[', ']', '\"' })
            {
                if (name.IndexOf(c) >= 0)
                {
                    throw new ArgumentException(
                        $"'{name}' is not a bare file name: ffmpeg's filter parser would " +
                        $"read '{c}' as punctuation. Run ffmpeg in the folder instead.",
                        nameof(name));
                }
            }
        }
    }
}
