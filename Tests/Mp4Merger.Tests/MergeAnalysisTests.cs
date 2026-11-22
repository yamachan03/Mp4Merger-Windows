using Mp4Merger.Core;
using static Mp4Merger.Tests.TestProbes;

namespace Mp4Merger.Tests;

/// Whether a set of files can be copy merged as they are, and which file is in
/// the way when they cannot.
public class MergeAnalysisTests
{
    [Fact]
    public void LeavesMatchingFilesAlone()
    {
        var report = MergeCompatibility.Analyze(new[] { Probe("a.mp4"), Probe("b.mp4") });

        Assert.False(report.NeedsConforming);
        Assert.Empty(report.AudioDifferences);
        Assert.Empty(report.VideoDifferences);
        Assert.Empty(report.FrameRateDifferences);
        Assert.Null(report.CommonTimescale);
    }

    [Fact]
    public void NamesTheFileWhoseSampleRateIsDifferent()
    {
        var report = MergeCompatibility.Analyze(new[]
        {
            Probe("a.mp4"),
            Probe("b.mp4", sampleRate: 44100)
        });

        Assert.True(report.NeedsConforming);
        Assert.Equal(new[] { 1 }, report.FilesNeedingAudio);

        var difference = Assert.Single(report.AudioDifferences);
        Assert.Equal(MergeCompatibility.Field.SampleRate, difference.Field);
        Assert.Equal("b.mp4", difference.FileName);
        Assert.Equal("44100", difference.Actual);
        Assert.Equal("48000", difference.Expected);
    }

    [Fact]
    public void NoticesAFileWithNoAudioAtAll()
    {
        // Silence has to be added, or the merged file loses its audio track
        // from that point on.
        var report = MergeCompatibility.Analyze(new[]
        {
            Probe("a.mp4"),
            Probe("b.mp4", audioLayout: null)
        });

        var difference = Assert.Single(report.AudioDifferences);
        Assert.Equal(MergeCompatibility.Field.MissingAudio, difference.Field);
        Assert.Equal(new[] { 1 }, report.FilesNeedingAudio);
    }

    [Fact]
    public void TreatsAResolutionChangeAsSomethingCopyingCannotSurvive()
    {
        var report = MergeCompatibility.Analyze(new[]
        {
            Probe("a.mp4"),
            Probe("a2.mp4"),
            Probe("b.mp4", width: 1280, height: 720)
        });

        var difference = Assert.Single(report.VideoDifferences);
        Assert.Equal(MergeCompatibility.Field.Resolution, difference.Field);
        Assert.Equal("1280x720", difference.Actual);
        Assert.Equal("1920x1080", difference.Expected);
        Assert.Equal(new[] { 2 }, report.FilesNeedingVideo);
    }

    [Fact]
    public void KeepsAFrameRateDifferenceOutOfTheMustReencodeList()
    {
        // Copy merging usually survives mixed frame rates, so this is reported
        // apart from the differences that force a re-encode.
        var report = MergeCompatibility.Analyze(new[]
        {
            Probe("a.mp4", frameRate: "30/1", timeBase: "1/30000"),
            Probe("b.mp4", frameRate: "24/1", timeBase: "1/24000")
        });

        Assert.Empty(report.VideoDifferences);
        Assert.Single(report.FrameRateDifferences);
        Assert.Equal(new[] { 1 }, report.FilesWithOtherFrameRate);
    }

    [Fact]
    public void FlagsMixedTimeBasesBecauseConcatWillNotRescaleThem()
    {
        // This is the one that used to play later clips too fast while the
        // container duration still looked right.
        var report = MergeCompatibility.Analyze(new[]
        {
            Probe("a.mp4", frameRate: "30/1", timeBase: "1/30000"),
            Probe("b.mp4", frameRate: "24/1", timeBase: "1/24000")
        });

        Assert.Equal(120000, report.CommonTimescale);
        Assert.True(report.NeedsConforming);
    }

    [Fact]
    public void PlansToRemuxEveryFileWhenTheTimeBasesDiffer()
    {
        var report = MergeCompatibility.Analyze(new[]
        {
            Probe("a.mp4", frameRate: "30/1", timeBase: "1/30000"),
            Probe("b.mp4", frameRate: "24/1", timeBase: "1/24000")
        });

        var (timescale, remuxAll) = report.PlanTimescale(reencodingVideo: false);

        Assert.Equal(120000, timescale);
        Assert.True(remuxAll);
    }

    [Fact]
    public void LeavesFilesAsTheyAreWhenOneTimeBaseFitsThemAll()
    {
        var report = MergeCompatibility.Analyze(new[] { Probe("a.mp4"), Probe("b.mp4") });

        var (timescale, remuxAll) = report.PlanTimescale(reencodingVideo: false);

        Assert.Equal(30000, timescale);
        Assert.False(remuxAll);
    }
}
