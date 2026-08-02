using System;
using System.IO;
using System.Text.Json;

namespace Mp4Merger.Core
{
    public class AppConfig
    {
        public string Language { get; set; } = ""; // Empty means not set yet (first launch)
        public string Theme { get; set; } = "dark"; // dark, light, auto
        public bool KeepOptions { get; set; } = false;
        public bool WarnOnSlow { get; set; } = false;
        
        // Option values to keep if KeepOptions is true
        public string Resolution { get; set; } = "Original"; // Original, 1080p, 4K
        public bool FixJitter { get; set; } = false;
        public bool Stabilize { get; set; } = false;
        public int StabilizeSmoothing { get; set; } = 50;
        public bool Hevc { get; set; } = false;
        public bool NormalizeAudio { get; set; } = false;
        public bool DeepCheck { get; set; } = false;
        public bool MergeOutput { get; set; } = true;

        // Config lives in the per-user data dir; the install dir may be read-only (Program Files).
        private static readonly string ConfigPath = Path.Combine(AppPaths.DataDir, "config.json");
        // Older versions saved next to the exe.
        private static readonly string LegacyConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        public static AppConfig Load()
        {
            try
            {
                string path = File.Exists(ConfigPath) ? ConfigPath
                            : File.Exists(LegacyConfigPath) ? LegacyConfigPath
                            : "";
                if (path.Length > 0)
                {
                    string json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
            }
            catch { }
            return new AppConfig();
        }

        public void Save()
        {
            try
            {
                AppPaths.EnsureDataDir();
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch { }
        }
    }
}
