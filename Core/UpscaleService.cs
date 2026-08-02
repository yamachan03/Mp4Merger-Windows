using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Linq;

namespace Mp4Merger.Core
{
    public class UpscaleService
    {
        private string _ffmpegPath;
        private string _realesrganPath;

        public UpscaleService(string ffmpegPath, string realesrganPath)
        {
            _ffmpegPath = ffmpegPath;
            _realesrganPath = realesrganPath;
        }

        public async Task UpscaleVideoAsync(string inputVideo, string outputVideo, string modelName, bool keepOriginalResolution, Action<double, string> reportProgress)
        {
            // 1. Setup Directories
            string tempDir = Path.Combine(Path.GetTempPath(), "Mp4Merger_Upscale_" + Guid.NewGuid());
            string framesDir = Path.Combine(tempDir, "frames");
            string upscaledDir = Path.Combine(tempDir, "upscaled_frames");
            string audioFile = Path.Combine(tempDir, "audio.m4a");

            Directory.CreateDirectory(framesDir);
            Directory.CreateDirectory(upscaledDir);

            try
            {
                // 2. Get Video Info (Duration & FrameRate)
                // 2. Get Video Info (Duration & FrameRate & Resolution)
                var (durationSecs, fps, width, height) = await GetVideoInfoAsync(inputVideo);
                int totalFramesEstimate = (int)(durationSecs * fps);

                // 3. Extract Audio
                reportProgress(0, "Extracting audio...");
                await RunProcessAsync(_ffmpegPath, $"-i \"{inputVideo}\" -vn -acodec copy \"{audioFile}\"");

                // 4. Extract Frames
                reportProgress(5, "Extracting frames...");
                // %08d.png ensures we can handle many frames. 
                await RunProcessAsync(_ffmpegPath, $"-i \"{inputVideo}\" \"{Path.Combine(framesDir, "%08d.png")}\"");

                // 5. Upscale Frames
                // Monitor the upscaledDir directory to report progress
                int framesToUpscale = Directory.GetFiles(framesDir, "*.png").Length;
                
                var progressTask = Task.Run(async () =>
                {
                    while (true)
                    {
                        await Task.Delay(1000);
                        if (!Directory.Exists(upscaledDir)) break;
                        int finished = Directory.GetFiles(upscaledDir, "*.png").Length;
                        
                        double pct = 10 + (double)finished / framesToUpscale * 80; // 10% to 90%
                        reportProgress(pct, $"Upscaling frames ({finished}/{framesToUpscale})...");

                        if (finished >= framesToUpscale) break;
                    }
                });

                // Run Real-ESRGAN
                // -i inputs -o outputs -n modelname -s scale
                // scale is usually 4 by default for these models.
                string args = $"-i \"{framesDir}\" -o \"{upscaledDir}\" -n {modelName} -s 4 -f png";
                // realesrgan resolves its default "models" folder relative to the working
                // directory, so run it from the directory the exe (and models) live in.
                await RunProcessAsync(_realesrganPath, args, Path.GetDirectoryName(_realesrganPath));
                
                // Wait for progress loop (it should finish shortly after process exits)
                // await progressTask; // actually we can likely just stop it or let it run out. 
                // The loop breaks when count matches, but process might exit before filesystem updates fully or vice-versa.
                
                // 6. Merge Frames & Audio
                reportProgress(90, "Merging frames and audio...");
                
                // Construct video from images
                // -r fps set framerate
                // Construct video from images
                // -r fps set framerate
                string vf = "";
                if (keepOriginalResolution && width > 0 && height > 0)
                {
                    vf = $"-vf \"scale={width}:{height}:flags=lanczos\" ";
                }

                string tempOut = Path.Combine(Path.GetTempPath(), "upscaletmp_" + Guid.NewGuid() + ".mp4");

                string mergeArgs = $"-r {fps} -i \"{Path.Combine(upscaledDir, "%08d.png")}\" -i \"{audioFile}\" -map 0:v:0 -map 1:a:0 {vf}-c:v libx264 -pix_fmt yuv420p -c:a copy \"{tempOut}\"";
                
                // If audio extraction failed (no audio stream), we might need to handle that. 
                if (!File.Exists(audioFile))
                {
                    mergeArgs = $"-r {fps} -i \"{Path.Combine(upscaledDir, "%08d.png")}\" {vf}-c:v libx264 -pix_fmt yuv420p \"{tempOut}\"";
                }

                await RunProcessAsync(_ffmpegPath, mergeArgs, null);

                if (File.Exists(tempOut))
                {
                    if (File.Exists(outputVideo)) File.Delete(outputVideo);
                    File.Move(tempOut, outputVideo);
                }

                reportProgress(100, "Upscaling Complete!");

            }
            finally
            {
                // Cleanup
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { /* Ignore cleanup errors */ }
                }
            }
        }

        private async Task<(double duration, double fps, int width, int height)> GetVideoInfoAsync(string file)
        {
            // Use ffmpeg/ffprobe to get info. For simplicity, just use FFprobe if available or parse ffmpeg output.
            // Since we have ffmpeg path, let's use ffmpeg -i and parse stderr.
            
            string workDir = Path.GetDirectoryName(file) ?? "";
            string shortName = Path.GetFileName(file);

            Process p = new Process();
            if (!string.IsNullOrEmpty(workDir)) p.StartInfo.WorkingDirectory = workDir;
            p.StartInfo.FileName = _ffmpegPath;
            p.StartInfo.Arguments = $"-i \"{shortName}\"";
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.CreateNoWindow = true;
            p.Start();
            string output = await p.StandardError.ReadToEndAsync();
            p.WaitForExit();

            // Duration: 00:00:05.12, start: ...
            // Stream #0:0(...): Video: ..., 30 fps, ...
            
            double duration = 0;
            double fps = 30; // default
            int width = 0;
            int height = 0;

            var durMatch = Regex.Match(output, @"Duration: (\d{2}):(\d{2}):(\d{2}\.\d{2})");
            if (durMatch.Success)
            {
                var ts = TimeSpan.Parse(durMatch.Groups[0].Value.Replace("Duration: ", ""));
                duration = ts.TotalSeconds;
            }

            var fpsMatch = Regex.Match(output, @"(\d+(\.\d+)?) fps");
            if (fpsMatch.Success)
            if (fpsMatch.Success)
            {
                double.TryParse(fpsMatch.Groups[1].Value, out fps);
            }

            var resMatch = Regex.Match(output, @"Video: .+, (\d+)x(\d+)");
            if (resMatch.Success)
            {
                int.TryParse(resMatch.Groups[1].Value, out width);
                int.TryParse(resMatch.Groups[2].Value, out height);
            }

            return (duration, fps, width, height);
        }

        private async Task RunProcessAsync(string fileName, string args, string? workingDir = null)
        {
            Process p = new Process();
            if (!string.IsNullOrEmpty(workingDir)) p.StartInfo.WorkingDirectory = workingDir;
            p.StartInfo.FileName = fileName;
            p.StartInfo.Arguments = args;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.CreateNoWindow = true;
            
            // To avoid deadlocks, redirect both if reading, but here we generally just wait.
            // Realesrgan might output to stderr/stdout.
            p.StartInfo.RedirectStandardOutput = true; 
            p.StartInfo.RedirectStandardError = true;

            p.Start();
            
            // Consume streams to prevent deadlock
            var t1 = p.StandardOutput.ReadToEndAsync();
            var t2 = p.StandardError.ReadToEndAsync();

            await p.WaitForExitAsync();
            
            if (p.ExitCode != 0)
            {
                // Throw simple error for now
                throw new Exception($"Process {Path.GetFileName(fileName)} failed with code {p.ExitCode}.");
            }
        }
    }
}
