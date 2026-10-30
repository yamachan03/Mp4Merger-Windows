using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Mp4Merger.Core
{
    /// Turns a failed merge into something the user can act on, in terms of
    /// their files rather than ffmpeg's log.
    public static class MergeDiagnosis
    {
        /// What went wrong in a check of the merged file.
        public abstract record Failure
        {
            public sealed record AudioDecode : Failure;
            public sealed record VideoDecode : Failure;
            public sealed record VideoDuration(double Expected, double Actual) : Failure;
            public sealed record AudioDuration(double Expected, double Actual) : Failure;
            public sealed record Other : Failure;
            /// ffmpeg itself failed before there was a merged file to check.
            public sealed record Processing : Failure;
        }

        public static Failure Classify(string log, Failure? fallback = null)
        {
            string lower = log.ToLowerInvariant();
            string[] audioMarkers = { "[aac", "dec:aac", "aist#", "channel element", "[mp3", "[ac3", "[opus" };
            string[] videoMarkers = { "[h264", "[hevc", "dec:h264", "dec:hevc", "vist#", "invalid nal" };
            if (audioMarkers.Any(lower.Contains)) return new Failure.AudioDecode();
            if (videoMarkers.Any(lower.Contains)) return new Failure.VideoDecode();
            return fallback ?? new Failure.Other();
        }

        /// One line per input, written at the top of the log so it can be read
        /// without running ffprobe by hand.
        public static string Summary(IReadOnlyList<MediaProbe> probes)
        {
            return string.Join("\n", probes.Select((probe, index) =>
            {
                var parts = new List<string>();
                if (probe.Video is { } v)
                {
                    parts.Add($"video {v.Codec} {v.Profile} {v.Width}x{v.Height} {v.PixelFormat} " +
                              $"{FormatFrameRate(v.FrameRate)} tb={probe.VideoTimeBase ?? "?"}");
                }
                if (probe.Audio is { } a)
                {
                    parts.Add($"audio {a.Codec} {a.Profile} {a.SampleRate}Hz {a.ChannelLayout}");
                }
                else
                {
                    parts.Add("no audio");
                }
                return $"#{index + 1} {probe.FileName}: " + string.Join(" / ", parts);
            }));
        }

        public static string Ordinal(int index, string lang) =>
            Localizer.GetDynamic("FileNumber", lang, (index + 1).ToString());

        public static string Describe(MergeCompatibility.Difference difference, string lang)
        {
            string label;
            switch (difference.Field)
            {
                case MergeCompatibility.Field.AudioCodec: label = Localizer.Get("FieldAudioCodec", lang); break;
                case MergeCompatibility.Field.AudioProfile: label = Localizer.Get("FieldAudioProfile", lang); break;
                case MergeCompatibility.Field.SampleRate: label = Localizer.Get("FieldSampleRate", lang); break;
                case MergeCompatibility.Field.Channels: label = Localizer.Get("FieldAudioChannels", lang); break;
                case MergeCompatibility.Field.MissingAudio:
                    return Localizer.GetDynamic("DiffNoAudioTrack", lang, Ordinal(difference.Index, lang), difference.FileName);
                case MergeCompatibility.Field.VideoCodec: label = Localizer.Get("FieldVideoCodec", lang); break;
                case MergeCompatibility.Field.VideoProfile: label = Localizer.Get("FieldVideoProfile", lang); break;
                case MergeCompatibility.Field.Resolution: label = Localizer.Get("FieldResolution", lang); break;
                case MergeCompatibility.Field.PixelFormat: label = Localizer.Get("FieldPixelFormat", lang); break;
                case MergeCompatibility.Field.FrameRate: label = Localizer.Get("FieldFrameRate", lang); break;
                default: label = difference.Field.ToString(); break;
            }

            return Localizer.GetDynamic("DiffLine", lang,
                Ordinal(difference.Index, lang),
                difference.FileName,
                label,
                DisplayValue(difference.Actual, difference.Field, lang),
                DisplayValue(difference.Expected, difference.Field, lang));
        }

        public static string DisplayValue(string raw, MergeCompatibility.Field field, string lang)
        {
            switch (field)
            {
                case MergeCompatibility.Field.Channels:
                case MergeCompatibility.Field.MissingAudio:
                    if (raw == "mono") return Localizer.Get("ChannelMono", lang);
                    if (raw == "stereo") return Localizer.Get("ChannelStereo", lang);
                    if (raw.StartsWith("5.1", StringComparison.Ordinal)) return "5.1ch";
                    if (raw.StartsWith("7.1", StringComparison.Ordinal)) return "7.1ch";
                    return raw;

                case MergeCompatibility.Field.SampleRate:
                    if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double hz)) return raw;
                    return (hz % 1000 == 0)
                        ? (hz / 1000).ToString("F0", CultureInfo.InvariantCulture) + "kHz"
                        : (hz / 1000).ToString("F1", CultureInfo.InvariantCulture) + "kHz";

                case MergeCompatibility.Field.FrameRate:
                    return FormatFrameRate(raw);

                default:
                    return raw;
            }
        }

        public static string FormatFrameRate(string raw)
        {
            var parts = raw.Split('/');
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den) ||
                den <= 0)
            {
                return raw;
            }
            // 29.97 and 23.976 are how people know these rates; plain rounding would hide them.
            string text = (num / den).ToString("F3", CultureInfo.InvariantCulture);
            text = text.TrimEnd('0').TrimEnd('.');
            return text + "fps";
        }

        public static string Describe(IReadOnlyList<MergeCompatibility.Difference> differences, string lang) =>
            string.Join("\n", differences.Select(d => "• " + Describe(d, lang)));

        /// Why a merge will take noticeably longer than a straight copy, for the
        /// "warn on slow process" prompt. Empty when the files join as they are.
        public static List<string> SlowMergeReasons(MergeCompatibility.Report report, string lang)
        {
            var reasons = new List<string>();
            if (report.VideoDifferences.Count > 0)
            {
                reasons.Add(Localizer.Get("SlowReasonFormat", lang) + "\n" + Describe(report.VideoDifferences, lang));
            }
            if (report.AudioDifferences.Count > 0)
            {
                reasons.Add(Localizer.Get("SlowReasonAudio", lang) + "\n" + Describe(report.AudioDifferences, lang));
            }
            return reasons;
        }

        /// One message naming the files and properties behind a failed merge.
        public static string Explain(Failure failure, MergeCompatibility.Report? report, string lang, bool includeLogHint = true)
        {
            var lines = new List<string>();
            switch (failure)
            {
                case Failure.AudioDecode:
                    lines.Add(Localizer.Get("MergedAudioUndecodable", lang));
                    if (report != null && report.AudioDifferences.Count > 0)
                    {
                        lines.Add(Localizer.Get("AudioFormatDiffers", lang));
                        lines.Add(Describe(report.AudioDifferences, lang));
                    }
                    break;

                case Failure.VideoDecode:
                    lines.Add(Localizer.Get("MergedVideoUndecodable", lang));
                    if (report != null)
                    {
                        var all = report.VideoDifferences.Concat(report.FrameRateDifferences).ToList();
                        if (all.Count > 0)
                        {
                            lines.Add(Localizer.Get("VideoFormatDiffers", lang));
                            lines.Add(Describe(all, lang));
                        }
                    }
                    break;

                case Failure.VideoDuration vd:
                    lines.Add(Localizer.GetDynamic("MergedVideoDurationOff", lang,
                        vd.Actual.ToString("F1", CultureInfo.InvariantCulture),
                        vd.Expected.ToString("F1", CultureInfo.InvariantCulture)));
                    if (report != null && report.FrameRateDifferences.Count > 0)
                    {
                        lines.Add(Localizer.Get("FrameRateDiffers", lang));
                        lines.Add(Describe(report.FrameRateDifferences, lang));
                    }
                    break;

                case Failure.AudioDuration ad:
                    lines.Add(Localizer.GetDynamic("MergedAudioDurationOff", lang,
                        ad.Actual.ToString("F1", CultureInfo.InvariantCulture),
                        ad.Expected.ToString("F1", CultureInfo.InvariantCulture)));
                    if (report != null && report.AudioDifferences.Count > 0)
                    {
                        lines.Add(Localizer.Get("AudioFormatDiffers", lang));
                        lines.Add(Describe(report.AudioDifferences, lang));
                    }
                    break;

                case Failure.Processing:
                    lines.Add(Localizer.Get("FfmpegCouldNotProcess", lang));
                    break;

                default:
                    lines.Add(Localizer.Get("MergedFileFailedVerification", lang));
                    break;
            }

            if (includeLogHint) lines.Add(Localizer.Get("SeeLogForDetails", lang));
            return string.Join("\n", lines);
        }
    }
}
