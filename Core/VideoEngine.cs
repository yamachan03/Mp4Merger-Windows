using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Mp4Merger.Core
{
    /// A merge that failed, described in terms of the user's files. `Log` holds
    /// ffmpeg's own output for the "show log" affordance.
    public sealed class MergeFailedException : Exception
    {
        public string Log { get; }
        public MergeFailedException(string summary, string log) : base(summary) { Log = log; }
    }

    public class VideoEngine
    {
        private string? _detectedH264Encoder;
        private string? _detectedHevcEncoder;

        /// Language for user-facing messages, as AppConfig stores it.
        public string Language { get; set; } = "ja";

        /// Asks the user a yes/no question. Returning false means "stop".
        public Func<string, Task<bool>> ConfirmAsync { get; set; } = _ => Task.FromResult(false);

        private string H264Encoder => _detectedH264Encoder ?? "libx264";
        private string HevcEncoder => _detectedHevcEncoder ?? "libx265";

        // MARK: - Encoder detection

        private async Task DetectGpuEncodersAsync(string ffmpegPath, CancellationToken token)
        {
            if (_detectedH264Encoder != null) return; // already detected

            // "-encoders" lists everything compiled into the build regardless of actual hardware,
            // so each candidate must be validated with a tiny test encode before being trusted.
            string output = "";
            try
            {
                var result = await ProcessRunner.CaptureAsync(ffmpegPath, new[] { "-encoders" }, token)
                    .ConfigureAwait(false);
                output = result.StdOut;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Debug.WriteLine("GPU Encoder Detection failed: " + ex.Message);
            }

            _detectedH264Encoder = await PickWorkingEncoderAsync(ffmpegPath, output,
                new[] { "h264_nvenc", "h264_qsv", "h264_amf" }, "libx264", token).ConfigureAwait(false);
            _detectedHevcEncoder = await PickWorkingEncoderAsync(ffmpegPath, output,
                new[] { "hevc_nvenc", "hevc_qsv", "hevc_amf" }, "libx265", token).ConfigureAwait(false);
        }

        private async Task<string> PickWorkingEncoderAsync(string ffmpegPath, string encodersOutput,
            string[] candidates, string cpuFallback, CancellationToken token)
        {
            foreach (var enc in candidates)
            {
                if (!encodersOutput.Contains(enc)) continue;
                if (await TestEncoderAsync(ffmpegPath, enc, token).ConfigureAwait(false)) return enc;
            }
            return cpuFallback;
        }

        private async Task<bool> TestEncoderAsync(string ffmpegPath, string encoder, CancellationToken token)
        {
            try
            {
                var result = await ProcessRunner.CaptureAsync(ffmpegPath, new[]
                {
                    "-hide_banner", "-f", "lavfi", "-i", "nullsrc=s=256x256:r=30",
                    "-frames:v", "3", "-pix_fmt", "yuv420p", "-c:v", encoder, "-f", "null", "-"
                }, token).ConfigureAwait(false);
                return result.ExitCode == 0;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return false;
            }
        }

        // MARK: - Probing

        /// ffprobe every file, in order. Throws naming the first unreadable one.
        public async Task<List<MediaProbe>> ProbeAsync(IReadOnlyList<string> files, CancellationToken token)
        {
            string ffprobePath = ResolveToolPath("ffprobe");
            var probes = new List<MediaProbe>();
            foreach (string file in files)
            {
                var result = await ProcessRunner.CaptureAsync(ffprobePath, MediaProbe.FfprobeArguments(file), token)
                    .ConfigureAwait(false);
                var probe = result.ExitCode == 0 ? MediaProbe.Parse(result.StdOut, file) : null;
                if (probe == null)
                {
                    throw new MergeFailedException(
                        Localizer.GetDynamic("CouldNotRead", Language, Path.GetFileName(file)),
                        result.StdErr);
                }
                probes.Add(probe);
            }
            return probes;
        }

        /// ffprobe each file, tolerating ones it cannot read: callers that only
        /// want encoding hints fall back to defaults for those.
        private async Task<List<MediaProbe?>> ProbeEachAsync(IReadOnlyList<string> files, CancellationToken token)
        {
            var probes = new List<MediaProbe?>();
            foreach (string file in files)
            {
                try
                {
                    var one = await ProbeAsync(new[] { file }, token).ConfigureAwait(false);
                    probes.Add(one[0]);
                }
                catch (OperationCanceledException) { throw; }
                catch { probes.Add(null); }
            }
            return probes;
        }

        /// Runs the integrity checker over every input, throwing on the first
        /// damaged one. The old implementation passed `-v error` and trusted the
        /// exit status, so it reported damaged files as clean.
        public async Task<CheckResult> VerifyFileAsync(string filePath, CancellationToken token)
        {
            var checker = new VideoIntegrityChecker(ResolveToolPath("ffmpeg"), ResolveToolPath("ffprobe"));
            var targets = await checker.InspectAsync(new[] { filePath }, token).ConfigureAwait(false);
            return await checker.CheckAsync(targets[0], CheckLevel.Full, _ => { }, token).ConfigureAwait(false);
        }

        // MARK: - Merge context

        /// What one merge learned about its inputs, kept so a failure can be
        /// explained in terms of files instead of ffmpeg's log.
        private sealed class MergeContext
        {
            public DirectoryInfo WorkDirectory { get; }
            public MergeCompatibility.Report? Report { get; set; }
            private readonly List<(string Name, string Path, string Stamp)> _snapshots;

            public MergeContext(DirectoryInfo workDirectory, IReadOnlyList<string> inputs)
            {
                WorkDirectory = workDirectory;
                _snapshots = inputs.Select(p => (Path.GetFileName(p), p, Stamp(p))).ToList();
            }

            /// Inputs that another app modified or updated while we were reading
            /// them. Their content cannot be trusted.
            public List<string> ChangedInputs =>
                _snapshots.Where(s => Stamp(s.Path) != s.Stamp).Select(s => s.Name).ToList();

            private static string Stamp(string path)
            {
                try
                {
                    var info = new FileInfo(path);
                    return $"{info.Length}@{info.LastWriteTimeUtc.Ticks}";
                }
                catch { return "missing"; }
            }

            public string Temp(string name) => Path.Combine(WorkDirectory.FullName, name);
        }

        /// A merged file that did not survive inspection.
        private sealed class VerificationFailure : Exception
        {
            public MergeDiagnosis.Failure Failure { get; }
            public string FfmpegLog { get; }
            public VerificationFailure(MergeDiagnosis.Failure failure, string log)
            {
                Failure = failure;
                FfmpegLog = log;
            }
        }

        /// The user declined a step the merge cannot continue without.
        private sealed class CancelledByUserException : Exception
        {
            public CancelledByUserException(string message) : base(message) { }
        }

        // MARK: - Entry point

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
            Action<double, string> reportProgress,
            CancellationToken token)
        {
            string ffmpegPath = ResolveToolPath("ffmpeg");
            await DetectGpuEncodersAsync(ffmpegPath, token).ConfigureAwait(false);

            var workDirectory = WorkDir.Create();
            var context = new MergeContext(workDirectory, inputFiles);
            try
            {
                if (!mergeOutput)
                {
                    await ProcessIndividuallyAsync(inputFiles, outputFile, resolution, fixJitter, stabilize,
                        hevc, normalize, stabilizeSmoothing, context, reportProgress, token).ConfigureAwait(false);
                    return;
                }

                await MergeAsync(inputFiles, outputFile, resolution, fixJitter, stabilize, hevc, normalize,
                    stabilizeSmoothing, context, reportProgress, token).ConfigureAwait(false);
            }
            catch (VerificationFailure failure)
            {
                throw new MergeFailedException(
                    ChangedInputsNote(context) + MergeDiagnosis.Explain(failure.Failure, context.Report, Language),
                    DetailedLog(failure.FfmpegLog, context.Report));
            }
            catch (CancelledByUserException ex)
            {
                throw new MergeFailedException(ex.Message, "");
            }
            finally
            {
                try { workDirectory.Delete(recursive: true); } catch { }
            }
        }

        /// Leads a failure message when an input changed under us, because then
        /// that — not the format — is the likely cause.
        private string ChangedInputsNote(MergeContext context)
        {
            var changed = context.ChangedInputs;
            if (changed.Count == 0) return "";
            return Localizer.GetDynamic("InputsChangedDuringMerge", Language, string.Join(", ", changed)) + "\n\n";
        }

        private static string DetailedLog(string log, MergeCompatibility.Report? report) =>
            report == null ? log : MergeDiagnosis.Summary(report.Probes) + "\n\n" + log;

        /// The output must not be one of the files being read.
        private void EnsureNotAnInput(string output, IReadOnlyList<string> inputs)
        {
            string target;
            try { target = Path.GetFullPath(output); } catch { return; }

            foreach (string input in inputs)
            {
                string full;
                try { full = Path.GetFullPath(input); } catch { continue; }
                if (string.Equals(full, target, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CancelledByUserException(
                        Localizer.GetDynamic("OutputWouldOverwriteInput", Language, Path.GetFileName(output)));
                }
            }
        }

        // MARK: - Batch mode

        private async Task ProcessIndividuallyAsync(
            List<string> inputFiles, string outputFile, string resolution, bool fixJitter, bool stabilize,
            bool hevc, bool normalize, int stabilizeSmoothing, MergeContext context,
            Action<double, string> reportProgress, CancellationToken token)
        {
            string outputDir = Path.GetDirectoryName(outputFile) ?? Path.GetTempPath();
            var probes = await ProbeEachAsync(inputFiles, token).ConfigureAwait(false);

            for (int i = 0; i < inputFiles.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                string file = inputFiles[i];
                string baseName = Path.GetFileNameWithoutExtension(file);
                string suffix = SuffixFor(resolution, fixJitter, stabilize, hevc, normalize);
                string singleOutputFile = Path.Combine(outputDir, $"{baseName}{suffix}.mp4");
                EnsureNotAnInput(singleOutputFile, inputFiles);

                double duration = probes[i]?.Duration ?? 0;
                reportProgress((double)i / inputFiles.Count * 100,
                    Localizer.GetDynamic("ProcessingFileOfN", Language, (i + 1).ToString(), inputFiles.Count.ToString()));

                string source = file;
                if (stabilize)
                {
                    source = await StabilizeAsync(file, probes[i], duration, stabilizeSmoothing, hevc, context,
                        i + 1, inputFiles.Count,
                        (pct, msg) => reportProgress((double)i / inputFiles.Count * 100 + pct / inputFiles.Count * 0.5, msg),
                        token).ConfigureAwait(false);
                    probes[i] = (await ProbeEachAsync(new[] { source }, token).ConfigureAwait(false))[0];
                    duration = probes[i]?.Duration ?? duration;
                }

                if (resolution.StartsWith("AI_", StringComparison.Ordinal))
                {
                    string tempPre = context.Temp($"pretmp_{Guid.NewGuid():N}.mp4");
                    await EncodeOneAsync(source, tempPre, "Original", probes[i], fixJitter, hevc, normalize,
                        duration, null,
                        pct => reportProgress((double)i / inputFiles.Count * 100 + pct / inputFiles.Count * 0.3,
                            Localizer.GetDynamic("ProcessingFiltersOfN", Language, (i + 1).ToString(), inputFiles.Count.ToString())),
                        token).ConfigureAwait(false);

                    await RunAiUpscaleAsync(tempPre, singleOutputFile, resolution,
                        pct => reportProgress((double)i / inputFiles.Count * 100 + 30.0 / inputFiles.Count + pct / inputFiles.Count * 0.7,
                            Localizer.GetDynamic("ProcessingAiOfN", Language, (i + 1).ToString(), inputFiles.Count.ToString())),
                        token).ConfigureAwait(false);
                }
                else
                {
                    await EncodeOneAsync(source, singleOutputFile, resolution, probes[i], fixJitter, hevc, normalize,
                        duration, null,
                        pct => reportProgress((double)i / inputFiles.Count * 100 + pct / inputFiles.Count,
                            Localizer.GetDynamic("ProcessingFileOfN", Language, (i + 1).ToString(), inputFiles.Count.ToString())),
                        token).ConfigureAwait(false);
                }
            }

            reportProgress(100, Localizer.Get("Ready", Language));
        }

        private static string SuffixFor(string resolution, bool fixJitter, bool stabilize, bool hevc, bool normalize)
        {
            string suffix = "";
            if (fixJitter) suffix += "_jitter";
            if (stabilize) suffix += "_stab";
            if (hevc) suffix += "_hevc";
            if (normalize) suffix += "_norm";
            suffix += resolution switch
            {
                "AI_1x" => "_ai_1x",
                "AI_4x" => "_ai",
                "AI_Anime" => "_ai_anime",
                "1080p" => "_1080",
                "4K" => "_4k",
                _ => ""
            };
            return suffix;
        }

        // MARK: - Merge mode

        private async Task MergeAsync(
            List<string> inputFiles, string outputFile, string resolution, bool fixJitter, bool stabilize,
            bool hevc, bool normalize, int stabilizeSmoothing, MergeContext context,
            Action<double, string> reportProgress, CancellationToken token)
        {
            EnsureNotAnInput(outputFile, inputFiles);

            // AI upscaling wraps a plain merge: merge at the original size first,
            // then hand the single file to Real-ESRGAN.
            if (resolution.StartsWith("AI_", StringComparison.Ordinal))
            {
                string tempMerge = context.Temp($"mergetmp_before_ai_{Guid.NewGuid():N}.mp4");
                await MergeAsync(inputFiles, tempMerge, "Original", fixJitter, stabilize, hevc, normalize,
                    stabilizeSmoothing, context,
                    (pct, msg) => reportProgress(pct * 0.4, msg + " (AI 1/2)"), token).ConfigureAwait(false);

                await RunAiUpscaleAsync(tempMerge, outputFile, resolution,
                    pct => reportProgress(40.0 + pct * 0.6,
                        Localizer.GetDynamic("AiUpscalingPhase", Language, pct.ToString("F1", CultureInfo.InvariantCulture))),
                    token).ConfigureAwait(false);
                return;
            }

            int? targetHeight = resolution switch { "1080p" => 1080, "4K" => 2160, _ => null };

            var workingFiles = new List<string>(inputFiles);
            var sourceProbes = await ProbeEachAsync(workingFiles, token).ConfigureAwait(false);

            // --- PRE-PROCESSING: Gimbal stabilization ---
            if (stabilize)
            {
                var stabilized = new List<string>();
                for (int i = 0; i < workingFiles.Count; i++)
                {
                    double duration = sourceProbes[i]?.Duration ?? 0;
                    string result = await StabilizeAsync(workingFiles[i], sourceProbes[i], duration,
                        stabilizeSmoothing, hevc, context, i + 1, workingFiles.Count,
                        (pct, msg) => reportProgress(((double)i / workingFiles.Count * 100) + pct / workingFiles.Count, msg),
                        token).ConfigureAwait(false);
                    stabilized.Add(result);
                }
                workingFiles = stabilized;
                sourceProbes = await ProbeEachAsync(workingFiles, token).ConfigureAwait(false);
            }

            // Inspect what will actually be joined (after stabilization, if any).
            // A single file has nothing to be compatible with, but its frame rate
            // and bit rate still guide a re-encode.
            if (workingFiles.Count > 1)
            {
                reportProgress(0, Localizer.Get("InspectingFiles", Language));
                var probes = await ProbeAsync(workingFiles, token).ConfigureAwait(false);
                context.Report = MergeCompatibility.Analyze(probes);
                sourceProbes = probes.Cast<MediaProbe?>().ToList();
            }

            double totalDuration = sourceProbes.Sum(p => p?.Duration ?? 0);
            if (totalDuration <= 0) totalDuration = 1;

            bool filtersActive = normalize || fixJitter || targetHeight != null || hevc;
            bool fastMergePossible = !filtersActive;

            reportProgress(0, Localizer.Get(
                filtersActive || stabilize ? "ModeFilterReencode" : "ModeFastMerge", Language));

            // --- TIER 1 & 2: copy merge, after conforming what has to be conformed ---
            if (fastMergePossible)
            {
                var failure = await CopyMergeConformingAsync(workingFiles, totalDuration, outputFile, context,
                    reportProgress, token).ConfigureAwait(false);
                if (failure == null)
                {
                    reportProgress(100, Localizer.Get("Ready", Language));
                    return;
                }

                // Re-encoding everything can take as long as the footage itself,
                // so it is never started without the user seeing why.
                string question = ChangedInputsNote(context)
                    + MergeDiagnosis.Explain(failure.Failure, context.Report, Language, includeLogHint: false)
                    + "\n\n" + Localizer.Get("FullReencodeQuestion", Language);
                if (!await ConfirmAsync(question).ConfigureAwait(false)) throw failure;

                reportProgress(0, Localizer.Get("FastMergeFailedReencoding", Language));
            }
            else
            {
                reportProgress(0, Localizer.Get("ProcessingWithReencoding", Language));
            }

            // --- TIER 3: re-encode every segment, then join ---
            await ReencodeAndConcatAsync(workingFiles, sourceProbes, outputFile, targetHeight, fixJitter,
                hevc, normalize, totalDuration, context, reportProgress, token).ConfigureAwait(false);

            reportProgress(100, Localizer.Get("Ready", Language));
        }

        // MARK: - Copy merge

        /// Conforms whatever stops a stream-copy merge, joins, and verifies.
        /// Returns null on success, or why copy merging did not work out so the
        /// caller can offer re-encoding everything; throws when the user declined.
        private async Task<VerificationFailure?> CopyMergeConformingAsync(
            List<string> workingFiles, double totalDuration, string outputFile, MergeContext context,
            Action<double, string> reportProgress, CancellationToken token)
        {
            var reencodeVideo = new HashSet<int>();

            // Codec, size or pixel format differences make a copy merge hopeless,
            // and fixing them costs a re-encode, so the user decides first.
            if (context.Report is { } report && report.VideoDifferences.Count > 0)
            {
                string details = MergeDiagnosis.Describe(report.VideoDifferences, Language);
                if (!await ConfirmAsync(details + "\n\n" + Localizer.Get("VideoMismatchQuestion", Language)).ConfigureAwait(false))
                {
                    throw new CancelledByUserException(
                        Localizer.Get("MergeCancelledFormatsDiffer", Language) + "\n" + details);
                }
                foreach (int index in report.FilesNeedingVideo) reencodeVideo.Add(index);
            }

            var conformed = new List<string>();
            bool askedAboutFrameRate = false;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                foreach (string path in conformed) TryDelete(path);

                var inputs = await ConformInputsAsync(workingFiles, reencodeVideo, context, reportProgress, token)
                    .ConfigureAwait(false);
                conformed = inputs.Where((path, i) => path != workingFiles[i]).ToList();

                VerificationFailure failure;
                try
                {
                    await CopyMergeAsync(inputs, totalDuration, outputFile, conformed.Count > 0, context,
                        reportProgress, token).ConfigureAwait(false);
                    return null;
                }
                catch (VerificationFailure error)
                {
                    failure = error;
                }

                // Frame rate alone usually survives a copy merge. When it did not,
                // offer — once — to re-encode just the clips that differ.
                if (context.Report is not { } current || askedAboutFrameRate) return failure;
                var remaining = current.FilesWithOtherFrameRate.Where(i => !reencodeVideo.Contains(i)).ToList();
                if (remaining.Count == 0) return failure;
                askedAboutFrameRate = true;

                string question = MergeDiagnosis.Explain(failure.Failure, current, Language, includeLogHint: false)
                    + "\n\n" + Localizer.Get("FrameRateMismatchQuestion", Language);
                // Declining means no re-encode at all, so no Tier 3 either.
                if (!await ConfirmAsync(question).ConfigureAwait(false)) throw failure;
                foreach (int index in remaining) reencodeVideo.Add(index);
            }
        }

        /// Writes conformed copies of the inputs that cannot be joined as they are
        /// and returns the list to concatenate. Untouched inputs are passed through.
        private async Task<List<string>> ConformInputsAsync(
            List<string> files, HashSet<int> reencodeVideo, MergeContext context,
            Action<double, string> reportProgress, CancellationToken token)
        {
            if (context.Report is not { } report) return new List<string>(files);

            var audioJobs = new HashSet<int>(report.FilesNeedingAudio);
            var plan = report.PlanTimescale(reencodeVideo.Count > 0);

            // Video first: it is the slow part and the one the user agreed to.
            var videoOrder = Enumerable.Range(0, files.Count).Where(reencodeVideo.Contains).ToList();
            var audioOrder = Enumerable.Range(0, files.Count)
                .Where(i => audioJobs.Contains(i) && !reencodeVideo.Contains(i)).ToList();
            var remuxOrder = plan.RemuxAll
                ? Enumerable.Range(0, files.Count).Where(i => !reencodeVideo.Contains(i) && !audioJobs.Contains(i)).ToList()
                : new List<int>();

            var stages = new (List<int> Order, string Key)[]
            {
                (videoOrder, "ConformingVideo"),
                (audioOrder, "ConformingAudio"),
                (remuxOrder, "AligningTimestamps")
            };

            var result = new List<string>(files);
            foreach (var stage in stages)
            {
                for (int step = 0; step < stage.Order.Count; step++)
                {
                    token.ThrowIfCancellationRequested();
                    int index = stage.Order[step];
                    string message = Localizer.GetDynamic(stage.Key, Language,
                        (step + 1).ToString(), stage.Order.Count.ToString());
                    reportProgress(0, message);

                    string output = context.Temp($"conformed_{index}_{Guid.NewGuid():N}.mov");
                    var args = MergeCompatibility.ConformArguments(
                        report.Probes[index], report.AudioTarget, report.VideoTarget,
                        reencodeVideo.Contains(index), audioJobs.Contains(index),
                        plan.Timescale, H264Encoder, HevcEncoder, output);

                    await RunFFmpegWithProgressAsync(args, report.Probes[index].Duration, 0, DateTime.UtcNow,
                        pct => reportProgress(pct, message), token).ConfigureAwait(false);
                    result[index] = output;
                }
            }
            return result;
        }

        /// Tier 1 (concat demuxer) and, failing that, Tier 2 (rewrap via MPEG-TS).
        /// Throws the failure of the last attempt.
        private async Task CopyMergeAsync(
            List<string> inputs, double totalDuration, string outputFile, bool mapMainStreamsOnly,
            MergeContext context, Action<double, string> reportProgress, CancellationToken token)
        {
            var startTime = DateTime.UtcNow;
            VerificationFailure lastFailure = new(new MergeDiagnosis.Failure.Other(), "");

            VerificationFailure AsFailure(Exception error) => error switch
            {
                VerificationFailure failure => failure,
                MergeFailedException merge => new VerificationFailure(
                    MergeDiagnosis.Classify(merge.Log, new MergeDiagnosis.Failure.Processing()), merge.Log),
                _ => new VerificationFailure(new MergeDiagnosis.Failure.Processing(), error.ToString())
            };

            // --- TIER 1: fast merge (direct copy) ---
            string concatList = context.Temp($"concat_list_{Guid.NewGuid():N}.txt");
            try
            {
                WriteConcatList(concatList, inputs);

                var args = new List<string> { "-f", "concat", "-safe", "0", "-i", concatList };
                // Keep the first file's metadata on the merged result.
                args.AddRange(new[] { "-i", inputs[0], "-map_metadata", "1" });
                // Conformed files carry only the main video and audio, so the
                // first file's extra tracks (timecode, data) cannot be relied on.
                args.AddRange(mapMainStreamsOnly
                    ? new[] { "-map", "0:v:0?", "-map", "0:a:0?" }
                    : new[] { "-map", "0" });
                args.AddRange(new[] { "-c", "copy", "-movflags", "+faststart", "-y", outputFile });

                Debug.WriteLine("Attempting Tier 1 (Direct Copy)...");
                await RunFFmpegWithProgressAsync(args, totalDuration, 0, startTime,
                    pct => reportProgress(pct, Localizer.Get("ModeFastMerge", Language)), token).ConfigureAwait(false);

                await VerifyMergedAsync(outputFile, totalDuration, context, reportProgress, token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                Debug.WriteLine("Tier 1 failed: " + error.Message);
                lastFailure = AsFailure(error);
                TryDelete(outputFile);
            }
            finally
            {
                TryDelete(concatList);
            }

            // --- TIER 2: rewrap merge (stream copy via MPEG-TS) ---
            // Fixes container timestamp problems without the cost of encoding.
            Debug.WriteLine("Attempting Tier 2 (Rewrap via TS)...");
            reportProgress(0, Localizer.Get("FastMergeFailedRewrap", Language));

            string firstCodec = context.Report?.Probes[0].Video?.Codec
                ?? (await ProbeAsync(new[] { inputs[0] }, token).ConfigureAwait(false))[0].Video?.Codec
                ?? "";
            string? bsf = firstCodec switch
            {
                "h264" => "h264_mp4toannexb",
                "hevc" => "hevc_mp4toannexb",
                _ => null
            };

            var tsFiles = new List<string>();
            try
            {
                double accumulated = 0;
                for (int i = 0; i < inputs.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    string tsPath = context.Temp($"part_{Guid.NewGuid():N}.ts");
                    tsFiles.Add(tsPath);

                    var args = new List<string> { "-i", inputs[i], "-map", "0:v:0?", "-map", "0:a:0?", "-c", "copy" };
                    if (bsf != null) args.AddRange(new[] { "-bsf:v", bsf });
                    args.AddRange(new[] { "-f", "mpegts", "-y", tsPath });

                    await RunFFmpegWithProgressAsync(args, totalDuration, accumulated, startTime,
                        pct => reportProgress(pct, Localizer.Get("FastMergeFailedRewrap", Language)), token)
                        .ConfigureAwait(false);
                    accumulated += context.Report?.Probes[i].Duration ?? 0;
                }

                var concatArgs = new List<string>
                {
                    "-i", "concat:" + string.Join("|", tsFiles),
                    "-c", "copy", "-movflags", "+faststart", "-y", outputFile
                };
                await RunFFmpegWithProgressAsync(concatArgs, 0, 0, startTime, _ => { }, token).ConfigureAwait(false);

                await VerifyMergedAsync(outputFile, totalDuration, context, reportProgress, token).ConfigureAwait(false);
                Debug.WriteLine("Tier 2 succeeded.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                Debug.WriteLine("Tier 2 failed: " + error.Message);
                TryDelete(outputFile);
                // Tier 1's diagnosis is the more telling one: Tier 2 mostly fails
                // for the same reason, just less legibly.
                throw lastFailure.Failure is MergeDiagnosis.Failure.Processing ? AsFailure(error) : lastFailure;
            }
            finally
            {
                foreach (string ts in tsFiles) TryDelete(ts);
            }
        }

        private static void WriteConcatList(string listPath, IReadOnlyList<string> inputs)
        {
            using var writer = new StreamWriter(listPath, append: false);
            foreach (string file in inputs)
            {
                // The concat demuxer takes single quotes; the only escape it
                // understands is closing and reopening the quoted run.
                writer.WriteLine($"file '{file.Replace("'", "'\\''")}'");
            }
        }

        // MARK: - Tier 3

        private async Task ReencodeAndConcatAsync(
            List<string> workingFiles, List<MediaProbe?> probes, string outputFile, int? targetHeight,
            bool fixJitter, bool hevc, bool normalize, double totalDuration, MergeContext context,
            Action<double, string> reportProgress, CancellationToken token)
        {
            var startTime = DateTime.UtcNow;
            var segments = new List<string>();

            // Every segment has to come out with the same audio layout, or the
            // concat below hits the same header mismatch the copy merge does.
            var audioTarget = context.Report?.AudioTarget;
            var videoTarget = context.Report?.VideoTarget;
            bool resolutionsDiffer = context.Report?.VideoDifferences
                .Any(d => d.Field == MergeCompatibility.Field.Resolution) ?? false;
            int segmentTimescale = MergeCompatibility.ReencodeTimescale(
                probes.Where(p => p?.VideoTimeBase != null).Select(p => p!.VideoTimeBase!));

            double completed = 0;
            for (int i = 0; i < workingFiles.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                string segment = context.Temp($"segment_{i}_{Guid.NewGuid():N}.mov");
                segments.Add(segment);

                double duration = probes[i]?.Duration ?? 0;
                string message = Localizer.GetDynamic("ProcessingSegment", Language,
                    (i + 1).ToString(), workingFiles.Count.ToString());

                double offset = completed;
                await EncodeSegmentAsync(workingFiles[i], segment, probes[i], targetHeight, fixJitter, hevc,
                    normalize, duration, audioTarget, resolutionsDiffer ? videoTarget : null, segmentTimescale,
                    totalDuration, offset, startTime,
                    pct => reportProgress(pct, message), token).ConfigureAwait(false);

                completed += duration;
            }

            reportProgress(100, Localizer.Get("FinalizingMergingSegments", Language));
            string concatList = context.Temp($"tier3_concat_{Guid.NewGuid():N}.txt");
            try
            {
                WriteConcatList(concatList, segments);
                var args = new List<string>
                {
                    "-f", "concat", "-safe", "0", "-i", concatList,
                    "-c", "copy", "-movflags", "+faststart", "-y", outputFile
                };
                await RunFFmpegWithProgressAsync(args, totalDuration, totalDuration - 1, startTime, _ => { }, token)
                    .ConfigureAwait(false);
            }
            finally
            {
                TryDelete(concatList);
            }

            // 10s tolerance: segments were re-encoded, so small timing shifts are expected.
            await VerifyOutputAsync(outputFile, totalDuration, 10.0, token).ConfigureAwait(false);
        }

        /// One re-encoded segment, keeping the source's own frame timing and a
        /// bit rate derived from the source rather than a fixed figure.
        private async Task EncodeSegmentAsync(
            string input, string output, MediaProbe? probe, int? targetHeight, bool fixJitter, bool hevc,
            bool normalize, double duration, AudioFormat? audioTarget, VideoFormat? padTarget, int timescale,
            double totalDuration, double offset, DateTime startTime,
            Action<double> reportProgress, CancellationToken token)
        {
            var args = new List<string>();

            bool padToTarget = targetHeight == null && padTarget != null;
            bool needsCpuFilters = fixJitter || targetHeight != null || padToTarget;

            // Hardware decode only when no CPU-side pixel filter is active; yadif
            // and scale need frames in CPU memory. "auto" falls back to software.
            if (!needsCpuFilters) args.AddRange(new[] { "-hwaccel", "auto" });

            args.AddRange(new[] { "-i", input });

            // A file without audio gets silence, so every segment has an audio track.
            bool addSilence = audioTarget != null && probe != null && probe.Audio == null;
            if (addSilence && audioTarget != null)
            {
                args.AddRange(new[]
                {
                    "-f", "lavfi", "-t", duration.ToString("F3", CultureInfo.InvariantCulture),
                    "-i", $"anullsrc=r={audioTarget.SampleRate}:cl={audioTarget.ChannelLayout}",
                    "-map", "0:v:0", "-map", "1:a:0"
                });
            }

            var videoFilters = new List<string>();
            if (fixJitter) videoFilters.Add("yadif=0:-1:0");
            if (targetHeight is { } height)
            {
                videoFilters.Add($"scale=-2:{height}:flags=lanczos");
            }
            else if (padToTarget && padTarget != null)
            {
                videoFilters.Add(
                    $"scale={padTarget.Width}:{padTarget.Height}:force_original_aspect_ratio=decrease:flags=lanczos," +
                    $"pad={padTarget.Width}:{padTarget.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1");
            }
            videoFilters.Add("format=yuv420p");
            args.AddRange(new[] { "-vf", string.Join(",", videoFilters) });

            var audioFilters = new List<string> { "aresample=48000" };
            if (normalize) audioFilters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
            // Last, so loudnorm's own output rate is brought back down as well.
            audioFilters.Add(audioTarget != null
                ? $"aformat=sample_rates=48000:channel_layouts={audioTarget.ChannelLayout}"
                : "aformat=sample_rates=48000");
            args.AddRange(new[] { "-af", string.Join(",", audioFilters) });

            // The source bit rate, adjusted for any change in size.
            var sourceSize = (probe?.Video?.Width ?? 0, probe?.Video?.Height ?? 0);
            (int Width, int Height) outputSize;
            if (targetHeight is { } h2)
            {
                outputSize = MergeCompatibility.ScaledSize(sourceSize.Item1, sourceSize.Item2, h2);
            }
            else if (padToTarget && padTarget != null)
            {
                outputSize = (padTarget.Width, padTarget.Height);
            }
            else
            {
                outputSize = sourceSize;
            }

            int bitRate = MergeCompatibility.ReencodeBitRate(probe?.VideoBitRate, sourceSize, outputSize,
                hevc ? 8_000_000 : 12_000_000);
            args.AddRange(EncoderArguments(hevc, bitRate));

            // Keep each source's own frame timing. One timescale that represents
            // all of them exactly keeps the concat demuxer from rescaling between
            // segments, which is what used to require forcing 30 fps.
            args.AddRange(new[] { "-fps_mode", "passthrough", "-video_track_timescale", timescale.ToString(CultureInfo.InvariantCulture) });
            if (duration > 0) args.AddRange(new[] { "-t", duration.ToString("F3", CultureInfo.InvariantCulture) });
            args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k", "-y", output });

            await RunFFmpegWithProgressAsync(args, totalDuration, offset, startTime, reportProgress, token)
                .ConfigureAwait(false);
        }

        /// Encoder, bit rate and the rate-control settings each encoder needs.
        private List<string> EncoderArguments(bool hevc, int bitRate)
        {
            string encoder = hevc ? HevcEncoder : H264Encoder;
            var args = new List<string> { "-c:v", encoder };

            string rate = bitRate.ToString(CultureInfo.InvariantCulture);
            string maxRate = ((long)(bitRate * 1.4)).ToString(CultureInfo.InvariantCulture);
            string bufSize = ((long)(bitRate * 1.8)).ToString(CultureInfo.InvariantCulture);

            args.AddRange(new[] { "-b:v", rate, "-maxrate", maxRate, "-bufsize", bufSize });

            if (encoder.StartsWith("lib", StringComparison.Ordinal))
            {
                // At a fixed bit rate "medium" is nearly identical in quality to
                // "slow" but about twice as fast.
                args.AddRange(new[] { "-preset", "medium" });
            }
            if (hevc) args.AddRange(new[] { "-tag:v", "hvc1" });

            return args;
        }

        /// A single file encoded with the chosen filters, keeping its own frame
        /// timing and a bit rate derived from the source.
        private async Task EncodeOneAsync(
            string input, string output, string resolution, MediaProbe? probe, bool fixJitter, bool hevc,
            bool normalize, double duration, int? forcedTimescale,
            Action<double> reportProgress, CancellationToken token)
        {
            int? targetHeight = resolution switch { "1080p" => 1080, "4K" => 2160, _ => null };
            int timescale = forcedTimescale ?? MergeCompatibility.ReencodeTimescale(
                probe?.VideoTimeBase is { } tb ? new[] { tb } : Array.Empty<string>());

            await EncodeSegmentAsync(input, output, probe, targetHeight, fixJitter, hevc, normalize, duration,
                null, null, timescale, duration > 0 ? duration : 1, 0, DateTime.UtcNow, reportProgress, token)
                .ConfigureAwait(false);
        }

        // MARK: - Stabilization

        private async Task<string> StabilizeAsync(
            string input, MediaProbe? probe, double duration, int smoothing, bool hevc, MergeContext context,
            int clip, int totalClips, Action<double, string> reportProgress, CancellationToken token)
        {
            string trfPath = context.Temp($"transform_{Guid.NewGuid():N}.trf");
            string output = context.Temp($"stabilized_{Guid.NewGuid():N}.mov");

            try
            {
                // Pass 1: analyse the shake.
                string detectMessage = Localizer.GetDynamic("StabilizePass1", Language, clip.ToString(), totalClips.ToString());
                await RunFFmpegWithProgressAsync(new List<string>
                {
                    "-i", input,
                    "-vf", $"vidstabdetect=shakiness=5:accuracy=15:result={EscapeFilterPath(trfPath)}",
                    "-f", "null", "-"
                }, duration, 0, DateTime.UtcNow, pct => reportProgress(pct * 0.3, detectMessage), token)
                    .ConfigureAwait(false);

                // Pass 2: apply it, at the source's own size and bit rate.
                var size = (probe?.Video?.Width ?? 0, probe?.Video?.Height ?? 0);
                int bitRate = MergeCompatibility.ReencodeBitRate(probe?.VideoBitRate, size, size, 20_000_000);
                int timescale = MergeCompatibility.ReencodeTimescale(
                    probe?.VideoTimeBase is { } tb ? new[] { tb } : Array.Empty<string>());

                var args = new List<string>
                {
                    "-i", input,
                    "-vf", $"vidstabtransform=input={EscapeFilterPath(trfPath)}:smoothing={smoothing}:crop=black:optzoom=1"
                };
                args.AddRange(EncoderArguments(hevc, bitRate));
                args.AddRange(new[]
                {
                    "-fps_mode", "passthrough",
                    "-video_track_timescale", timescale.ToString(CultureInfo.InvariantCulture)
                });
                if (duration > 0) args.AddRange(new[] { "-t", duration.ToString("F3", CultureInfo.InvariantCulture) });
                args.AddRange(new[] { "-c:a", "copy", "-y", output });

                string applyMessage = Localizer.GetDynamic("StabilizePass2", Language, clip.ToString(), totalClips.ToString());
                await RunFFmpegWithProgressAsync(args, duration, 0, DateTime.UtcNow,
                    pct => reportProgress(30.0 + pct * 0.7, applyMessage), token).ConfigureAwait(false);

                return output;
            }
            finally
            {
                TryDelete(trfPath);
            }
        }

        /// vidstab takes the transform file inside a filter string, where a
        /// Windows path's backslashes and drive colon would be read as escapes.
        private static string EscapeFilterPath(string path) =>
            path.Replace("\\", "/").Replace(":", "\\:");

        // MARK: - Verification

        /// Decodes the tail and compares the container duration: a header that
        /// does not match the packets after a join leaves the tail undecodable.
        private async Task VerifyOutputAsync(string path, double? expectedDuration, double tolerance, CancellationToken token)
        {
            string ffmpegPath = ResolveToolPath("ffmpeg");
            var tail = await ProcessRunner.CaptureAsync(ffmpegPath,
                new[] { "-v", "error", "-sseof", "-1", "-i", path, "-f", "null", "-" }, token).ConfigureAwait(false);

            string findings = VideoIntegrityChecker.MeaningfulDiagnostics(tail.StdErr);
            if (tail.ExitCode != 0 || findings.ToLowerInvariant().Contains("error") || findings.Contains("Invalid"))
            {
                throw new VerificationFailure(MergeDiagnosis.Classify(findings), tail.StdErr);
            }

            if (expectedDuration is not { } expected) return;

            string ffprobePath = ResolveToolPath("ffprobe");
            var result = await ProcessRunner.CaptureAsync(ffprobePath, new[]
            {
                "-v", "error", "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1", path
            }, token).ConfigureAwait(false);

            if (double.TryParse(result.StdOut.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double actual))
            {
                double difference = Math.Abs(actual - expected);
                if (difference > tolerance)
                {
                    throw new VerificationFailure(
                        new MergeDiagnosis.Failure.VideoDuration(expected, actual),
                        $"Duration mismatch: expected {expected:F2}s, got {actual:F2}s (difference {difference:F2}s).");
                }
            }
        }

        /// VerifyOutput plus what a copy merge specifically gets wrong: each
        /// track's own length (a time base mismatch speeds clips up while the
        /// container duration still looks right) and decoding across every join.
        private async Task VerifyMergedAsync(
            string path, double totalDuration, MergeContext context,
            Action<double, string> reportProgress, CancellationToken token)
        {
            reportProgress(100, Localizer.Get("VerifyingMergedFile", Language));
            await VerifyOutputAsync(path, totalDuration, 5.0, token).ConfigureAwait(false);

            if (context.Report is not { } report) return;

            var output = (await ProbeAsync(new[] { path }, token).ConfigureAwait(false))[0];
            double tolerance = 0.5 + 0.1 * report.Probes.Count;

            var videoDurations = report.Probes.Where(p => p.Video != null).Select(p => p.VideoDuration).ToList();
            if (videoDurations.Count > 0 && videoDurations.All(d => d != null) && output.VideoDuration is { } actualVideo)
            {
                double expected = videoDurations.Sum(d => d ?? 0);
                if (Math.Abs(actualVideo - expected) > tolerance)
                {
                    throw new VerificationFailure(
                        new MergeDiagnosis.Failure.VideoDuration(expected, actualVideo),
                        $"Video track is {actualVideo:F2}s, inputs add up to {expected:F2}s.");
                }
            }

            if (report.AudioTarget != null && output.AudioDuration is { } actualAudio)
            {
                // Silence added to a file without audio spans its video.
                var lengths = report.Probes.Select(p => p.AudioDuration ?? p.VideoDuration).ToList();
                if (lengths.All(d => d != null))
                {
                    double expected = lengths.Sum(d => d ?? 0);
                    if (Math.Abs(actualAudio - expected) > tolerance)
                    {
                        throw new VerificationFailure(
                            new MergeDiagnosis.Failure.AudioDuration(expected, actualAudio),
                            $"Audio track is {actualAudio:F2}s, inputs add up to {expected:F2}s.");
                    }
                }
            }

            // Two seconds around each join is where mismatched streams break.
            string ffmpegPath = ResolveToolPath("ffmpeg");
            double boundary = 0;
            for (int i = 0; i < report.Probes.Count - 1; i++)
            {
                token.ThrowIfCancellationRequested();
                boundary += report.Probes[i].Duration;
                var window = await ProcessRunner.CaptureAsync(ffmpegPath, new[]
                {
                    "-v", "error",
                    "-ss", Math.Max(boundary - 1, 0).ToString("F3", CultureInfo.InvariantCulture),
                    "-t", "2", "-i", path, "-f", "null", "-"
                }, token).ConfigureAwait(false);

                string findings = VideoIntegrityChecker.MeaningfulDiagnostics(window.StdErr);
                if (window.ExitCode != 0 || findings.Length > 0)
                {
                    throw new VerificationFailure(
                        MergeDiagnosis.Classify(findings),
                        $"Decoding around {boundary:F2}s:\n{window.StdErr}");
                }
            }
        }

        // MARK: - AI upscaling (Windows only; the Mac build has no Real-ESRGAN)

        private async Task RunAiUpscaleAsync(string input, string output, string resolution,
            Action<double> reportProgress, CancellationToken token)
        {
            string modelName = resolution == "AI_Anime" ? "realesrgan-x4plus-anime" : "realesrgan-x4plus";
            bool keepResolution = resolution == "AI_1x";
            var service = new UpscaleService(ResolveToolPath("ffmpeg"), ResolveToolPath("realesrgan-ncnn-vulkan"));
            await service.UpscaleVideoAsync(input, output, modelName, keepResolution,
                (pct, _) => reportProgress(pct), token).ConfigureAwait(false);
        }

        // MARK: - Running ffmpeg

        private async Task RunFFmpegWithProgressAsync(
            List<string> arguments, double totalDuration, double offsetDuration, DateTime startTime,
            Action<double> reportProgress, CancellationToken token)
        {
            string ffmpegPath = ResolveToolPath("ffmpeg");
            var timePattern = new Regex(@"time=(\d{2}):(\d{2}):(\d{2}\.\d{2})", RegexOptions.Compiled);

            void OnLine(string line)
            {
                if (totalDuration <= 0) return;
                var match = timePattern.Match(line);
                if (!match.Success) return;

                double seconds = int.Parse(match.Groups[1].Value) * 3600
                               + int.Parse(match.Groups[2].Value) * 60
                               + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                double processed = offsetDuration + seconds;
                reportProgress(Math.Clamp(processed / totalDuration * 100.0, 0, 100));
            }

            var result = await ProcessRunner.CaptureAsync(ffmpegPath, arguments, token, onStdErrLine: OnLine)
                .ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new MergeFailedException(
                    Localizer.Get("FfmpegCouldNotProcess", Language),
                    result.StdErr);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private string ResolveToolPath(string toolName) => AppPaths.ResolveTool(toolName);
    }
}
