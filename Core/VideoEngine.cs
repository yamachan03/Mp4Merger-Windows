using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Mp4Merger.Core
{
    public class VideoEngine
    {
        private string? _detectedH264Encoder;
        private string? _detectedHevcEncoder;

        private async Task DetectGpuEncodersAsync(string ffmpegPath)
        {
            if (_detectedH264Encoder != null) return; // already detected

            // "-encoders" lists everything compiled into the build regardless of actual hardware,
            // so each candidate must be validated with a tiny test encode before being trusted.
            string output = "";
            try
            {
                var p = new Process();
                p.StartInfo.FileName = ffmpegPath;
                p.StartInfo.Arguments = "-encoders";
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;
                p.Start();

                output = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("GPU Encoder Detection failed: " + ex.Message);
            }

            _detectedH264Encoder = await PickWorkingEncoderAsync(ffmpegPath, output,
                new[] { "h264_nvenc", "h264_qsv", "h264_amf" }, "libx264");
            _detectedHevcEncoder = await PickWorkingEncoderAsync(ffmpegPath, output,
                new[] { "hevc_nvenc", "hevc_qsv", "hevc_amf" }, "libx265");
        }

        private async Task<string> PickWorkingEncoderAsync(string ffmpegPath, string encodersOutput, string[] candidates, string cpuFallback)
        {
            foreach (var enc in candidates)
            {
                if (!encodersOutput.Contains(enc)) continue;
                if (await TestEncoderAsync(ffmpegPath, enc)) return enc;
            }
            return cpuFallback;
        }

        private async Task<bool> TestEncoderAsync(string ffmpegPath, string encoder)
        {
            try
            {
                var p = new Process();
                p.StartInfo.FileName = ffmpegPath;
                p.StartInfo.Arguments = $"-hide_banner -f lavfi -i nullsrc=s=256x256:r=30 -frames:v 3 -pix_fmt yuv420p -c:v {encoder} -f null -";
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;
                p.Start();

                var t1 = p.StandardOutput.ReadToEndAsync();
                var t2 = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();

                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> VerifyFileAsync(string filePath, string ffmpegPath)
        {
            try
            {
                var p = new Process();
                p.StartInfo.FileName = ffmpegPath;
                p.StartInfo.Arguments = $"-v error -i \"{filePath}\" -f null -";
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;
                p.Start();

                string errOutput = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();

                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public async Task ProcessFilesAsync(
            List<string> inputFiles,
            string outputFile,
            string resolution,
            bool fixJitter,
            bool stabilize,
            bool hevc,
            bool normalize,
            bool mergeOutput,
            int stabilizeSmoothing,
            Action<double, string> reportProgress)
        {
            string ffmpegPath = ResolveToolPath("ffmpeg");
            string ffprobePath = ResolveToolPath("ffprobe");
            await DetectGpuEncodersAsync(ffmpegPath);

            // 1. Probe every file once up front and reuse the results below
            //    (duration totals, format mismatch check, target resolution, TS remux codec)
            var probedItems = new List<VideoItem>();
            TimeSpan totalDuration = TimeSpan.Zero;
            var durations = new List<double>();
            foreach (var file in inputFiles)
            {
                var item = await VideoItem.GetVideoDetailsAsync(file, ffprobePath);
                probedItems.Add(item);
                totalDuration += TimeSpan.FromSeconds(item.DurationSeconds);
                durations.Add(item.DurationSeconds);
            }
            if (totalDuration == TimeSpan.Zero) totalDuration = TimeSpan.FromSeconds(1);

            // --- BATCH MODE (Save to individual files) ---
            if (!mergeOutput)
            {
                string outputDir = Path.GetDirectoryName(outputFile) ?? Path.GetTempPath();
                
                for (int i = 0; i < inputFiles.Count; i++)
                {
                    string file = inputFiles[i];
                    string baseName = Path.GetFileNameWithoutExtension(file);
                    
                    // Suffix naming
                    string suffix = "";
                    if (fixJitter) suffix += "_jitter";
                    if (stabilize) suffix += "_stab";
                    if (hevc) suffix += "_hevc";
                    if (normalize) suffix += "_norm";
                    
                    if (resolution == "AI_1x") suffix += "_ai_1x";
                    else if (resolution == "AI_4x") suffix += "_ai";
                    else if (resolution == "AI_Anime") suffix += "_ai_anime";
                    else if (resolution == "1080p") suffix += "_1080";
                    else if (resolution == "4K") suffix += "_4k";

                    string singleOutputFile = Path.Combine(outputDir, $"{baseName}{suffix}.mp4");
                    
                    reportProgress((double)i / inputFiles.Count * 100, $"Processing {i+1}/{inputFiles.Count}...");

                    if (resolution.StartsWith("AI_"))
                    {
                        string tempPre = Path.Combine(Path.GetTempPath(), $"pretmp_{Guid.NewGuid()}.mp4");
                        try
                        {
                            // 1. Preprocess with filters
                            await PreprocessFileAsync(
                                file,
                                tempPre,
                                "Original",
                                fixJitter,
                                stabilize,
                                hevc,
                                normalize,
                                durations[i],
                                stabilizeSmoothing,
                                (pct) =>
                                {
                                    double overallPct = ((double)i / inputFiles.Count * 100) + (pct / inputFiles.Count * 0.3);
                                    reportProgress(overallPct, $"Processing {i+1}/{inputFiles.Count} (Filters): {pct:F1}%");
                                });

                            // 2. Run AI upscaling
                            string modelName = resolution == "AI_Anime" ? "realesrgan-x4plus-anime" : "realesrgan-x4plus";
                            bool keepRes = resolution == "AI_1x";
                            var ups = new UpscaleService(ResolveToolPath("ffmpeg"), ResolveToolPath("realesrgan-ncnn-vulkan"));
                            await ups.UpscaleVideoAsync(tempPre, singleOutputFile, modelName, keepRes, (pct, msg) => 
                            {
                                double overallPct = ((double)i / inputFiles.Count * 100) + (30.0 / inputFiles.Count) + (pct / inputFiles.Count * 0.7);
                                reportProgress(overallPct, $"Processing {i+1}/{inputFiles.Count} (AI): {pct:F1}%");
                            });
                        }
                        finally
                        {
                            if (File.Exists(tempPre))
                            {
                                try { File.Delete(tempPre); } catch { }
                            }
                        }
                    }
                    else
                    {
                        await PreprocessFileAsync(
                            file,
                            singleOutputFile,
                            resolution,
                            fixJitter,
                            stabilize,
                            hevc,
                            normalize,
                            durations[i],
                            stabilizeSmoothing,
                            (pct) =>
                            {
                                double overallPct = ((double)i / inputFiles.Count * 100) + (pct / inputFiles.Count);
                                reportProgress(overallPct, $"Processing {i+1}/{inputFiles.Count}: {pct:F1}%");
                            });
                    }
                }
                reportProgress(100, "Ready");
                return;
            }

            // --- MERGING MODE ---
            bool isAI = resolution.StartsWith("AI_");
            if (isAI)
            {
                string tempMergeOutput = Path.Combine(Path.GetTempPath(), $"mergetmp_before_ai_{Guid.NewGuid()}.mp4");
                try
                {
                    // Merge to tempMergeOutput first (Original resolution)
                    await ProcessFilesAsync(
                        inputFiles,
                        tempMergeOutput,
                        "Original",
                        fixJitter,
                        stabilize,
                        hevc,
                        normalize,
                        true,
                        stabilizeSmoothing,
                        (pct, msg) =>
                        {
                            double overallPct = pct * 0.4;
                            reportProgress(overallPct, $"{msg} (AI Phase 1/2)");
                        });

                    // Run AI upscaling
                    string modelName = resolution == "AI_Anime" ? "realesrgan-x4plus-anime" : "realesrgan-x4plus";
                    bool keepRes = resolution == "AI_1x";
                    var ups = new UpscaleService(ResolveToolPath("ffmpeg"), ResolveToolPath("realesrgan-ncnn-vulkan"));
                    await ups.UpscaleVideoAsync(tempMergeOutput, outputFile, modelName, keepRes, (pct, msg) => 
                    {
                        double overallPct = 40.0 + (pct * 0.6);
                        reportProgress(overallPct, $"{msg} (AI Phase 2/2)");
                    });
                }
                finally
                {
                    if (File.Exists(tempMergeOutput))
                    {
                        try { File.Delete(tempMergeOutput); } catch { }
                    }
                }
                return;
            }

            // 2. Determine if we need re-encoding
            bool needsReencode = fixJitter || stabilize || hevc || normalize || resolution != "Original";

            // Also check resolution/format mismatch across files
            if (!needsReencode && probedItems.Count > 1)
            {
                var first = probedItems[0];
                for (int i = 1; i < probedItems.Count; i++)
                {
                    if (probedItems[i].Width != first.Width || probedItems[i].Height != first.Height || probedItems[i].FrameRate != first.FrameRate)
                    {
                        needsReencode = true;
                        break;
                    }
                }
            }

            if (!needsReencode)
            {
                // --- TIER 1: Concat Demuxer (Fast Concat) ---
                try
                {
                    reportProgress(5, "Attempting Tier 1 (Fast Concat)...");
                    await RunConcatDemuxerAsync(inputFiles, outputFile);
                    reportProgress(100, "Ready");
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Tier 1 Concat failed, falling back to Tier 2. Error: " + ex.Message);
                }

                // --- TIER 2: TS Remux Concat ---
                try
                {
                    reportProgress(10, "Attempting Tier 2 (TS Remux Concat)...");
                    await RunTsRemuxConcatAsync(inputFiles, probedItems, outputFile, (pct) =>
                    {
                        reportProgress(10 + pct * 0.9, "Merging (Tier 2)...");
                    });
                    reportProgress(100, "Ready");
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Tier 2 Concat failed, falling back to Tier 3. Error: " + ex.Message);
                }
            }

            // --- TIER 3: Re-encoding & Filters ---
            reportProgress(0, "Running Tier 3 (Re-encoding & Filters)...");
            
            var tempFiles = new List<string>();
            try
            {
                string targetRes = resolution;
                if (resolution == "Original")
                {
                    targetRes = $"{probedItems[0].Width}x{probedItems[0].Height}";
                }

                for (int i = 0; i < inputFiles.Count; i++)
                {
                    string tempOut = Path.Combine(Path.GetTempPath(), $"mergetmp_{Guid.NewGuid()}.mp4");
                    tempFiles.Add(tempOut);

                    await PreprocessFileAsync(
                        inputFiles[i],
                        tempOut,
                        targetRes,
                        fixJitter,
                        stabilize,
                        hevc,
                        normalize,
                        durations[i],
                        stabilizeSmoothing,
                        (pct) =>
                        {
                            double overallPct = ((double)i / inputFiles.Count * 90);
                            overallPct += (pct / inputFiles.Count * 0.9);
                            reportProgress(overallPct, $"Preprocessing {i+1}/{inputFiles.Count}: {pct:F1}%");
                        });
                }

                reportProgress(90, "Merging preprocessed segments...");
                await RunConcatDemuxerAsync(tempFiles, outputFile);
                reportProgress(100, "Ready");
            }
            finally
            {
                foreach (var tmp in tempFiles)
                {
                    if (File.Exists(tmp))
                    {
                        try { File.Delete(tmp); } catch { }
                    }
                }
            }
        }

        private async Task PreprocessFileAsync(
            string inputFile,
            string outputFile,
            string resolution,
            bool fixJitter,
            bool stabilize,
            bool hevc,
            bool normalize,
            double durationSeconds,
            int stabilizeSmoothing,
            Action<double> reportProgress)
        {
            string ffmpegPath = ResolveToolPath("ffmpeg");
            string encoder = hevc ? _detectedHevcEncoder! : _detectedH264Encoder!;

            // 1. Stabilization (Pass 1 of vidstab)
            string? trfFile = null;
            if (stabilize)
            {
                trfFile = Path.Combine(Path.GetTempPath(), $"stab_{Guid.NewGuid()}.trf");
                string detectArgs = $"-i \"{inputFile}\" -vf vidstabdetect=shakiness=5:accuracy=15:result=\"{trfFile.Replace("\\", "/").Replace(":", "\\:")}\" -f null -";

                await RunFFmpegWithProgressAsync(ffmpegPath, detectArgs, durationSeconds, (pct) =>
                {
                    reportProgress(pct * 0.3);
                });
            }

            // 2. Preprocess & Encode (Pass 2)
            var vfFilters = new List<string>();
            if (fixJitter)
            {
                vfFilters.Add("yadif=0:-1:0");
            }

            if (stabilize && trfFile != null)
            {
                vfFilters.Add($"vidstabtransform=input=\"{trfFile.Replace("\\", "/").Replace(":", "\\:")}\":smoothing={stabilizeSmoothing}:crop=black:optzoom=1");
            }

            if (resolution == "1080p")
            {
                vfFilters.Add("scale=-2:1080:flags=lanczos");
            }
            else if (resolution == "4K")
            {
                vfFilters.Add("scale=-2:2160:flags=lanczos");
            }
            else if (resolution.Contains("x"))
            {
                var parts = resolution.Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                {
                    vfFilters.Add($"scale={w}:{h}:flags=lanczos");
                }
            }

            var afFilters = new List<string>();
            if (normalize)
            {
                afFilters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
            }

            string vfArg = vfFilters.Count > 0 ? $"-vf \"{string.Join(",", vfFilters)}\" " : "";
            string afArg = afFilters.Count > 0 ? $"-af \"{string.Join(",", afFilters)}\" " : "";

            string encoderArg = $"-c:v {encoder} -pix_fmt yuv420p ";
            if (encoder.Contains("nvenc")) encoderArg += "-b:v 8000k -maxrate 12000k -bufsize 16000k ";
            else if (encoder.Contains("qsv")) encoderArg += "-b:v 8000k -maxrate 12000k -bufsize 16000k ";
            else if (encoder.Contains("amf")) encoderArg += "-b:v 8000k -maxrate 12000k -bufsize 16000k ";
            // At a fixed 10M bitrate, "medium" is nearly identical in quality to "slow" but ~2x faster
            else encoderArg += "-b:v 10000k -maxrate 14000k -bufsize 18000k -preset medium ";

            string audioArg = normalize ? "-c:a aac -b:a 192k " : "-c:a copy ";
            
            // Standardize output to constant 30fps CFR and timescale 90000 to resolve VFR timing issues
            string cfrArg = "-r 30 -video_track_timescale 90000 ";
            
            string limitArg = durationSeconds > 0 ? $"-t {durationSeconds} " : "";
            string mainArgs = $"{limitArg}-i \"{inputFile}\" {vfArg}{afArg}{encoderArg}{cfrArg}{audioArg}-y \"{outputFile}\"";

            await RunFFmpegWithProgressAsync(ffmpegPath, mainArgs, durationSeconds, (pct) => 
            {
                double currentPct = stabilize ? 30.0 + (pct * 0.7) : pct;
                reportProgress(currentPct);
            });

            if (trfFile != null && File.Exists(trfFile))
            {
                try { File.Delete(trfFile); } catch { }
            }
        }

        private async Task RunConcatDemuxerAsync(List<string> inputFiles, string outputFile)
        {
            string ffmpegPath = ResolveToolPath("ffmpeg");
            string listFile = Path.Combine(Path.GetTempPath(), $"concat_list_{Guid.NewGuid()}.txt");

            try
            {
                using (var sw = new StreamWriter(listFile))
                {
                    foreach (var file in inputFiles)
                    {
                        sw.WriteLine($"file '{file.Replace("'", "'\\''")}'");
                    }
                }

                string args = $"-f concat -safe 0 -i \"{listFile}\" -c copy -y \"{outputFile}\"";
                
                var p = new Process();
                p.StartInfo.FileName = ffmpegPath;
                p.StartInfo.Arguments = args;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;
                p.Start();

                string errLog = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();

                if (p.ExitCode != 0)
                {
                    throw new Exception($"FFmpeg Concat failed: {errLog}");
                }
            }
            finally
            {
                if (File.Exists(listFile))
                {
                    try { File.Delete(listFile); } catch { }
                }
            }
        }

        private async Task RunTsRemuxConcatAsync(List<string> inputFiles, List<VideoItem> probedItems, string outputFile, Action<double> reportProgress)
        {
            string ffmpegPath = ResolveToolPath("ffmpeg");
            var tsFiles = new List<string>();

            try
            {
                for (int i = 0; i < inputFiles.Count; i++)
                {
                    string tsOut = Path.Combine(Path.GetTempPath(), $"ts_{Guid.NewGuid()}.ts");
                    tsFiles.Add(tsOut);

                    string codec = probedItems[i].CodecName.ToLowerInvariant();
                    string bsfArg = codec switch
                    {
                        "h264" => "-bsf:v h264_mp4toannexb ",
                        "hevc" => "-bsf:v hevc_mp4toannexb ",
                        _ => ""
                    };

                    string remuxArgs = $"-i \"{inputFiles[i]}\" -c copy {bsfArg}-f mpegts -y \"{tsOut}\"";
                    var p = new Process();
                    p.StartInfo.FileName = ffmpegPath;
                    p.StartInfo.Arguments = remuxArgs;
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.CreateNoWindow = true;
                    p.Start();
                    await p.WaitForExitAsync();
                    
                    if (p.ExitCode != 0) throw new Exception($"TS remux failed for {inputFiles[i]}");
                    
                    reportProgress(((double)(i + 1) / inputFiles.Count) * 50.0);
                }

                // Concat TS files
                string tsConcatList = string.Join("|", tsFiles);
                string finalArgs = $"-i \"concat:{tsConcatList}\" -c copy -y \"{outputFile}\"";
                
                var pMerge = new Process();
                pMerge.StartInfo.FileName = ffmpegPath;
                pMerge.StartInfo.Arguments = finalArgs;
                pMerge.StartInfo.UseShellExecute = false;
                pMerge.StartInfo.CreateNoWindow = true;
                pMerge.Start();
                await pMerge.WaitForExitAsync();

                if (pMerge.ExitCode != 0) throw new Exception("TS concat merge failed.");
                
                reportProgress(100.0);
            }
            finally
            {
                foreach (var ts in tsFiles)
                {
                    if (File.Exists(ts))
                    {
                        try { File.Delete(ts); } catch { }
                    }
                }
            }
        }

        private async Task RunFFmpegWithProgressAsync(string ffmpegPath, string arguments, double durationSeconds, Action<double> reportProgress)
        {
            var tcs = new TaskCompletionSource<bool>();
            var errorLog = new System.Text.StringBuilder();

            var p = new Process();
            p.StartInfo.FileName = ffmpegPath;
            p.StartInfo.Arguments = arguments;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.CreateNoWindow = true;

            p.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    errorLog.AppendLine(e.Data);
                    
                    var match = Regex.Match(e.Data, @"time=(\d{2}):(\d{2}):(\d{2}\.\d{2})");
                    if (match.Success && durationSeconds > 0)
                    {
                        int h = int.Parse(match.Groups[1].Value);
                        int m = int.Parse(match.Groups[2].Value);
                        double s = double.Parse(match.Groups[3].Value);
                        double elapsedSecs = (h * 3600) + (m * 60) + s;
                        
                        double pct = (elapsedSecs / durationSeconds) * 100.0;
                        if (pct > 100) pct = 100;
                        if (pct < 0) pct = 0;
                        
                        reportProgress(pct);
                    }
                }
            };

            p.EnableRaisingEvents = true;
            p.Exited += (sender, e) => tcs.TrySetResult(p.ExitCode == 0);

            try
            {
                p.Start();
                p.BeginErrorReadLine();
                
                bool success = await tcs.Task;
                if (!success)
                {
                    throw new Exception($"FFmpeg failed with code {p.ExitCode}.\nLogs:\n{errorLog}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to run FFmpeg: {ex.Message}", ex);
            }
        }

        private string ResolveToolPath(string toolName)
        {
            return AppPaths.ResolveTool(toolName);
        }
    }
}
