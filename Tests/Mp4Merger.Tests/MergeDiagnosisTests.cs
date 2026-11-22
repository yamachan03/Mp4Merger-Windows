using Mp4Merger.Core;
using static Mp4Merger.Tests.TestProbes;

namespace Mp4Merger.Tests;

/// Turning an ffmpeg log into something the app can act on, and the one-line
/// summary written at the top of the log. Nothing here touches the UI strings,
/// which need a language to look up.
public class MergeDiagnosisTests
{
    [Theory]
    [InlineData("[aac @ 000001f8] channel element 0.0 is not allocated")]
    [InlineData("Error while decoding stream #0:1: aist#0:1")]
    [InlineData("[mp3 @ 000001f8] Header missing")]
    [InlineData("[ac3 @ 000001f8] frame sync error")]
    [InlineData("[opus @ 000001f8] Error decoding packet")]
    public void BlamesTheAudioWhenTheLogPointsAtADecoder(string log)
    {
        Assert.IsType<MergeDiagnosis.Failure.AudioDecode>(MergeDiagnosis.Classify(log));
    }

    [Theory]
    [InlineData("[h264 @ 000001f8] Invalid NAL unit size")]
    [InlineData("[hevc @ 000001f8] Could not find ref with POC")]
    [InlineData("Error while decoding stream #0:0: vist#0:0")]
    public void BlamesTheVideoWhenTheLogPointsAtAPictureDecoder(string log)
    {
        Assert.IsType<MergeDiagnosis.Failure.VideoDecode>(MergeDiagnosis.Classify(log));
    }

    [Fact]
    public void ReadsTheLogWhateverCaseItIsIn()
    {
        Assert.IsType<MergeDiagnosis.Failure.AudioDecode>(
            MergeDiagnosis.Classify("[AAC @ 000001F8] CHANNEL ELEMENT 0.0 IS NOT ALLOCATED"));
    }

    [Fact]
    public void PutsTheAudioFirstWhenBothDecodersComplain()
    {
        // The audio header mismatch after a join is the cause; the picture
        // errors that follow it are the symptom.
        var failure = MergeDiagnosis.Classify(
            "[h264 @ 1] Invalid NAL unit size\n[aac @ 2] channel element 0.0 is not allocated");

        Assert.IsType<MergeDiagnosis.Failure.AudioDecode>(failure);
    }

    [Fact]
    public void SaysItDoesNotKnowWhenTheLogNamesNothing()
    {
        Assert.IsType<MergeDiagnosis.Failure.Other>(MergeDiagnosis.Classify("Output file is empty"));
        Assert.IsType<MergeDiagnosis.Failure.Other>(MergeDiagnosis.Classify(""));
    }

    [Fact]
    public void FallsBackToWhatTheCallerAlreadyKnows()
    {
        var failure = MergeDiagnosis.Classify("Output file is empty", new MergeDiagnosis.Failure.Processing());

        Assert.IsType<MergeDiagnosis.Failure.Processing>(failure);
    }

    [Theory]
    [InlineData("30/1", "30fps")]
    [InlineData("25/1", "25fps")]
    [InlineData("60/1", "60fps")]
    [InlineData("120/1", "120fps")]
    public void WritesAWholeFrameRateWithoutDecimals(string raw, string expected)
    {
        Assert.Equal(expected, MergeDiagnosis.FormatFrameRate(raw));
    }

    [Theory]
    [InlineData("30000/1001", "29.97fps")]
    [InlineData("24000/1001", "23.976fps")]
    public void KeepsTheRatesPeopleRecogniseRatherThanRoundingThemAway(string raw, string expected)
    {
        Assert.Equal(expected, MergeDiagnosis.FormatFrameRate(raw));
    }

    [Theory]
    [InlineData("0/0")]
    [InlineData("30/0")]
    [InlineData("nonsense")]
    [InlineData("25")]
    [InlineData("")]
    public void HandsBackAFrameRateItCannotRead(string raw)
    {
        Assert.Equal(raw, MergeDiagnosis.FormatFrameRate(raw));
    }

    [Fact]
    public void SummarisesEachInputOnItsOwnLine()
    {
        var summary = MergeDiagnosis.Summary(new[] { Probe("a.mp4"), Probe("b.mp4", audioLayout: null) });
        var lines = summary.Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.Equal(
            "#1 a.mp4: video h264 High 1920x1080 yuv420p 30fps tb=1/30000 / audio aac LC 48000Hz stereo",
            lines[0]);
        Assert.Equal(
            "#2 b.mp4: video h264 High 1920x1080 yuv420p 30fps tb=1/30000 / no audio",
            lines[1]);
    }

    [Fact]
    public void SaysSoWhenTheTimeBaseIsUnknown()
    {
        var probe = new MediaProbe
        {
            Path = @"C:\clips\a.mp4",
            Video = new VideoFormat("h264", "High", 1920, 1080, "yuv420p", "30/1")
        };

        Assert.Contains("tb=?", MergeDiagnosis.Summary(new[] { probe }));
    }

    [Fact]
    public void SummarisesNothingAsNothing()
    {
        Assert.Equal("", MergeDiagnosis.Summary(Array.Empty<MediaProbe>()));
    }
}
