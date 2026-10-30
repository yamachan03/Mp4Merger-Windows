using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Mp4Merger.Core
{
    /// The first audio stream's format — what a copy merge has to agree on.
    public sealed record AudioFormat(string Codec, string Profile, int SampleRate, int Channels, string ChannelLayout);

    /// The first video stream's picture format.
    public sealed record VideoFormat(string Codec, string Profile, int Width, int Height, string PixelFormat, string FrameRate);

    /// The first video and first audio stream of a file — the ones a merge maps.
    public sealed class MediaProbe
    {
        public string Path { get; init; } = "";
        public string FileName => System.IO.Path.GetFileName(Path);
        public VideoFormat? Video { get; set; }
        public AudioFormat? Audio { get; set; }
        /// `time_base` as ffprobe prints it, e.g. "1/30000".
        public string? VideoTimeBase { get; set; }
        public int? VideoBitRate { get; set; }
        public double? VideoDuration { get; set; }
        public double? AudioDuration { get; set; }
        public double Duration { get; set; }

        /// ffprobe arguments whose JSON output `Parse` understands.
        public static string[] FfprobeArguments(string path) => new[]
        {
            "-v", "error",
            "-show_entries",
            "stream=codec_type,codec_name,profile,width,height,pix_fmt,r_frame_rate,time_base,bit_rate,duration,sample_rate,channels,channel_layout:stream_disposition=attached_pic:format=duration,bit_rate",
            "-of", "json",
            path
        };

        public static MediaProbe? Parse(string json, string path)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch { return null; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                JsonElement? format = root.TryGetProperty("format", out var f) ? f : null;
                var probe = new MediaProbe
                {
                    Path = path,
                    Duration = (format.HasValue ? Number(format.Value, "duration") : null) ?? 0
                };

                if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
                {
                    foreach (var stream in streams.EnumerateArray())
                    {
                        string type = Str(stream, "codec_type");
                        if (type == "video" && probe.Video == null)
                        {
                            // Cover art shows up as a one-frame video stream.
                            if (stream.TryGetProperty("disposition", out var disp) &&
                                disp.ValueKind == JsonValueKind.Object &&
                                Number(disp, "attached_pic") == 1)
                            {
                                continue;
                            }

                            probe.Video = new VideoFormat(
                                Str(stream, "codec_name"),
                                Str(stream, "profile"),
                                (int)(Number(stream, "width") ?? 0),
                                (int)(Number(stream, "height") ?? 0),
                                Str(stream, "pix_fmt"),
                                Str(stream, "r_frame_rate"));
                            probe.VideoTimeBase = Str(stream, "time_base");
                            var vRate = Number(stream, "bit_rate");
                            probe.VideoBitRate = vRate.HasValue ? (int)vRate.Value : null;
                            probe.VideoDuration = Number(stream, "duration");
                        }
                        else if (type == "audio" && probe.Audio == null)
                        {
                            int channels = (int)(Number(stream, "channels") ?? 0);
                            probe.Audio = new AudioFormat(
                                Str(stream, "codec_name"),
                                Str(stream, "profile"),
                                (int)(Number(stream, "sample_rate") ?? 0),
                                channels,
                                MergeCompatibility.NormalizedLayout(Str(stream, "channel_layout"), channels));
                            probe.AudioDuration = Number(stream, "duration");
                        }
                    }
                }

                if (probe.VideoBitRate == null && format.HasValue)
                {
                    // Without a per-stream figure, the container rate minus a typical audio track is close enough.
                    var total = Number(format.Value, "bit_rate");
                    if (total.HasValue)
                    {
                        probe.VideoBitRate = Math.Max((int)total.Value - (probe.Audio == null ? 0 : 192_000), 0);
                    }
                }

                return probe;
            }
        }

        private static string Str(JsonElement obj, string key) =>
            obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        // ffprobe's JSON writer prints most numbers as strings, but not all.
        private static double? Number(JsonElement obj, string key)
        {
            if (!obj.TryGetProperty(key, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            {
                return d;
            }
            return null;
        }
    }
}
