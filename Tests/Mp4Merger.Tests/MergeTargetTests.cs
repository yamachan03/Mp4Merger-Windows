using Mp4Merger.Core;

namespace Mp4Merger.Tests;

/// What every file is conformed to, and whether an encoder can produce it.
public class MergeTargetTests
{
    [Fact]
    public void MapsTheCommonFormatsToAnEncoder()
    {
        var settings = MergeCompatibility.GetEncoderSettings(
            new VideoFormat("h264", "High", 1920, 1080, "yuv420p", "30/1"), "libx264", "libx265");

        Assert.NotNull(settings);
        Assert.Equal("libx264", settings!.Encoder);
        Assert.Equal("high", settings.Profile);
        Assert.Equal("yuv420p", settings.PixelFormat);
    }

    [Fact]
    public void AsksAHardwareEncoderFor10BitFramesInItsOwnLayout()
    {
        var format = new VideoFormat("hevc", "Main 10", 1920, 1080, "yuv420p10le", "30/1");

        Assert.Equal("yuv420p10le",
            MergeCompatibility.GetEncoderSettings(format, "libx264", "libx265")!.PixelFormat);
        Assert.Equal("p010le",
            MergeCompatibility.GetEncoderSettings(format, "h264_nvenc", "hevc_nvenc")!.PixelFormat);
    }

    [Fact]
    public void SaysSoWhenNoEncoderCanReproduceTheFormat()
    {
        Assert.Null(MergeCompatibility.GetEncoderSettings(
            new VideoFormat("vp9", "Profile 0", 1920, 1080, "yuv420p", "30/1"), "libx264", "libx265"));
    }

    [Theory]
    [InlineData("aac", "LC", 48000, 2, true)]
    [InlineData("mp3", "LC", 48000, 2, false)]
    [InlineData("aac", "HE-AAC", 48000, 2, false)]
    [InlineData("aac", "LC", 0, 2, false)]
    [InlineData("aac", "LC", 48000, 0, false)]
    public void KnowsWhichAudioFormatsItCanProduce(
        string codec, string profile, int rate, int channels, bool expected)
    {
        Assert.Equal(expected,
            MergeCompatibility.CanEncode(new AudioFormat(codec, profile, rate, channels, "stereo")));
    }

    [Fact]
    public void HasNoAudioTargetWhenNoFileHasAudio()
    {
        Assert.Null(MergeCompatibility.AudioTargetFor(new AudioFormat?[] { null, null }));
    }

    [Fact]
    public void KeepsTheFormatEveryFileAlreadyShares()
    {
        var shared = new AudioFormat("aac", "LC", 48000, 2, "stereo");

        Assert.Equal(shared, MergeCompatibility.AudioTargetFor(new AudioFormat?[] { shared, shared }));
    }

    [Fact]
    public void DownmixesToStereoRatherThanInventingChannels()
    {
        // Two 5.1 files and one stereo: 5.1 is the majority, but upmixing the
        // stereo file would only add empty channels, so everything goes stereo.
        var surround = new AudioFormat("aac", "LC", 48000, 6, "5.1");
        var stereo = new AudioFormat("aac", "LC", 48000, 2, "stereo");

        var target = MergeCompatibility.AudioTargetFor(new AudioFormat?[] { surround, surround, stereo })!;

        Assert.Equal(2, target.Channels);
        Assert.Equal("stereo", target.ChannelLayout);
        Assert.Equal(48000, target.SampleRate);
    }

    [Fact]
    public void FallsBackToAacStereoWhenTheMajorityCannotBeEncoded()
    {
        var mp3 = new AudioFormat("mp3", "", 44100, 2, "stereo");

        Assert.Equal(MergeCompatibility.DefaultAudio,
            MergeCompatibility.AudioTargetFor(new AudioFormat?[] { mp3, mp3 }));
    }

    [Fact]
    public void HasNoVideoTargetWhenNoFileHasVideo()
    {
        Assert.Null(MergeCompatibility.VideoTargetFor(new VideoFormat?[] { null }));
    }

    [Fact]
    public void ConformsToH264WhenTheSharedFormatCannotBeEncoded()
    {
        var vp9 = new VideoFormat("vp9", "Profile 0", 1920, 1080, "yuv420p", "30/1");

        var target = MergeCompatibility.VideoTargetFor(new VideoFormat?[] { vp9, vp9 })!;

        Assert.Equal("h264", target.Codec);
        Assert.Equal("High", target.Profile);
        Assert.Equal("yuv420p", target.PixelFormat);
        // The size and frame rate are kept; only the coding changes.
        Assert.Equal(1920, target.Width);
        Assert.Equal(1080, target.Height);
        Assert.Equal("30/1", target.FrameRate);
    }
}
