using System;
using System.IO;
using System.IO.Compression;
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
        // API URL of the release that actually owns the downloaded asset (used for digest verification).
        private static readonly string RealEsrganPinnedApiUrl = "https://api.github.com/repos/xinntao/Real-ESRGAN/releases/tags/v0.2.5.0";

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
            ExtractFfmpeg(zipPath, AppPaths.EnsureDataDir());
            if (File.Exists(zipPath)) File.Delete(zipPath);

            if (!string.IsNullOrEmpty(latestDate))
            {
                File.WriteAllText(Path.Combine(AppPaths.EnsureDataDir(), "ffmpeg_date.txt"), latestDate);
            }
        }

        public static async Task DownloadAiAsync(Action<double, string> reportProgress)
        {
            using HttpClient apiClient = CreateClient(TimeSpan.FromSeconds(30));
            using HttpClient dlClient = CreateClient(TimeSpan.FromMinutes(30));

            string latestDate = await GetLatestDateAsync(apiClient, RealEsrganApiUrl);
            string? expectedHash = await GetAssetSha256Async(apiClient, RealEsrganPinnedApiUrl, Path.GetFileName(new Uri(RealEsrganUrl).LocalPath));

            reportProgress(50, "Downloading AI Upscaler...");
            string zipPath = Path.Combine(Path.GetTempPath(), "realesrgan_download.zip");
            await DownloadFileAsync(dlClient, RealEsrganUrl, zipPath, (p) => reportProgress(50 + (p * 0.5), $"Downloading AI Upscaler: {p:F1}%"));

            reportProgress(95, "Verifying AI Upscaler...");
            await VerifySha256Async(zipPath, expectedHash);

            reportProgress(100, "Extracting AI Upscaler...");
            ExtractRealEsrgan(zipPath, AppPaths.EnsureDataDir());
            if (File.Exists(zipPath)) File.Delete(zipPath);

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

        private static void ExtractFfmpeg(string zipPath, string targetDir)
        {
            string root = Path.GetFullPath(targetDir);
            if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) ||
                    entry.FullName.EndsWith("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                {
                    // Zip Slip guard: entry.Name can still contain backslash sequences on Windows
                    string destFile = Path.GetFullPath(Path.Combine(root, entry.Name));
                    if (!destFile.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

                    entry.ExtractToFile(destFile, true);
                }
            }
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
