using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Mp4Merger.Core
{
    // Stream copy only works when every input describes its streams the same way:
    // the merged file keeps the first input's headers, so a 5.1 header followed by
    // stereo packets leaves the AAC decoder rejecting every packet after the join.
    // This file decides what has to be conformed before a copy merge can succeed.
    public static class MergeCompatibility
    {
        public enum Field
        {
            AudioCodec, AudioProfile, SampleRate, Channels, MissingAudio,
            VideoCodec, VideoProfile, Resolution, PixelFormat, FrameRate
        }

        public sealed record Difference(int Index, string FileName, Field Field, string Actual, string Expected);

        public sealed class Report
        {
            public IReadOnlyList<MediaProbe> Probes { get; init; } = Array.Empty<MediaProbe>();
            /// null when no input carries audio, so there is nothing to conform.
            public AudioFormat? AudioTarget { get; init; }
            /// What every video stream has to look like; null when no input has video.
            public VideoFormat? VideoTarget { get; init; }
            public IReadOnlyList<Difference> AudioDifferences { get; init; } = Array.Empty<Difference>();
            /// Codec, profile, resolution or pixel format — copy merging cannot work.
            public IReadOnlyList<Difference> VideoDifferences { get; init; } = Array.Empty<Difference>();
            /// Frame rate alone, which copy merging often survives.
            public IReadOnlyList<Difference> FrameRateDifferences { get; init; } = Array.Empty<Difference>();
            /// Set when video time bases differ: the concat demuxer does not rescale
            /// between them, so later clips play back too fast or too slow.
            public int? CommonTimescale { get; init; }

            /// Timescale every conformed file is written with, and whether the time
            /// bases differ enough that every input has to be remuxed to it.
            /// Re-encoded video takes the target frame rate's numerator as its base,
            /// so that joins the set as well.
            public (int Timescale, bool RemuxAll) PlanTimescale(bool reencodingVideo)
            {
                var bases = Probes.Where(p => p.VideoTimeBase != null).Select(p => p.VideoTimeBase!).ToList();
                if (reencodingVideo && VideoTarget != null)
                {
                    string numerator = VideoTarget.FrameRate.Split('/').FirstOrDefault() ?? "";
                    if (numerator.Length > 0) bases.Add($"1/{numerator}");
                }
                return (MergeCompatibility.CommonTimescaleFor(bases), bases.Distinct().Count() > 1);
            }

            public IReadOnlyList<int> FilesNeedingAudio => Indices(AudioDifferences);
            public IReadOnlyList<int> FilesNeedingVideo => Indices(VideoDifferences);
            public IReadOnlyList<int> FilesWithOtherFrameRate => Indices(FrameRateDifferences);
            public bool NeedsConforming =>
                AudioDifferences.Count > 0 || VideoDifferences.Count > 0 || CommonTimescale != null;

            private static IReadOnlyList<int> Indices(IReadOnlyList<Difference> differences) =>
                differences.Select(d => d.Index).Distinct().OrderBy(i => i).ToList();
        }

        public static readonly AudioFormat DefaultAudio = new("aac", "LC", 48000, 2, "stereo");

        // MARK: - Analysis

        public static Report Analyze(IReadOnlyList<MediaProbe> probes)
        {
            var audioTarget = AudioTargetFor(probes.Select(p => p.Audio).ToList());
            var videoTarget = VideoTargetFor(probes.Select(p => p.Video).ToList());

            var audioDifferences = new List<Difference>();
            var videoDifferences = new List<Difference>();
            var frameRateDifferences = new List<Difference>();

            for (int index = 0; index < probes.Count; index++)
            {
                var probe = probes[index];
                string name = probe.FileName;
                void Note(Field field, string actual, string expected, List<Difference> into) =>
                    into.Add(new Difference(index, name, field, actual, expected));

                if (audioTarget != null)
                {
                    var audio = probe.Audio;
                    if (audio != null)
                    {
                        if (audio.Codec != audioTarget.Codec)
                            Note(Field.AudioCodec, audio.Codec, audioTarget.Codec, audioDifferences);
                        if (audio.Profile != audioTarget.Profile)
                            Note(Field.AudioProfile, audio.Profile, audioTarget.Profile, audioDifferences);
                        if (audio.SampleRate != audioTarget.SampleRate)
                            Note(Field.SampleRate, audio.SampleRate.ToString(), audioTarget.SampleRate.ToString(), audioDifferences);
                        if (audio.Channels != audioTarget.Channels || audio.ChannelLayout != audioTarget.ChannelLayout)
                            Note(Field.Channels, audio.ChannelLayout, audioTarget.ChannelLayout, audioDifferences);
                    }
                    else
                    {
                        Note(Field.MissingAudio, "", audioTarget.ChannelLayout, audioDifferences);
                    }
                }

                if (videoTarget != null && probe.Video != null)
                {
                    var video = probe.Video;
                    if (video.Codec != videoTarget.Codec)
                        Note(Field.VideoCodec, video.Codec, videoTarget.Codec, videoDifferences);
                    if (video.Profile != videoTarget.Profile)
                        Note(Field.VideoProfile, video.Profile, videoTarget.Profile, videoDifferences);
                    if (video.Width != videoTarget.Width || video.Height != videoTarget.Height)
                        Note(Field.Resolution, $"{video.Width}x{video.Height}", $"{videoTarget.Width}x{videoTarget.Height}", videoDifferences);
                    if (video.PixelFormat != videoTarget.PixelFormat)
                        Note(Field.PixelFormat, video.PixelFormat, videoTarget.PixelFormat, videoDifferences);
                    if (video.FrameRate != videoTarget.FrameRate)
                        Note(Field.FrameRate, video.FrameRate, videoTarget.FrameRate, frameRateDifferences);
                }
            }

            var timeBases = probes.Where(p => p.VideoTimeBase != null).Select(p => p.VideoTimeBase!).ToList();
            int? timescale = timeBases.Distinct().Count() > 1 ? CommonTimescaleFor(timeBases) : null;

            return new Report
            {
                Probes = probes,
                AudioTarget = audioTarget,
                VideoTarget = videoTarget,
                AudioDifferences = audioDifferences,
                VideoDifferences = videoDifferences,
                FrameRateDifferences = frameRateDifferences,
                CommonTimescale = timescale
            };
        }

        /// The majority format if one exists and we can encode it, otherwise AAC-LC
        /// 48 kHz stereo. Mixed channel counts always end up stereo: downmixing 5.1
        /// is safe, while upmixing stereo would only invent empty channels.
        public static AudioFormat? AudioTargetFor(IReadOnlyList<AudioFormat?> formats)
        {
            var present = formats.Where(f => f != null).Select(f => f!).ToList();
            if (present.Count == 0) return null;

            var majority = Majority(present);
            var target = (majority != null && CanEncode(majority)) ? majority : DefaultAudio;

            if (present.Select(a => a.Channels).Distinct().Count() > 1 && target.Channels != 2)
            {
                target = target with { Channels = 2, ChannelLayout = "stereo" };
            }
            return target;
        }

        /// The majority picture format, falling back to the first file. If that is
        /// something the available encoders cannot produce, conform to H.264 High.
        public static VideoFormat? VideoTargetFor(IReadOnlyList<VideoFormat?> formats)
        {
            var present = formats.Where(f => f != null).Select(f => f!).ToList();
            if (present.Count == 0) return null;
            var first = present[0];

            static VideoFormat PictureKey(VideoFormat f) => f with { FrameRate = "" };

            var target = Majority(present.Select(PictureKey).ToList()) ?? PictureKey(first);
            target = target with { FrameRate = Majority(present.Select(f => f.FrameRate).ToList()) ?? first.FrameRate };

            if (GetEncoderSettings(target, "libx264", "libx265") == null)
            {
                target = target with { Codec = "h264", Profile = "High", PixelFormat = "yuv420p" };
            }
            return target;
        }

        public static bool CanEncode(AudioFormat format) =>
            format.Codec == "aac" && format.Profile == "LC" && format.SampleRate > 0 && format.Channels > 0;

        public sealed record EncoderSettings(string Encoder, string Profile, string PixelFormat);

        /// Encoder, `-profile:v` value and input pixel format that reproduce
        /// `format`, or null if the available encoders cannot.
        public static EncoderSettings? GetEncoderSettings(VideoFormat format, string h264Encoder, string hevcEncoder)
        {
            bool software(string enc) => enc.StartsWith("lib", StringComparison.OrdinalIgnoreCase);

            return (format.Codec, format.Profile, format.PixelFormat) switch
            {
                ("h264", "High", "yuv420p") => new EncoderSettings(h264Encoder, "high", "yuv420p"),
                ("h264", "Main", "yuv420p") => new EncoderSettings(h264Encoder, "main", "yuv420p"),
                ("h264", "Constrained Baseline", "yuv420p") => new EncoderSettings(h264Encoder, "baseline", "yuv420p"),
                ("h264", "Baseline", "yuv420p") => new EncoderSettings(h264Encoder, "baseline", "yuv420p"),
                ("hevc", "Main", "yuv420p") => new EncoderSettings(hevcEncoder, "main", "yuv420p"),
                // Hardware encoders take 10-bit frames as p010le; x265 wants the planar form.
                ("hevc", "Main 10", "yuv420p10le") =>
                    new EncoderSettings(hevcEncoder, "main10", software(hevcEncoder) ? "yuv420p10le" : "p010le"),
                _ => null
            };
        }

        /// Bit rate for re-encoding `probe` at the target size: the source rate,
        /// scaled with the pixel count so a 720p clip blown up to 1080p is not starved.
        public static int VideoBitRateFor(MediaProbe probe, VideoFormat target) =>
            ReencodeBitRate(probe.VideoBitRate,
                            (probe.Video?.Width ?? 0, probe.Video?.Height ?? 0),
                            (target.Width, target.Height),
                            12_000_000);

        /// Bit rate for re-encoding a stream of `source` bps at a new size: the
        /// source rate, scaled with the edge length rather than the pixel count.
        /// An upscale adds pixels but no detail, so 1080p → 4K doubles the rate
        /// instead of quadrupling it; a downscale saves bits the same way.
        public static int ReencodeBitRate(int? source, (int Width, int Height) from, (int Width, int Height) to, int fallback)
        {
            if (source is not > 0 || from.Width <= 0 || from.Height <= 0 || to.Width <= 0 || to.Height <= 0)
            {
                return fallback;
            }
            double edgeRatio = Math.Sqrt((double)(to.Width * to.Height) / (from.Width * from.Height));
            return Math.Min(Math.Max((int)(source.Value * edgeRatio), 1_000_000), 100_000_000);
        }

        /// Size after `scale=-2:height`, which keeps the aspect ratio and rounds
        /// the width to an even number.
        public static (int Width, int Height) ScaledSize(int width, int height, int toHeight)
        {
            if (width <= 0 || height <= 0) return (0, toHeight);
            int scaledWidth = (int)Math.Round((double)width * toHeight / height / 2, MidpointRounding.AwayFromZero) * 2;
            return (scaledWidth, toHeight);
        }

        /// Timescale for re-encoded output that represents every input's frame
        /// timing exactly, so keeping the source frame rate loses nothing to
        /// rounding. 90 kHz is always included: it suits common rates and is what
        /// re-encoded segments used before.
        public static int ReencodeTimescale(IEnumerable<string> timeBases) =>
            CommonTimescaleFor(timeBases.Concat(new[] { "1/90000" }));

        public static string AudioBitRateFor(AudioFormat format) => format.Channels switch
        {
            <= 1 => "128k",
            2 => "192k",
            _ => "384k"
        };

        /// Smallest timescale that represents every input exactly, so remuxing is
        /// lossless; 90 kHz when that would get unreasonably large.
        public static int CommonTimescaleFor(IEnumerable<string> timeBases)
        {
            var denominators = new List<int>();
            foreach (string b in timeBases)
            {
                var parts = b.Split('/');
                if (parts.Length == 2 && int.TryParse(parts[1], out int den) && den > 0) denominators.Add(den);
            }
            if (denominators.Count == 0) return 90000;

            long result = 1;
            foreach (int den in denominators)
            {
                result = result / Gcd(result, den) * den;
                if (result > 1_000_000) return 90000;
            }
            return (int)result;
        }

        /// ffmpeg arguments that rewrite one input into a file the copy merge can
        /// join. Video is stream-copied unless `reencodeVideo`; the user's file is
        /// only ever read.
        public static List<string> ConformArguments(
            MediaProbe probe,
            AudioFormat? audioTarget,
            VideoFormat? videoTarget,
            bool reencodeVideo,
            bool conformAudio,
            int timescale,
            string h264Encoder,
            string hevcEncoder,
            string output)
        {
            var args = new List<string> { "-i", probe.Path };
            bool addSilence = audioTarget != null && probe.Audio == null;
            if (addSilence && audioTarget != null)
            {
                double length = probe.VideoDuration ?? probe.Duration;
                args.AddRange(new[]
                {
                    "-f", "lavfi", "-t", length.ToString("F3", CultureInfo.InvariantCulture),
                    "-i", $"anullsrc=r={audioTarget.SampleRate}:{LayoutOption(audioTarget, "cl")}"
                });
            }

            args.AddRange(new[] { "-map", "0:v:0?" });
            if (audioTarget != null) args.AddRange(new[] { "-map", addSilence ? "1:a:0" : "0:a:0" });

            EncoderSettings? encoder = videoTarget == null
                ? null
                : GetEncoderSettings(videoTarget, h264Encoder, hevcEncoder);

            if (reencodeVideo && videoTarget != null && encoder != null)
            {
                int w = videoTarget.Width, h = videoTarget.Height;
                args.AddRange(new[]
                {
                    "-vf", $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1,format={encoder.PixelFormat}",
                    "-c:v", encoder.Encoder,
                    "-profile:v", encoder.Profile,
                    "-b:v", VideoBitRateFor(probe, videoTarget).ToString(CultureInfo.InvariantCulture),
                    "-r", videoTarget.FrameRate
                });
                if (encoder.Encoder.Contains("hevc") || encoder.Encoder.Contains("265"))
                {
                    args.AddRange(new[] { "-tag:v", "hvc1" });
                }
            }
            else
            {
                args.AddRange(new[] { "-c:v", "copy" });
            }

            if (audioTarget != null)
            {
                if (conformAudio || addSilence)
                {
                    args.AddRange(new[]
                    {
                        "-af", $"aformat=sample_rates={audioTarget.SampleRate}:{LayoutOption(audioTarget, "channel_layouts")}",
                        "-c:a", "aac", "-profile:a", "aac_low",
                        "-b:a", AudioBitRateFor(audioTarget)
                    });
                }
                else
                {
                    args.AddRange(new[] { "-c:a", "copy" });
                }
            }

            args.AddRange(new[]
            {
                "-video_track_timescale", timescale.ToString(CultureInfo.InvariantCulture),
                "-map_metadata", "0", "-y", output
            });
            return args;
        }

        /// `key=layout`, or a bare channel count when ffprobe gave no usable layout name.
        private static string LayoutOption(AudioFormat format, string key)
        {
            if (format.ChannelLayout.EndsWith(" channels", StringComparison.Ordinal))
            {
                return key == "cl" ? $"cl={format.Channels}c" : $"channel_layouts={format.Channels}c";
            }
            return $"{key}={format.ChannelLayout}";
        }

        public static string NormalizedLayout(string layout, int channels)
        {
            if (!string.IsNullOrEmpty(layout) && layout != "unknown") return layout;
            return channels switch
            {
                1 => "mono",
                2 => "stereo",
                6 => "5.1",
                _ => $"{channels} channels"
            };
        }

        private static T? Majority<T>(IReadOnlyList<T> values) where T : class
        {
            var counts = new Dictionary<T, int>();
            foreach (var value in values)
            {
                counts.TryGetValue(value, out int n);
                counts[value] = n + 1;
            }
            foreach (var pair in counts)
            {
                if (pair.Value * 2 > values.Count) return pair.Key;
            }
            return null;
        }

        private static long Gcd(long a, long b) => b == 0 ? a : Gcd(b, a % b);
    }
}
