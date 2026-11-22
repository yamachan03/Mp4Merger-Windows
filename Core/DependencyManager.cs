using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Text.Json;

namespace Mp4Merger.Core
{
    public static class DependencyManager
    {
        private static readonly string FfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
        private static readonly string RealEsrganUrl = "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip";

        private static readonly string FfmpegApiUrl = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest";
        private static readonly string RealEsrganApiUrl = "https://api.github.com/repos/xinntao/Real-ESRGAN/releases/latest";

        // RealEsrganUrl points at an immutable 2022 asset that predates GitHub's per-asset
        // digests (the API returns null for it), so verification uses this pinned SHA-256
        // measured from the official release. Update this constant if RealEsrganUrl changes.
        private static readonly string RealEsrganZipSha256 = "abc02804e17982a3be33675e4d471e91ea374e65b70167abc09e31acb412802d";

        public static bool IsFfmpegMissing()
        {
            return !File.Exists(AppPaths.ResolveTool("ffmpeg"));
        }

        public static bool IsAiMissing()
        {
            return !File.Exists(AppPaths.ResolveTool("realesrgan-ncnn-vulkan"));
        }

        private static HttpClient CreateClient(TimeSpan timeout)
        {
            var client = new HttpClient { Timeout = timeout };
            client.DefaultRequestHeaders.Add("User-Agent", "Mp4Merger-AutoDownloader");
            return client;
        }

        public static async Task<bool> IsUpdateAvailableAsync(string toolName)
        {
            string apiUrl = toolName == "ffmpeg" ? FfmpegApiUrl : RealEsrganApiUrl;
            try
            {
                using var client = CreateClient(TimeSpan.FromSeconds(30));
                var response = await client.GetStringAsync(apiUrl);
                using var doc = JsonDocument.Parse(response);
                string latestDate = doc.RootElement.GetProperty("published_at").GetString() ?? "";

                string? installedFile = FindExistingDataFile($"{toolName}_date.txt");
                if (installedFile == null) return true;

                string installedDate = File.ReadAllText(installedFile).Trim();
                return installedDate != latestDate && !string.IsNullOrEmpty(latestDate);
            }
            catch
            {
                return false;
            }
        }

        // Date markers may still exist next to the exe from older versions.
        private static string? FindExistingDataFile(string fileName)
        {
            string inData = Path.Combine(AppPaths.DataDir, fileName);
            if (File.Exists(inData)) return inData;
            string inApp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            if (File.Exists(inApp)) return inApp;
            return null;
        }

        private static async Task<string> GetLatestDateAsync(HttpClient client, string apiUrl)
        {
            try {
                var response = await client.GetStringAsync(apiUrl);
                using var doc = JsonDocument.Parse(response);
                return doc.RootElement.GetProperty("published_at").GetString() ?? "";
            } catch { return ""; }
        }

        // Reads the "digest" (sha256:<hex>) the GitHub Releases API publishes per asset.
        // Returns null when unavailable (older releases have no digest).
        private static async Task<string?> GetAssetSha256Async(HttpClient client, string apiUrl, string assetFileName)
        {
            try
            {
                var response = await client.GetStringAsync(apiUrl);
                using var doc = JsonDocument.Parse(response);
                if (!doc.RootElement.TryGetProperty("assets", out var assets)) return null;

                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var nameVal) &&
                        string.Equals(nameVal.GetString(), assetFileName, StringComparison.OrdinalIgnoreCase) &&
                        asset.TryGetProperty("digest", out var digestVal) &&
                        digestVal.ValueKind == JsonValueKind.String)
                    {
                        string digest = digestVal.GetString() ?? "";
                        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        {
                            return digest.Substring("sha256:".Length);
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static async Task VerifySha256Async(string filePath, string? expectedSha256)
        {
            if (string.IsNullOrEmpty(expectedSha256)) return; // no published digest to check against

            using var sha = SHA256.Create();
            await using var fs = File.OpenRead(filePath);
            byte[] hash = await sha.ComputeHashAsync(fs);
            string actual = Convert.ToHexString(hash);

            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(filePath); } catch { }
                throw new InvalidOperationException(
                    "Downloaded file failed the SHA-256 integrity check and was deleted. " +
                    "Please try again later. (expected " + expectedSha256 + ", got " + actual + ")");
            }
        }

        public static async Task DownloadFfmpegAsync(Action<double, string> reportProgress)
        {
            using HttpClient apiClient = CreateClient(TimeSpan.FromSeconds(30));
            using HttpClient dlClient = CreateClient(TimeSpan.FromMinutes(30));

            string latestDate = await GetLatestDateAsync(apiClient, FfmpegApiUrl);
            string? expectedHash = await GetAssetSha256Async(apiClient, FfmpegApiUrl, Path.GetFileName(new Uri(FfmpegUrl).LocalPath));

            reportProgress(0, "Downloading FFmpeg...");
            string zipPath = Path.Combine(Path.GetTempPath(), "ffmpeg_download.zip");
            await DownloadFileAsync(dlClient, FfmpegUrl, zipPath, (p) => reportProgress(p * 0.5, $"Downloading FFmpeg: {p:F1}%"));

            reportProgress(50, "Verifying FFmpeg...");
            await VerifySha256Async(zipPath, expectedHash);

            reportProgress(55, "Extracting FFmpeg...");
            try
            {
                ExtractFfmpeg(zipPath, AppPaths.EnsureDataDir());
            }
            finally
            {
                // A failed extract used to leave the 200MB download behind,
                // because the delete sat after the call that threw.
                TryDelete(zipPath);
            }

            if (!string.IsNullOrEmpty(latestDate))
            {
                File.WriteAllText(Path.Combine(AppPaths.EnsureDataDir(), "ffmpeg_date.txt"), latestDate);
            }

            await RecordFfmpegVersionAsync().ConfigureAwait(false);
        }

        public static async Task DownloadAiAsync(Action<double, string> reportProgress)
        {
            using HttpClient apiClient = CreateClient(TimeSpan.FromSeconds(30));
            using HttpClient dlClient = CreateClient(TimeSpan.FromMinutes(30));

            string latestDate = await GetLatestDateAsync(apiClient, RealEsrganApiUrl);

            reportProgress(50, "Downloading AI Upscaler...");
            string zipPath = Path.Combine(Path.GetTempPath(), "realesrgan_download.zip");
            await DownloadFileAsync(dlClient, RealEsrganUrl, zipPath, (p) => reportProgress(50 + (p * 0.5), $"Downloading AI Upscaler: {p:F1}%"));

            reportProgress(95, "Verifying AI Upscaler...");
            await VerifySha256Async(zipPath, RealEsrganZipSha256);

            reportProgress(100, "Extracting AI Upscaler...");
            try
            {
                ExtractRealEsrgan(zipPath, AppPaths.EnsureDataDir());
            }
            finally
            {
                TryDelete(zipPath);
            }

            if (!string.IsNullOrEmpty(latestDate))
            {
                File.WriteAllText(Path.Combine(AppPaths.EnsureDataDir(), "realesrgan_date.txt"), latestDate);
            }
        }

        private static async Task DownloadFileAsync(HttpClient client, string url, string destination, Action<double> progress)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalBytesRead = 0;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                totalBytesRead += bytesRead;
                if (contentLength.HasValue)
                {
                    progress((double)totalBytesRead / contentLength.Value * 100);
                }
            }
        }

        /// The tools taken out of the FFmpeg zip. They are a matched pair: ffprobe
        /// reads the formats ffmpeg writes, so one must never be newer than the other.
        private static readonly string[] FfmpegTools = { "ffmpeg.exe", "ffprobe.exe" };

        /// Both tools, unpacked beside the installed ones and only then swapped in.
        ///
        /// Extracting straight over the old files looked simpler but left the pair
        /// mismatched: the zip holds ffprobe before ffmpeg, Windows refuses to
        /// overwrite an exe that is running, and a merge in progress holds
        /// ffmpeg.exe open. So ffprobe was replaced, ffmpeg threw, and the install
        /// silently ended up with two different builds while the date file still
        /// said the old one (2026-11-22, seen on this machine). Staging first means
        /// either both land or neither does.
        private static void ExtractFfmpeg(string zipPath, string targetDir)
        {
            string root = Path.GetFullPath(targetDir);
            if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

            var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (ZipArchive archive = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        string name = Path.GetFileName(entry.FullName);
                        if (!FfmpegTools.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                        if (staged.ContainsKey(name)) continue;

                        // Zip Slip guard: the entry's path can still try to climb out.
                        string destFile = Path.GetFullPath(Path.Combine(root, name));
                        if (!destFile.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

                        string stagedFile = destFile + StagedSuffix;
                        entry.ExtractToFile(stagedFile, true);
                        staged[name] = stagedFile;
                    }
                }

                var missing = FfmpegTools.Where(tool => !staged.ContainsKey(tool)).ToList();
                if (missing.Count > 0)
                {
                    // Nothing is swapped in, so the working install is untouched.
                    throw new InvalidOperationException(
                        "The FFmpeg download did not contain " + string.Join(" and ", missing) +
                        ". Nothing was replaced.");
                }

                SwapIntoPlace(staged);
            }
            finally
            {
                // Anything still staged is left over from a failure.
                foreach (string file in staged.Values) TryDelete(file);
            }
        }

        private const string StagedSuffix = ".new";
        private const string ReplacedSuffix = ".old";

        /// Puts the staged tools in place together. A tool that is running cannot
        /// be deleted or overwritten, but it can be renamed: the copy already in
        /// use keeps running under the new name, and the leftover is cleared away
        /// the next time an update runs. A swap that fails part way puts back
        /// everything it moved, so the two tools never stay on different builds.
        private static void SwapIntoPlace(IReadOnlyDictionary<string, string> staged)
        {
            var movedAside = new List<(string Live, string Aside)>();
            var swappedIn = new List<string>();

            try
            {
                foreach (var pair in staged)
                {
                    string stagedFile = pair.Value;
                    string live = stagedFile.Substring(0, stagedFile.Length - StagedSuffix.Length);

                    if (File.Exists(live))
                    {
                        string aside = live + ReplacedSuffix;
                        TryDelete(aside);
                        File.Move(live, aside);
                        movedAside.Add((live, aside));
                    }

                    File.Move(stagedFile, live);
                    swappedIn.Add(live);
                }
            }
            catch
            {
                foreach (string live in swappedIn)
                {
                    try { File.Move(live, live + StagedSuffix, true); } catch { }
                }
                foreach (var moved in movedAside)
                {
                    try { File.Move(moved.Aside, moved.Live, true); } catch { }
                }
                throw;
            }

            // Best effort: one still running cannot go until a later update.
            foreach (var moved in movedAside) TryDelete(moved.Aside);
        }

        /// Writes down which build was just installed. The release tag's contents
        /// are replaced in place, so this file is the only record of what the
        /// user is actually running once the tag has moved on. Best effort: a
        /// failure here must not fail an install that otherwise worked.
        private static async Task RecordFfmpegVersionAsync()
        {
            try
            {
                string ffmpeg = AppPaths.ResolveTool("ffmpeg");
                if (!File.Exists(ffmpeg)) return;

                var result = await ProcessRunner.CaptureAsync(
                    ffmpeg, new[] { "-version" }, CancellationToken.None,
                    timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);

                string? banner = FfmpegVersion.FromBanner(result.StdOut)
                                 ?? FfmpegVersion.FromBanner(result.StdErr);
                if (banner != null)
                {
                    File.WriteAllText(
                        Path.Combine(AppPaths.EnsureDataDir(), "ffmpeg_version.txt"), banner);
                }
            }
            catch { /* the install stands with or without this note */ }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void ExtractRealEsrgan(string zipPath, string targetDir)
        {
            string root = Path.GetFullPath(targetDir);
            if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue; // skip directories

                // Zip Slip guard: reject entries that resolve outside the target directory
                string destPath = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!destPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

                string? dir = Path.GetDirectoryName(destPath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                entry.ExtractToFile(destPath, true);
            }
        }
    }
}
