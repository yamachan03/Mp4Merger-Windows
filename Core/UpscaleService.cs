using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mp4Merger.Core
{
    /// Frame-by-frame upscaling with Real-ESRGAN. This has no counterpart in the
    /// macOS build, which relies on ffmpeg alone.
    public class UpscaleService
    {
        /// Long enough for a tool with nothing to report: reading a file's
        /// details, or copying its audio out.
        private static readonly TimeSpan ShortToolLimit = TimeSpan.FromMinutes(2);

        private readonly string _ffmpegPath;
        private readonly string _realesrganPath;
        private readonly Func<double?, string> _describeStall;

        /// `describeStall` turns the position a run stopped at into the message
        /// the user sees, so the wording lives with the rest of the app's
        /// wording rather than here.
        public UpscaleService(
            string ffmpegPath,
            string realesrganPath,
            Func<double?, string>? describeStall = null)
        {
            _ffmpegPath = ffmpegPath;
            _realesrganPath = realesrganPath;
            _describeStall = describeStall ?? DescribeStallInEnglish;
        }

        private static string DescribeStallInEnglish(double? position) =>
            position is { } seconds
                ? "FFmpeg stopped responding " + TimeSpan.FromSeconds(seconds).ToString("c")
                    + " into the video."
                : "FFmpeg stopped responding after it started.";

        public async Task UpscaleVideoAsync(
            string inputVideo,
            string outputVideo,
            string modelName,
            bool keepOriginalResolution,
            Action<double, string> reportProgress,
            CancellationToken token)
        {
            var workDirectory = WorkDir.Create();
            string framesDir = Path.Combine(workDirectory.FullName, "frames");
            string upscaledDir = Path.Combine(workDirectory.FullName, "upscaled");
            string audioFile = Path.Combine(workDirectory.FullName, "audio.m4a");
            Directory.CreateDirectory(framesDir);
            Directory.CreateDirectory(upscaledDir);

            try
            {
                // 1. Source details. ffprobe reports these directly, so there is
                //    no need to parse them back out of ffmpeg's log.
                var info = await GetVideoInfoAsync(inputVideo, token).ConfigureAwait(false);
                string frameRate = info.FrameRate.Length > 0 ? info.FrameRate : "30";

                // 2. Audio, if the file has any. A video-only input is fine.
                reportProgress(0, "Extracting audio...");
                var audioResult = await ProcessRunner.CaptureAsync(_ffmpegPath, new[]
                {
                    "-i", inputVideo, "-vn", "-acodec", "copy", "-y", audioFile
                }, token, timeout: ShortToolLimit).ConfigureAwait(false);
                bool hasAudio = audioResult.ExitCode == 0 && File.Exists(audioFile) && new FileInfo(audioFile).Length > 0;

                // 3. Frames. %08d keeps the ordering correct past 99,999 frames.
                //    Decoding every frame of a long video takes as long as the
                //    upscale itself, so the bar has to move while it runs:
                //    ffmpeg's own status line already carries the position.
                reportProgress(5, "Extracting frames...");
                await RunGuardedAsync(_ffmpegPath, new[]
                {
                    "-i", inputVideo, "-y", Path.Combine(framesDir, "%08d.png")
                }, token, onStdErrLine: line =>
                {
                    if (FfmpegProgress.Percent(line, info.Duration, 0) is { } percent)
                    {
                        reportProgress(5 + percent * 0.05, "Extracting frames...");
                    }
                }).ConfigureAwait(false);

                int frameCount = Directory.GetFiles(framesDir, "*.png").Length;
                if (frameCount == 0) throw new InvalidOperationException("No frames could be extracted from the video.");

                // 4. Upscale. Real-ESRGAN gives no progress of its own, so the
                //    output folder is polled while it runs. That count is also
                //    the only sign of life it has, so it doubles as what the
                //    guard watches: frames written stand in for a position.
                using var upscaleDone = new CancellationTokenSource();
                using var guard = new StallGuard(token);
                var watcher = WatchProgressAsync(
                    upscaledDir, frameCount, reportProgress, guard, upscaleDone.Token);

                try
                {
                    // realesrgan resolves its default "models" folder relative to the
                    // working directory, so run it where the exe (and models) live.
                    await RunOrThrowAsync(_realesrganPath, new[]
                    {
                        "-i", framesDir, "-o", upscaledDir, "-n", modelName, "-s", "4", "-f", "png"
                    }, guard.Token, Path.GetDirectoryName(_realesrganPath)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (guard.Stalled)
                {
                    throw new InvalidOperationException(
                        "The AI upscaler stopped responding: no new frame was written for "
                        + $"{StallGuard.Limit.TotalMinutes:F0} minutes.");
                }
                finally
                {
                    upscaleDone.Cancel();
                    await watcher.ConfigureAwait(false);
                }

                // 5. Rebuild the video from the upscaled frames.
                reportProgress(90, "Merging frames and audio...");
                string tempOut = Path.Combine(workDirectory.FullName, $"upscaled_{Guid.NewGuid():N}.mp4");

                var args = new List<string> { "-framerate", frameRate, "-i", Path.Combine(upscaledDir, "%08d.png") };
                if (hasAudio) args.AddRange(new[] { "-i", audioFile, "-map", "0:v:0", "-map", "1:a:0" });
                if (keepOriginalResolution && info.Width > 0 && info.Height > 0)
                {
                    args.AddRange(new[] { "-vf", $"scale={info.Width}:{info.Height}:flags=lanczos" });
                }
                args.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p" });
                if (hasAudio) args.AddRange(new[] { "-c:a", "copy" });
                args.AddRange(new[] { "-r", frameRate, "-y", tempOut });

                await RunGuardedAsync(_ffmpegPath, args, token, onStdErrLine: line =>
                {
                    if (FfmpegProgress.Percent(line, info.Duration, 0) is { } percent)
                    {
                        reportProgress(90 + percent * 0.10, "Merging frames and audio...");
                    }
                }).ConfigureAwait(false);

                if (File.Exists(outputVideo)) File.Delete(outputVideo);
                File.Move(tempOut, outputVideo);
                reportProgress(100, "Upscaling complete.");
            }
            finally
            {
                try { workDirectory.Delete(recursive: true); } catch { }
            }
        }

        /// Reports how many frames Real-ESRGAN has written so far, and tells the
        /// guard about it: a frame appearing is the only sign of life it gives.
        private static async Task WatchProgressAsync(
            string upscaledDir, int total, Action<double, string> reportProgress,
            StallGuard guard, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                    int finished = Directory.Exists(upscaledDir)
                        ? Directory.GetFiles(upscaledDir, "*.png").Length
                        : 0;
                    guard.Advance(finished);
                    double percent = 10 + (double)finished / total * 80; // 10% to 90%
                    reportProgress(percent, $"Upscaling frames ({finished}/{total})...");
                    if (finished >= total) return;
                }
            }
            catch (OperationCanceledException) { /* the upscale finished */ }
        }

        private async Task<(double Duration, string FrameRate, int Width, int Height)> GetVideoInfoAsync(
            string file, CancellationToken token)
        {
            string ffprobePath = Path.Combine(
                Path.GetDirectoryName(_ffmpegPath) ?? "", "ffprobe.exe");
            if (!File.Exists(ffprobePath)) ffprobePath = AppPaths.ResolveTool("ffprobe");

            var result = await ProcessRunner.CaptureAsync(ffprobePath, MediaProbe.FfprobeArguments(file), token,
                timeout: ShortToolLimit).ConfigureAwait(false);
            var probe = MediaProbe.Parse(result.StdOut, file);

            return (
                probe?.Duration ?? 0,
                probe?.Video?.FrameRate ?? "30",
                probe?.Video?.Width ?? 0,
                probe?.Video?.Height ?? 0);
        }

        /// Runs a long ffmpeg, cutting it off if it stops getting anywhere.
        /// Both of the ffmpeg runs here take as long as the upscale itself on a
        /// long video, so neither can be left unwatched.
        private async Task RunGuardedAsync(
            string executable, IEnumerable<string> arguments, CancellationToken token,
            Action<string>? onStdErrLine = null, string? workingDirectory = null)
        {
            using var guard = new StallGuard(token);

            ProcessResult result;
            try
            {
                result = await ProcessRunner.CaptureAsync(executable, arguments, guard.Token,
                    onStdErrLine: line =>
                    {
                        guard.Advance(FfmpegProgress.PositionSeconds(line));
                        onStdErrLine?.Invoke(line);
                    },
                    workingDirectory: workingDirectory).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (guard.Stalled)
            {
                throw new InvalidOperationException(_describeStall(guard.Position));
            }

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{Path.GetFileName(executable)} failed with code {result.ExitCode}.\n{result.StdErr}");
            }
        }

        /// Runs a tool that reports nothing of its own. Its one caller passes a
        /// guarded token, so the run is still cut off if it stops producing
        /// frames; there is just nothing here to read for that.
        private static async Task RunOrThrowAsync(
            string executable, IEnumerable<string> arguments, CancellationToken token,
            string? workingDirectory = null, Action<string>? onStdErrLine = null)
        {
            var result = await ProcessRunner.CaptureAsync(executable, arguments, token,
                onStdErrLine: onStdErrLine, workingDirectory: workingDirectory).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{Path.GetFileName(executable)} failed with code {result.ExitCode}.\n{result.StdErr}");
            }
        }
    }
}
