using Mp4Merger.Core;

namespace Mp4Merger.Tests;

/// Trimming a failure log for the screen.
///
/// Two attempts at this were wrong before it was pulled out here, and both were
/// invisible without a test: the first kept only the tail and dropped the build
/// line, the second looked for the first blank line -- which is the one closing
/// the build line, so the per-input summary was never kept at any input count.
public class FailureLogTests
{
    private const string Build = "ffmpeg: N-129166-gae9f4540cb-20261121";

    /// Exactly what VideoEngine.DetailedLog produces:
    /// build, blank line, one line per input, blank line, then ffmpeg's output.
    private static string RealLog(int inputs, int noiseLines = 400)
    {
        var summary = string.Join("\n", Enumerable.Range(1, inputs).Select(i =>
            $"#{i} clip{i}.mp4: video h264 High 1920x1080 yuv420p 30fps tb=1/30000 " +
            "/ audio aac LC 48000Hz stereo"));
        var noise = string.Join("\n", Enumerable.Range(0, noiseLines).Select(i =>
            $"[h264 @ 0000] Invalid NAL unit size ({i})"));
        return Build + "\n\n" + summary + "\n\n" + noise + "\nConversion failed!";
    }

    [Fact]
    public void LeavesAShortLogAlone()
    {
        string log = RealLog(inputs: 1, noiseLines: 2);

        Assert.True(log.Length < FailureLog.DefaultLimit, $"log was {log.Length}");
        Assert.Equal(log, FailureLog.ForDisplay(log));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(100)]
    public void AlwaysKeepsTheBuildAndTheEnding(int inputs)
    {
        string shown = FailureLog.ForDisplay(RealLog(inputs));

        Assert.Contains(Build, shown);
        Assert.Contains("Conversion failed!", shown);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void KeepsThePerInputSummaryWhileItFits(int inputs)
    {
        // This is the bug the previous version had: it looked for the first
        // blank line, which closes the build line, so the summary was dropped
        // even when there was plenty of room for it.
        string shown = FailureLog.ForDisplay(RealLog(inputs));

        Assert.Contains("#1 clip1.mp4", shown);
        Assert.Contains($"#{inputs} clip{inputs}.mp4", shown);
    }

    [Fact]
    public void GivesUpTheSummaryRatherThanTheBuildWhenItNoLongerFits()
    {
        // Ten inputs run past the head room. The build has to survive: it is
        // the part that cannot be recovered afterwards.
        string shown = FailureLog.ForDisplay(RealLog(inputs: 10));

        Assert.Contains(Build, shown);
        Assert.DoesNotContain("#1 clip1.mp4", shown);
    }

    [Fact]
    public void StaysAboutAsLongAsItPromised()
    {
        foreach (int inputs in new[] { 1, 5, 10, 30 })
        {
            string shown = FailureLog.ForDisplay(RealLog(inputs));

            Assert.True(shown.Length <= FailureLog.DefaultLimit + FailureLog.DefaultHeadRoom + 8,
                $"{inputs} inputs gave {shown.Length} characters");
        }
    }

    [Fact]
    public void SurvivesALogWithNoBlankLineAtAll()
    {
        string log = "ffmpeg: x\n" + new string('y', 9000);

        string shown = FailureLog.ForDisplay(log);

        Assert.StartsWith("ffmpeg: x", shown);
        Assert.True(shown.Length <= FailureLog.DefaultLimit + 12, shown.Length.ToString());
    }

    [Fact]
    public void SurvivesALogThatIsOneEnormousLine()
    {
        // The previous version computed a negative tail length here and threw
        // ArgumentOutOfRangeException -- inside a catch block, so it took the
        // app down while trying to explain a failure.
        string log = new string('z', 9000);

        string shown = FailureLog.ForDisplay(log);

        Assert.True(shown.Length <= FailureLog.DefaultLimit + 3, shown.Length.ToString());
        Assert.EndsWith("zzz", shown);
    }

    [Fact]
    public void SurvivesAFirstLineLongerThanTheWholeAllowance()
    {
        string log = new string('q', 5000) + "\nthe ending";

        string shown = FailureLog.ForDisplay(log);

        Assert.EndsWith("the ending", shown);
        Assert.True(shown.Length <= FailureLog.DefaultLimit + 3, shown.Length.ToString());
    }

    [Fact]
    public void SurvivesALogStartingWithABlankLine()
    {
        string log = "\n\n" + new string('w', 9000);

        string shown = FailureLog.ForDisplay(log);

        Assert.True(shown.Length <= FailureLog.DefaultLimit + 3, shown.Length.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void HandlesNothingAtAll(string? log)
    {
        Assert.Equal("", FailureLog.ForDisplay(log));
    }
}
