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
        private readonly string _ffmpegPath;
        private readonly string _realesrganPath;

        public UpscaleService(string ffmpegPath, string realesrganPath)
        {
            _ffmpegPath = ffmpegPath;
            _realesrganPath = realesrganPath;
        }

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
                }, token).ConfigureAwait(false);
                bool hasAudio = audioResult.ExitCode == 0 && File.Exists(audioFile) && new FileInfo(audioFile).Length > 0;

                // 3. Frames. %08d keeps the ordering correct past 99,999 frames.
                reportProgress(5, "Extracting frames...");
                await RunOrThrowAsync(_ffmpegPath, new[]
                {
                    "-i", inputVideo, "-y", Path.Combine(framesDir, "%08d.png")
                }, token).ConfigureAwait(false);

                int frameCount = Directory.GetFiles(framesDir, "*.png").Length;
                if (frameCount == 0) throw new InvalidOperationException("No frames could be extracted from the video.");

                // 4. Upscale. Real-ESRGAN gives no progress of its own, so the
                //    output folder is polled while it runs.
                using var upscaleDone = new CancellationTokenSource();
                var watcher = WatchProgressAsync(upscaledDir, frameCount, reportProgress, upscaleDone.Token);

                try
                {
                    // realesrgan resolves its default "models" folder relative to the
                    // working directory, so run it where the exe (and models) live.
                    await RunOrThrowAsync(_realesrganPath, new[]
                    {
                        "-i", framesDir, "-o", upscaledDir, "-n", modelName, "-s", "4", "-f", "png"
                    }, token, Path.GetDirectoryName(_realesrganPath)).ConfigureAwait(false);
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

                await RunOrThrowAsync(_ffmpegPath, args, token).ConfigureAwait(false);

                if (File.Exists(outputVideo)) File.Delete(outputVideo);
                File.Move(tempOut, outputVideo);
                reportProgress(100, "Upscaling complete.");
            }
            finally
            {
                try { workDirectory.Delete(recursive: true); } catch { }
            }
        }

        /// Reports how many frames Real-ESRGAN has written so far.
        private static async Task WatchProgressAsync(
            string upscaledDir, int total, Action<double, string> reportProgress, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                    int finished = Directory.Exists(upscaledDir)
                        ? Directory.GetFiles(upscaledDir, "*.png").Length
                        : 0;
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

            var result = await ProcessRunner.CaptureAsync(ffprobePath, MediaProbe.FfprobeArguments(file), token)
                .ConfigureAwait(false);
            var probe = MediaProbe.Parse(result.StdOut, file);

            return (
                probe?.Duration ?? 0,
                probe?.Video?.FrameRate ?? "30",
                probe?.Video?.Width ?? 0,
                probe?.Video?.Height ?? 0);
        }

        private static async Task RunOrThrowAsync(
            string executable, IEnumerable<string> arguments, CancellationToken token, string? workingDirectory = null)
        {
            var result = await ProcessRunner.CaptureAsync(executable, arguments, token,
                workingDirectory: workingDirectory).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{Path.GetFileName(executable)} failed with code {result.ExitCode}.\n{result.StdErr}");
            }
        }
    }
}
