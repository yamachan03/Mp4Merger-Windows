using Mp4Merger.Core;

namespace Mp4Merger.Tests;

/// Which files the app takes in.
///
/// It used to accept .mp4 and .mov only, so footage ffmpeg reads perfectly well
/// was turned away at the drop zone -- an .mpg from a camcorder could not be
/// added at all (2026-11-22, found by trying it).
public class VideoFormatsTests
{
    [Theory]
    [InlineData(@"C:\clips\a.mp4")]
    [InlineData(@"C:\clips\a.mov")]
    [InlineData(@"C:\clips\a.m4v")]
    [InlineData(@"C:\clips\test.mpg")]
    [InlineData(@"C:\clips\a.mpeg")]
    [InlineData(@"C:\clips\a.mts")]
    [InlineData(@"C:\clips\a.m2ts")]
    [InlineData(@"C:\clips\a.ts")]
    [InlineData(@"C:\clips\a.avi")]
    [InlineData(@"C:\clips\a.mkv")]
    [InlineData(@"C:\clips\a.wmv")]
    [InlineData(@"C:\clips\a.webm")]
    public void TakesTheCommonVideoContainers(string path)
    {
        Assert.True(VideoFormats.IsSupported(path), path);
    }

    [Fact]
    public void TakesTheOnesItAlwaysTook()
    {
        // Whatever else changes, the two it started with have to keep working.
        Assert.True(VideoFormats.IsSupported("a.mp4"));
        Assert.True(VideoFormats.IsSupported("a.mov"));
    }

    [Theory]
    [InlineData(@"C:\clips\DJI_0001.LRF")]
    [InlineData(@"C:\clips\DJI_0001.lrf")]
    [InlineData(@"C:\clips\a.thm")]
    [InlineData(@"C:\clips\a.srt")]
    [InlineData(@"C:\clips\a.xml")]
    public void TurnsAwayWhatSitsBesideFootageWithoutBeingIt(string path)
    {
        // A drone writes an .lrf proxy next to every clip; dragging a folder in
        // would otherwise queue each one up to fail.
        Assert.False(VideoFormats.IsSupported(path), path);
    }

    [Theory]
    [InlineData(@"C:\clips\notes.txt")]
    [InlineData(@"C:\clips\photo.jpg")]
    [InlineData(@"C:\clips\music.mp3")]
    [InlineData(@"C:\clips\archive.zip")]
    [InlineData(@"C:\clips\noextension")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TurnsAwayWhatIsNotFootage(string? path)
    {
        Assert.False(VideoFormats.IsSupported(path));
    }

    [Fact]
    public void DoesNotMindHowTheExtensionIsSpelled()
    {
        Assert.True(VideoFormats.IsSupported(@"C:\clips\A.MP4"));
        Assert.True(VideoFormats.IsSupported(@"C:\clips\A.MpG"));
    }

    [Fact]
    public void BuildsAFilterAnOpenDialogUnderstands()
    {
        string filter = VideoFormats.DialogFilter("動画ファイル", "すべてのファイル");
        string[] parts = filter.Split('|');

        // A dialog filter is label|patterns|label|patterns.
        Assert.Equal(4, parts.Length);
        Assert.StartsWith("動画ファイル (", parts[0]);
        Assert.Contains("*.mp4", parts[1]);
        Assert.Contains("*.mpg", parts[1]);
        Assert.Equal("すべてのファイル (*.*)", parts[2]);
        Assert.Equal("*.*", parts[3]);
    }

    [Fact]
    public void EveryListedExtensionStartsWithADotAndIsLowerCase()
    {
        foreach (string ext in VideoFormats.Extensions)
        {
            Assert.StartsWith(".", ext);
            Assert.Equal(ext.ToLowerInvariant(), ext);
            Assert.True(VideoFormats.IsSupported("clip" + ext), ext);
        }
    }
}
