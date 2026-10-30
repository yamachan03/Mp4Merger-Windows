using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mp4Merger.Core
{
    // How hard we make ffmpeg work. The decisive difference is whether the decoder
    // runs at all: `-c copy` never decodes, so it sails past bitstream damage that a
    // full decode catches immediately.
    public enum CheckLevel { Quick, Standard, Full }

    public static class CheckLevelExtensions
    {
        public static string TitleKey(this CheckLevel level) => level switch
        {
            CheckLevel.Quick => "CheckLevelQuick",
            CheckLevel.Standard => "CheckLevelStandard",
            _ => "CheckLevelFull"
        };

        public static string DetailKey(this CheckLevel level) => level switch
        {
            CheckLevel.Quick => "CheckLevelQuickDetail",
            CheckLevel.Standard => "CheckLevelStandardDetail",
            _ => "CheckLevelFullDetail"
        };

        // Wall-clock seconds spent per second of footage, against 1080p30 H.264.
        // Estimate() scales this by pixel count.
        public static double RealtimeFactor(this CheckLevel level) => level switch
        {
            CheckLevel.Quick => 0.003,
            CheckLevel.Standard => 0.015,
            _ => 0.048
        };

        // Software decoding only: a hardware decode session gives no frame-level
        // parallelism, so hardware is the wrong tool for verification even though
        // it is the right one for encoding.
        public static List<string> Arguments(this CheckLevel level, string path)
        {
            var args = new List<string> { "-v", "warning", "-nostats", "-progress", "pipe:1" };
            switch (level)
            {
                case CheckLevel.Quick:
                    args.AddRange(new[] { "-i", path, "-c", "copy" });
                    break;
                case CheckLevel.Standard:
                    args.AddRange(new[] { "-skip_frame", "nokey", "-i", path, "-an" });
                    break;
                default:
                    args.AddRange(new[] { "-i", path });
                    break;
            }
            args.AddRange(new[] { "-f", "null", "-" });
            return args;
        }
    }

    public enum CheckStatus { Ok, Warning, Broken }

    public sealed class CheckResult
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Path { get; init; } = "";
        public string FileName => System.IO.Path.GetFileName(Path);
        public CheckStatus Status { get; init; }
        public string Log { get; init; } = "";
        public double Duration { get; init; }
        public TimeSpan Elapsed { get; init; }
        public bool IsProblem => Status != CheckStatus.Ok;
    }

    public sealed class CheckTarget
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Path { get; init; } = "";
        public double Duration { get; init; }
        /// Relative to 1920x1080, so a 4K file costs roughly 4x the time.
        public double PixelScale { get; init; } = 1;
        public double EstimatedCost => Duration * PixelScale;
    }

    public sealed class VideoIntegrityChecker
    {
        private readonly string _ffmpegPath;
        private readonly string _ffprobePath;

        public VideoIntegrityChecker(string ffmpegPath, string ffprobePath)
        {
            _ffmpegPath = ffmpegPath;
            _ffprobePath = ffprobePath;
        }

        // A single full decode already spreads across several cores, so more
        // workers mostly contend. One per four cores, never more than four.
        public static int Concurrency => Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

        public static readonly HashSet<string> VideoExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v" };

        // MARK: - Discovery

        /// Expands dropped/selected items into the video files underneath them.
        public static List<string> CollectVideos(IEnumerable<string> paths, bool recursive)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Append(string path)
            {
                if (!VideoExtensions.Contains(Path.GetExtension(path))) return;
                string full;
                try { full = Path.GetFullPath(path); } catch { return; }
                if (!seen.Add(full)) return;
                found.Add(full);
            }

            foreach (string path in paths)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                        foreach (string child in SafeEnumerate(path, option)) Append(child);
                    }
                    else if (File.Exists(path))
                    {
                        Append(path);
                    }
                }
                catch { /* unreadable item, skip */ }
            }

            found.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
            return found;
        }

        /// Enumeration that survives a folder it cannot open partway through.
        private static IEnumerable<string> SafeEnumerate(string root, SearchOption option)
        {
            var pending = new Queue<string>();
            pending.Enqueue(root);

            while (pending.Count > 0)
            {
                string dir = pending.Dequeue();
                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { continue; }
                foreach (string file in files) yield return file;

                if (option != SearchOption.AllDirectories) continue;
                string[] subdirs;
                try { subdirs = Directory.GetDirectories(dir); }
                catch { continue; }
                foreach (string sub in subdirs)
                {
                    var info = new DirectoryInfo(sub);
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.ReparsePoint)) != 0) continue;
                    pending.Enqueue(sub);
                }
            }
        }

        /// Reads duration and frame size so the UI can estimate how long a run
        /// takes. A file damaged badly enough to defeat ffprobe still becomes a
        /// target — it simply carries no estimate.
        public async Task<List<CheckTarget>> InspectAsync(IReadOnlyList<string> paths, CancellationToken token)
        {
            var targets = new List<CheckTarget>();
            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested();
                var info = await ProbeAsync(path, token).ConfigureAwait(false);
                double scale = info.Width > 0 && info.Height > 0
                    ? (double)info.Width * info.Height / (1920.0 * 1080.0)
                    : 1.0;
                targets.Add(new CheckTarget { Path = path, Duration = info.Duration, PixelScale = scale });
            }
            return targets;
        }

        private async Task<(double Duration, int Width, int Height)> ProbeAsync(string path, CancellationToken token)
        {
            ProcessResult result;
            try
            {
                result = await ProcessRunner.CaptureAsync(_ffprobePath, new[]
                {
                    "-v", "error",
                    "-select_streams", "v:0",
                    "-show_entries", "stream=width,height",
                    "-show_entries", "format=duration",
                    "-of", "json",
                    path
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { return (0, 0, 0); }

            try
            {
                using var doc = JsonDocument.Parse(result.StdOut);
                var root = doc.RootElement;
                double duration = 0;
                if (root.TryGetProperty("format", out var format) &&
                    format.TryGetProperty("duration", out var d) &&
                    double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                {
                    duration = parsed;
                }

                int width = 0, height = 0;
                if (root.TryGetProperty("streams", out var streams) &&
                    streams.ValueKind == JsonValueKind.Array &&
                    streams.GetArrayLength() > 0)
                {
                    var stream = streams[0];
                    if (stream.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number) width = w.GetInt32();
                    if (stream.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number) height = h.GetInt32();
                }
                return (duration, width, height);
            }
            catch
            {
                return (0, 0, 0);
            }
        }

        /// Predicted wall-clock time for a run, accounting for resolution and
        /// the workers that share the machine.
        public static TimeSpan Estimate(IReadOnlyList<CheckTarget> targets, CheckLevel level)
        {
            double cost = targets.Sum(t => t.EstimatedCost);
            // A lone file gets no benefit from the extra workers.
            int workers = Math.Max(Math.Min(Concurrency, targets.Count), 1);
            return TimeSpan.FromSeconds(cost * level.RealtimeFactor() / workers);
        }

        // MARK: - Checking

        /// Runs one file. `onProgress` reports seconds of footage decoded so far.
        /// stdout carries `-progress` key/value lines, stderr carries only the
        /// diagnostics we judge on. Keeping them apart means a noisy progress
        /// stream can never be mistaken for damage.
        public async Task<CheckResult> CheckAsync(
            CheckTarget target,
            CheckLevel level,
            Action<double> onProgress,
            CancellationToken token)
        {
            var startedAt = DateTime.UtcNow;

            void HandleStdOut(string line)
            {
                // `out_time_ms` is microseconds despite the name.
                if (!line.StartsWith("out_time_ms=", StringComparison.Ordinal)) return;
                if (double.TryParse(line.AsSpan("out_time_ms=".Length), NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out double micros))
                {
                    onProgress(Math.Max(micros / 1_000_000.0, 0));
                }
            }

            var result = await ProcessRunner.CaptureAsync(
                _ffmpegPath,
                level.Arguments(target.Path),
                token,
                onStdErrLine: null,
                onStdOutLine: HandleStdOut,
                stdErrLimit: 64_000).ConfigureAwait(false);

            string findings = MeaningfulDiagnostics(result.StdErr);
            return new CheckResult
            {
                Path = target.Path,
                Status = Classify(findings),
                Log = findings,
                Duration = target.Duration,
                Elapsed = DateTime.UtcNow - startedAt
            };
        }

        /// Drops complaints that come from the discard muxer rather than the input.
        /// A healthy file routinely makes `-f null` grumble about timestamps, and
        /// reporting that as damage would flag good footage.
        public static string MeaningfulDiagnostics(string raw)
        {
            string[] benign =
            {
                "non monotonically increasing dts to muxer",
                "encoder did not produce proper pts",
                "could not update timestamps for skipped samples"
            };

            var kept = raw
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .Where(line =>
                {
                    string lower = line.ToLowerInvariant();
                    // Everything the null muxer says is about our own throwaway output.
                    if (lower.Contains("[null @ ")) return false;
                    return !benign.Any(lower.Contains);
                });

            return string.Join("\n", kept).Trim();
        }

        /// ffmpeg exits 0 even on badly damaged input unless `-xerror` is set, and
        /// we deliberately let it run to the end so every damaged region is
        /// reported. That makes stderr — not the exit status — the only usable signal.
        public static CheckStatus Classify(string diagnostics)
        {
            string text = diagnostics.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(text)) return CheckStatus.Ok;

            string[] fatal =
            {
                "invalid nal", "error splitting", "partial file", "invalid data found",
                "moov atom not found", "could not find codec parameters", "error while decoding",
                "decoding error", "no such file", "invalid argument", "packet corrupt"
            };
            if (fatal.Any(text.Contains)) return CheckStatus.Broken;

            // `corrupt decoded frame` is logged at warning level, which is exactly
            // why a `-v error` check reports a damaged file as clean.
            return CheckStatus.Warning;
        }
    }
}
