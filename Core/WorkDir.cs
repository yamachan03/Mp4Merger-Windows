using System;
using System.Diagnostics;
using System.IO;

namespace Mp4Merger.Core
{
    /// Every intermediate file of a run lives in one folder, so a crash leaves
    /// one directory to remove rather than scattered temp files.
    public static class WorkDir
    {
        public const string Prefix = "Mp4Merger-";

        /// Work folders carry the owning process ID, because more than one copy
        /// of the app can be running and one must never clear out another's
        /// folder mid-merge.
        public static string NameFor(int pid) => $"{Prefix}pid{pid}-{Guid.NewGuid():N}";

        public static int? OwnerOf(string name)
        {
            if (!name.StartsWith(Prefix + "pid", StringComparison.Ordinal)) return null;
            string rest = name.Substring(Prefix.Length + 3);
            int dash = rest.IndexOf('-');
            string digits = dash >= 0 ? rest.Substring(0, dash) : rest;
            return int.TryParse(digits, out int pid) ? pid : null;
        }

        /// Creates a fresh work folder for this process.
        public static DirectoryInfo Create()
        {
            string path = Path.Combine(Path.GetTempPath(), NameFor(Environment.ProcessId));
            return Directory.CreateDirectory(path);
        }

        /// Whether a work folder is garbage. Folders of a live process are kept;
        /// our own only when `includingOwn` (at quit). Folders from before owners
        /// were recorded are left alone unless they are a day old.
        public static bool IsStale(string name, int ownPid, bool includingOwn, TimeSpan age, Func<int, bool> isAlive)
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) return false;
            int? owner = OwnerOf(name);
            if (owner == null) return age > TimeSpan.FromDays(1);
            if (owner.Value == ownPid) return includingOwn;
            return !isAlive(owner.Value);
        }

        /// Removes work folders nobody is using. Called at launch for what a
        /// crash or force quit left behind, and at quit including our own.
        public static void RemoveStale(bool includingOwn)
        {
            string temp = Path.GetTempPath();
            string[] entries;
            try { entries = Directory.GetDirectories(temp, Prefix + "*"); }
            catch { return; }

            int ownPid = Environment.ProcessId;
            foreach (string path in entries)
            {
                string name = Path.GetFileName(path);
                TimeSpan age;
                try { age = DateTime.Now - Directory.GetLastWriteTime(path); }
                catch { continue; }

                if (IsStale(name, ownPid, includingOwn, age, IsProcessAlive))
                {
                    try { Directory.Delete(path, recursive: true); } catch { }
                }
            }
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false; // no such process
            }
            catch
            {
                // Access denied still means the process exists.
                return true;
            }
        }
    }
}
