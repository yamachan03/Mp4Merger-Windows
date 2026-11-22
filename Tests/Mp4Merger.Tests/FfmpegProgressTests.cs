using Mp4Merger.Core;

namespace Mp4Merger.Tests;

/// Reading the position out of ffmpeg's status line. The app drives every
/// progress bar off this, so a change here silently freezes the bar.
public class FfmpegProgressTests
{
    [Fact]
    public void ReadsThePositionOffARealStatusLine()
    {
        // Copied from this ffmpeg's actual stderr (measured 2026-11-22).
        const string line =
            "frame=  181 fps=0.0 q=-1.0 Lsize=    7958KiB time=00:00:07.46 bitrate=8733.8kbits/s speed=8.36x";

        Assert.Equal(7.46, FfmpegProgress.PositionSeconds(line)!.Value, 3);
    }

    [Theory]
    [InlineData("time=00:00:00.00", 0.0)]
    [InlineData("time=00:00:07.46", 7.46)]
    [InlineData("time=00:01:00.00", 60.0)]
    [InlineData("time=01:02:03.50", 3723.5)]
    [InlineData("time=02:04:00.00", 7440.0)]
    public void ReadsHoursMinutesAndSeconds(string line, double expected)
    {
        Assert.Equal(expected, FfmpegProgress.PositionSeconds(line)!.Value, 3);
    }

    [Fact]
    public void ReadsAnHourFieldLongerThanTwoDigits()
    {
        // A 100-hour file is absurd, but ffmpeg does not pad the hour field to
        // two digits, and the pattern used to insist on exactly two.
        Assert.Equal(360000.0, FfmpegProgress.PositionSeconds("time=100:00:00.00")!.Value, 3);
    }

    [Fact]
    public void ReadsFewerThanTwoDecimalPlaces()
    {
        Assert.Equal(7.4, FfmpegProgress.PositionSeconds("time=00:00:07.4")!.Value, 3);
        Assert.Equal(7.0, FfmpegProgress.PositionSeconds("time=00:00:07")!.Value, 3);
    }

    [Fact]
    public void TreatsTheNegativePositionBeforeTheFirstFrameAsZero()
    {
        // ffmpeg prints time=-00:00:00.50 while it is still filling the pipeline.
        // Dropping the sign would read that as half a second of real progress.
        Assert.Equal(0.0, FfmpegProgress.PositionSeconds("time=-00:00:00.50")!.Value, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("frame=  181 fps=0.0 q=-1.0 size=7958KiB bitrate=8733.8kbits/s")]
    [InlineData("[h264 @ 000001] Invalid NAL unit size")]
    [InlineData("time=N/A")]
    public void ReturnsNullWhenTheLineCarriesNoPosition(string? line)
    {
        Assert.Null(FfmpegProgress.PositionSeconds(line));
    }

    [Fact]
    public void ScalesThePositionIntoAPercentage()
    {
        Assert.Equal(50.0, FfmpegProgress.Percent("time=00:00:05.00", 10, 0)!.Value, 3);
    }

    [Fact]
    public void CountsWhatEarlierFilesAlreadyFinished()
    {
        // Merging file 2 of 2: 10s done, 5s into a 20s whole.
        Assert.Equal(75.0, FfmpegProgress.Percent("time=00:00:05.00", 20, 10)!.Value, 3);
    }

    [Fact]
    public void NeverLeavesTheBarOutsideZeroToHundred()
    {
        // ffmpeg can report past the container duration on a file whose header
        // understates its length.
        Assert.Equal(100.0, FfmpegProgress.Percent("time=00:00:30.00", 10, 0)!.Value, 3);
        Assert.Equal(0.0, FfmpegProgress.Percent("time=-00:00:01.00", 10, 0)!.Value, 3);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void LeavesTheBarAloneWithoutALengthToScaleAgainst(double totalSeconds)
    {
        // An unreadable header gives a duration of 0; the bar should stay put
        // rather than jump to 100%.
        Assert.Null(FfmpegProgress.Percent("time=00:00:05.00", totalSeconds, 0));
    }

    [Fact]
    public void LeavesTheBarAloneWhenTheLineCarriesNoPosition()
    {
        Assert.Null(FfmpegProgress.Percent("Press [q] to stop", 10, 0));
    }
}
