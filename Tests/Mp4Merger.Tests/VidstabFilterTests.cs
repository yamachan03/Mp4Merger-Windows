using Mp4Merger.Core;

namespace Mp4Merger.Tests;

/// Building the vidstab filter strings.
///
/// Gimbal stabilisation failed every time on Windows because the transform
/// file's path went inside the filter string, where ffmpeg reads "C:/x" as an
/// option named "C". Measured 2026-11-22 on ffmpeg 8.0.1 and N-129166 alike, so
/// it was never a change in ffmpeg. These tests keep a path from getting back in.
public class VidstabFilterTests
{
    private const string Name = "transform_ab12.trf";

    [Fact]
    public void BuildsTheMeasuringPass()
    {
        Assert.Equal(
            "vidstabdetect=shakiness=5:accuracy=15:result=transform_ab12.trf",
            VidstabFilters.Detect(Name, shakiness: 5, accuracy: 15));
    }

    [Fact]
    public void BuildsTheApplyingPass()
    {
        Assert.Equal(
            "vidstabtransform=input=transform_ab12.trf:smoothing=30:crop=black:optzoom=1",
            VidstabFilters.Transform(Name, smoothing: 30));
    }

    [Theory]
    [InlineData(@"C:/Users/yy200/AppData/Local/Temp/t.trf")]
    [InlineData(@"C:\Users\yy200\t.trf")]
    [InlineData(@"C\:/Users/yy200/t.trf")]
    [InlineData("sub/t.trf")]
    [InlineData(@"sub\t.trf")]
    public void RefusesAPath(string path)
    {
        // This is the bug itself: a path here is what made every run fail.
        Assert.Throws<ArgumentException>(() => VidstabFilters.Detect(path, 5, 15));
        Assert.Throws<ArgumentException>(() => VidstabFilters.Transform(path, 30));
    }

    [Theory]
    [InlineData("a,b.trf")]
    [InlineData("it's.trf")]
    [InlineData("a[1].trf")]
    [InlineData("say\"what\".trf")]
    public void RefusesTheOtherCharactersAFilterStringSplitsOn(string name)
    {
        // A user's temp folder can hold a comma or an apostrophe, and those break
        // a filter string just as a colon does. Keeping them out is why the file
        // is named rather than pathed.
        Assert.Throws<ArgumentException>(() => VidstabFilters.Detect(name, 5, 15));
        Assert.Throws<ArgumentException>(() => VidstabFilters.Transform(name, 30));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RefusesNoNameAtAll(string? name)
    {
        Assert.Throws<ArgumentException>(() => VidstabFilters.Detect(name!, 5, 15));
    }

    [Fact]
    public void AcceptsAnOrdinaryGeneratedName()
    {
        // The shape the engine actually passes.
        string generated = $"transform_{Guid.NewGuid():N}.trf";

        Assert.Contains(generated, VidstabFilters.Detect(generated, 5, 15));
        Assert.Contains(generated, VidstabFilters.Transform(generated, 10));
    }

    [Fact]
    public void PutsNothingButTheNameWhereAFileGoes()
    {
        string detect = VidstabFilters.Detect(Name, 5, 15);
        string transform = VidstabFilters.Transform(Name, 30);

        // Each colon in these strings must be a filter separator, never part of
        // a drive letter.
        Assert.DoesNotContain(":/", detect);
        Assert.DoesNotContain(":/", transform);
        Assert.DoesNotContain(@":\", detect);
        Assert.DoesNotContain(@":\", transform);
    }
}
