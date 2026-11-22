using Mp4Merger.Core;

namespace Mp4Merger.Tests;

/// Builds the probe results the tests analyse, so each test only has to name
/// the one thing it changes.
internal static class TestProbes
{
    public static MediaProbe Probe(
        string name,
        string? audioLayout = "stereo",
        int audioChannels = 2,
        int sampleRate = 48000,
        string audioCodec = "aac",
        string audioProfile = "LC",
        int width = 1920,
        int height = 1080,
        string frameRate = "30/1",
        string timeBase = "1/30000",
        double duration = 10) => new()
        {
            Path = @"C:\clips\" + name,
            Video = new VideoFormat("h264", "High", width, height, "yuv420p", frameRate),
            Audio = audioLayout == null
                ? null
                : new AudioFormat(audioCodec, audioProfile, sampleRate, audioChannels, audioLayout),
            VideoTimeBase = timeBase,
            Duration = duration
        };
}
