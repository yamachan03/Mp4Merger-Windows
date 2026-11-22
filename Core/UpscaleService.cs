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
        /// Reading a file's details reports no progress, so it gets a plain
        /// limit. Generous, for a file still coming down from cloud sync.
        private static readonly TimeSpan ProbeLimit = TimeSpan.FromMinutes(2);

        /// How long Real-ESRGAN may go without finishing a frame. Far longer
        /// than the default: what it is given is a frame of the source at its own
        /// size, upscaled four times, so one frame of a 4K source is 15360x8640
        /// and a weak GPU can spend minutes on it -- the first one longest, with
        /// the model and Vulkan still loading. Cutting it off wrongly throws away
        /// every frame extracted so far, which can be hours of work, while
        /// noticing a real hang late only costs the wait.
        private static readonly TimeSpan UpscalerAllowance = TimeSpan.FromMinutes(20);

        private readonly string _ffmpegPath;
        private readonly string _realesrganPath;
        private readonly Func<double?, string> _describeStall;
        private readonly Func<TimeSpan, string> _describeUpscalerStall;

        /// The two describers turn a cut-off run into the message the user sees,
        /// so the wording lives with the rest of the app's wording rather than
        /// here: `describeStall` for ffmpeg, which reports a position, and
        /// `describeUpscalerStall` for Real-ESRGAN, which only produces frames
        /// and so can report nothing but how long it was given.
        public UpscaleService(
            string ffmpegPath,
            string realesrganPath,
            Func<double?, string>? describeStall = null,
            Func<TimeSpan, string>? describeUpscalerStall = null)
        {
            _ffmpegPath = ffmpegPath;
            _realesrganPath = realesrganPath;
            _describeStall = describeStall ?? DescribeStallInEnglish;
            _describeUpscalerStall = describeUpscalerStall ?? DescribeUpscalerStallInEnglish;
        }

        private static string DescribeUpscalerStallInEnglish(TimeSpan allowance) =>
            $"The AI upscaler stopped responding: no new frame was written for "
            + $"{allowance.TotalMinutes:F0} minutes.";

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
                // Copying the audio out still reads the whole input, so this is
                // not a "short" call; it does report a position, so it is guarded
                // like the other long ones.
                reportProgress(0, "Extracting audio...");
                bool hasAudio = await ExtractAudioAsync(inputVideo, audioFile, token).ConfigureAwait(false);

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
                using (var upscaleDone = new CancellationTokenSource())
                using (var guard = new StallGuard(
                    token,
                    (int)(UpscalerAllowance / StallGuard.CheckEvery),
                    StallGuard.CheckEvery))
                {
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
                        throw new InvalidOperationException(_describeUpscalerStall(guard.Allowance));
                    }
                    finally
                    {
                        upscaleDone.Cancel();
                        await watcher.ConfigureAwait(false);
                    }
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

                    // Deliberately not returning once the count is reached.
                    // Directory.GetFiles counts a file still being written, so the
                    // count fills up slightly before the work is done; stopping
                    // here would leave the guard with nothing to feed it and it
                    // would fire on a run that was simply finishing off.
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
                timeout: ProbeLimit).ConfigureAwait(false);
            var probe = MediaProbe.Parse(result.StdOut, file);

            return (
                probe?.Duration ?? 0,
                probe?.Video?.FrameRate ?? "30",
                probe?.Video?.Width ?? 0,
                probe?.Video?.Height ?? 0);
        }

        /// Copies the audio out, and re-encodes it if it cannot be copied.
        ///
        /// Copying only works for a codec an .m4a can hold. The input used to be
        /// a file this app had just written, so it was always AAC; now that the
        /// filter pass is skipped when it has nothing to do, whatever the user
        /// brought arrives here. A copy that fails used to mean "no audio", so
        /// the track was dropped without a word.
        private async Task<bool> ExtractAudioAsync(string input, string audioFile, CancellationToken token)
        {
            foreach (string[] codec in new[]
            {
                new[] { "-acodec", "copy" },
                new[] { "-c:a", "aac", "-b:a", "192k" },
            })
            {
                try
                {
                    var args = new List<string> { "-i", input, "-vn" };
                    args.AddRange(codec);
                    args.AddRange(new[] { "-y", audioFile });

                    await RunGuardedAsync(_ffmpegPath, args, token).ConfigureAwait(false);
                    if (File.Exists(audioFile) && new FileInfo(audioFile).Length > 0) return true;
                }
                catch (InvalidOperationException)
                {
                    // Try the next way; a video-only input fails both, which is fine.
                }
            }
            return false;
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
