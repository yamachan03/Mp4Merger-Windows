using System;
using System.IO;

namespace Mp4Merger.Core
{
    public static class AppPaths
    {
        // Per-user writable data directory. The install directory (e.g. Program Files)
        // may be read-only, so downloaded tools, config and logs must live here.
        public static string DataDir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mp4Merger");

        public static string EnsureDataDir()
        {
            Directory.CreateDirectory(DataDir);
            return DataDir;
        }

        // Tools may sit next to the exe (dev checkouts, manual setups, old installs)
        // or in DataDir (auto-download target). Never resolve via PATH or the current
        // directory — that allows binary planting.
        public static string ResolveTool(string toolName)
        {
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, toolName + ".exe");
            if (File.Exists(local)) return local;
            return Path.Combine(DataDir, toolName + ".exe");
        }
    }
}
