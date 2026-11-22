using Mp4Merger.Core;

namespace Mp4Merger.Tests;

/// Layouts, timescales, sizes and bit rates: the arithmetic a re-encode is
/// built from. A wrong answer here either corrupts the output or throws away
/// quality, and none of it needs ffmpeg to check.
public class MergeFormatTests
{
    [Theory]
    [InlineData("", 1, "mono")]
    [InlineData("", 2, "stereo")]
    [InlineData("", 6, "5.1")]
    [InlineData("", 3, "3 channels")]
    [InlineData("unknown", 2, "stereo")]
    [InlineData("5.1(side)", 6, "5.1(side)")]
    public void FillsInALayoutNameFfprobeDidNotGive(string layout, int channels, string expected)
    {
        Assert.Equal(expected, MergeCompatibility.NormalizedLayout(layout, channels));
    }

    [Fact]
    public void UsesTheOnlyTimescaleWhenEveryFileAgrees()
    {
        Assert.Equal(30000, MergeCompatibility.CommonTimescaleFor(new[] { "1/30000", "1/30000" }));
    }

    [Fact]
    public void FindsATimescaleThatRepresentsEveryFileExactly()
    {
        // 30000 and 24000 both divide 120000, so remuxing to it loses nothing.
        Assert.Equal(120000, MergeCompatibility.CommonTimescaleFor(new[] { "1/30000", "1/24000" }));
    }

    [Fact]
    public void FallsBackTo90kHzWhenAnExactTimescaleWouldBeAbsurd()
    {
        // A 1001-based rate against 30000 would need 30,030,000.
        Assert.Equal(90000, MergeCompatibility.CommonTimescaleFor(new[] { "1/30000", "1/1001" }));
    }

    [Fact]
    public void FallsBackTo90kHzWithNothingToGoOn()
    {
        Assert.Equal(90000, MergeCompatibility.CommonTimescaleFor(Array.Empty<string>()));
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("1/0")]
    [InlineData("1")]
    [InlineData("1/-30")]
    public void IgnoresATimeBaseItCannotRead(string broken)
    {
        Assert.Equal(30000, MergeCompatibility.CommonTimescaleFor(new[] { broken, "1/30000" }));
    }

    [Fact]
    public void AlwaysKeeps90kHzInReachForReencodedOutput()
    {
        // 90 kHz suits the common rates and is what re-encoded segments used
        // before, so it is always part of the set.
        Assert.Equal(90000, MergeCompatibility.ReencodeTimescale(new[] { "1/30000" }));
        Assert.Equal(90000, MergeCompatibility.ReencodeTimescale(Array.Empty<string>()));
    }

    [Theory]
    [InlineData(1920, 1080, 1080, 1920, 1080)]
    [InlineData(1280, 720, 1080, 1920, 1080)]
    [InlineData(3840, 2160, 1080, 1920, 1080)]
    public void KeepsTheAspectRatioWhenScaling(int w, int h, int toHeight, int expectedW, int expectedH)
    {
        Assert.Equal((expectedW, expectedH), MergeCompatibility.ScaledSize(w, h, toHeight));
    }

    [Fact]
    public void RoundsTheScaledWidthToAnEvenNumber()
    {
        // A portrait 1080x1920 at 1080 high works out to 607.5 wide, and
        // yuv420p cannot have an odd width.
        var size = MergeCompatibility.ScaledSize(1080, 1920, 1080);

        Assert.Equal((608, 1080), size);
        Assert.Equal(0, size.Width % 2);
    }

    [Fact]
    public void ScalesWithoutASourceSize()
    {
        Assert.Equal((0, 1080), MergeCompatibility.ScaledSize(0, 0, 1080));
    }

    [Fact]
    public void DoublesTheBitRateFrom1080pTo4KRatherThanQuadruplingIt()
    {
        // Four times the pixels carry no more detail, so the rate follows the
        // edge length, not the area.
        Assert.Equal(20_000_000,
            MergeCompatibility.ReencodeBitRate(10_000_000, (1920, 1080), (3840, 2160), 12_000_000));
    }

    [Fact]
    public void SavesBitsOnADownscaleTheSameWay()
    {
        Assert.Equal(10_000_000,
            MergeCompatibility.ReencodeBitRate(20_000_000, (3840, 2160), (1920, 1080), 12_000_000));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void FallsBackWithoutASourceBitRate(int? source)
    {
        Assert.Equal(12_000_000,
            MergeCompatibility.ReencodeBitRate(source, (1920, 1080), (1920, 1080), 12_000_000));
    }

    [Fact]
    public void FallsBackWithoutASourceSize()
    {
        Assert.Equal(12_000_000,
            MergeCompatibility.ReencodeBitRate(10_000_000, (0, 0), (1920, 1080), 12_000_000));
    }

    [Fact]
    public void KeepsTheBitRateInsideSensibleBounds()
    {
        Assert.Equal(1_000_000,
            MergeCompatibility.ReencodeBitRate(100_000, (1920, 1080), (1920, 1080), 12_000_000));
        Assert.Equal(100_000_000,
            MergeCompatibility.ReencodeBitRate(500_000_000, (1920, 1080), (1920, 1080), 12_000_000));
    }

    [Theory]
    [InlineData(0, "128k")]
    [InlineData(1, "128k")]
    [InlineData(2, "192k")]
    [InlineData(6, "384k")]
    public void PicksAnAudioBitRateForTheChannelCount(int channels, string expected)
    {
        Assert.Equal(expected,
            MergeCompatibility.AudioBitRateFor(new AudioFormat("aac", "LC", 48000, channels, "stereo")));
    }
}
