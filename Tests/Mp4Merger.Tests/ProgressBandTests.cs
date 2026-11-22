using Mp4Merger.Core;

namespace Mp4Merger.Tests;

/// How the progress bar is shared out between the steps of a run.
///
/// Two bugs were fixed here on 2026-11-22: in individual mode the bar went
/// backwards after stabilizing a clip, and in merge mode it reached 100% and
/// then dropped to 0. Both were a step mapped onto the whole bar instead of its
/// own band, so these tests pin the mapping down.
public class ProgressBandTests
{
    [Fact]
    public void GivesEachFileAnEqualShare()
    {
        Assert.Equal(new ProgressBand(0, 20), ProgressBand.ForFile(0, 5));
        Assert.Equal(new ProgressBand(20, 20), ProgressBand.ForFile(1, 5));
        Assert.Equal(new ProgressBand(80, 20), ProgressBand.ForFile(4, 5));
    }

    [Fact]
    public void FillsTheWholeBarWithASingleFile()
    {
        var band = ProgressBand.ForFile(0, 1);

        Assert.Equal(0, band.At(0), 6);
        Assert.Equal(100, band.At(100), 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FallsBackToTheWholeBarRatherThanDividingByZero(int count)
    {
        Assert.Equal(ProgressBand.Whole, ProgressBand.ForFile(0, count));
    }

    [Fact]
    public void TheLastFileEndsExactlyAtAHundred()
    {
        // A bar that stops at 99.9% looks like the run never finished.
        for (int count = 1; count <= 8; count++)
        {
            Assert.Equal(100, ProgressBand.ForFile(count - 1, count).At(100), 6);
        }
    }

    [Fact]
    public void ScalesAStepsOwnPercentageIntoItsBand()
    {
        var band = ProgressBand.ForFile(1, 4); // 25% to 50%

        Assert.Equal(25, band.At(0), 6);
        Assert.Equal(37.5, band.At(50), 6);
        Assert.Equal(50, band.At(100), 6);
    }

    [Fact]
    public void NeverLetsAStepReportOutsideItsOwnBand()
    {
        var band = ProgressBand.ForFile(1, 4);

        Assert.Equal(25, band.At(-10), 6);
        Assert.Equal(50, band.At(150), 6);
    }

    [Fact]
    public void SplitsABandIntoConsecutivePieces()
    {
        var band = ProgressBand.ForFile(1, 4); // 25% to 50%

        var first = band.Slice(0, 0.5);
        var second = band.Slice(0.5, 1);

        Assert.Equal(25, first.At(0), 6);
        Assert.Equal(37.5, first.At(100), 6);
        // The second piece picks up exactly where the first one stopped.
        Assert.Equal(first.At(100), second.At(0), 6);
        Assert.Equal(50, second.At(100), 6);
    }

    [Fact]
    public void KeepsTheIndividualModeMappingItHadBeforeWithoutStabilization()
    {
        // The arithmetic this replaced was `i / count * 100 + pct / count`.
        // Without stabilization the bar has to land on exactly the same numbers.
        const int count = 6;
        for (int i = 0; i < count; i++)
        {
            var (_, convert) = ProgressBand.ForIndividualFile(i, count, stabilize: false);
            foreach (double pct in new[] { 0.0, 1.0, 50.0, 99.0, 100.0 })
            {
                double expected = (double)i / count * 100 + pct / count;
                Assert.Equal(expected, convert.At(pct), 6);
            }
        }
    }

    [Fact]
    public void KeepsTheAiMappingItHadBefore()
    {
        // Was `i / count * 100 + pct / count * 0.3` for the filter pass and
        // `i / count * 100 + 30 / count + pct / count * 0.7` for the upscale.
        const int count = 3;
        for (int i = 0; i < count; i++)
        {
            var (_, convert) = ProgressBand.ForIndividualFile(i, count, stabilize: false);
            var (filter, upscale) = convert.SplitForAiUpscale();

            foreach (double pct in new[] { 0.0, 50.0, 100.0 })
            {
                Assert.Equal((double)i / count * 100 + pct / count * 0.3, filter.At(pct), 6);
                Assert.Equal((double)i / count * 100 + 30.0 / count + pct / count * 0.7, upscale.At(pct), 6);
            }
        }
    }

    [Fact]
    public void NeverGoesBackwardsAcrossAWholeIndividualRunWithStabilization()
    {
        // This is the bug: stabilization used the first half of each file's band
        // and the conversion then restarted from the band's beginning. The split
        // is asked of ForIndividualFile rather than rebuilt here, so putting the
        // engine's old mapping back makes this fail.
        const int count = 4;
        double last = -1;

        for (int i = 0; i < count; i++)
        {
            var (stabilize, convert) = ProgressBand.ForIndividualFile(i, count, stabilize: true);

            foreach (var step in new[] { stabilize, convert })
            {
                foreach (double pct in new[] { 0.0, 25.0, 50.0, 75.0, 100.0 })
                {
                    double value = step.At(pct);
                    Assert.True(value >= last, $"file {i}: {value} came after {last}");
                    last = value;
                }
            }
        }

        Assert.Equal(100, last, 6);
    }

    [Fact]
    public void HandsTheConversionTheWholeFileWithoutStabilization()
    {
        // Without stabilization the conversion must get the file's whole band,
        // which is what keeps the numbers identical to before the split existed.
        var (stabilize, convert) = ProgressBand.ForIndividualFile(1, 4, stabilize: false);

        Assert.Equal(0, stabilize.Width, 6);
        Assert.Equal(25, convert.Start, 6);
        Assert.Equal(50, convert.End, 6);
    }

    [Fact]
    public void StabilizationNeverEatsIntoTheConversionsShare()
    {
        var (stabilize, convert) = ProgressBand.ForIndividualFile(0, 2, stabilize: true);

        Assert.Equal(stabilize.End, convert.Start, 6);
        Assert.Equal(ProgressBand.StabilizeShareOfFile,
            stabilize.Width / (stabilize.Width + convert.Width), 6);
    }

    [Fact]
    public void NeverGoesBackwardsAcrossAMergeWithStabilization()
    {
        // The merge-mode shape of the same bug: stabilizing took the bar to 100%
        // before the merge steps started over at 0. ForMerge and ForFileWithin
        // are the engine's own calls, so reverting it makes this fail.
        const int count = 3;
        var (stabilizeBand, mergeBand) = ProgressBand.ForMerge(stabilize: true);
        double last = -1;

        for (int i = 0; i < count; i++)
        {
            var clip = stabilizeBand.ForFileWithin(i, count);
            foreach (double pct in new[] { 0.0, 50.0, 100.0 })
            {
                double value = clip.At(pct);
                Assert.True(value >= last, $"clip {i}: {value} came after {last}");
                last = value;
            }
        }

        // Stabilization hands over at its share of the bar, not at 100%.
        Assert.Equal(ProgressBand.StabilizeShareOfMerge * 100, last, 6);

        foreach (double pct in new[] { 0.0, 50.0, 100.0 })
        {
            double value = mergeBand.At(pct);
            Assert.True(value >= last, $"merge: {value} came after {last}");
            last = value;
        }

        Assert.Equal(100, last, 6);
    }

    [Fact]
    public void LeavesTheWholeBarToTheMergeWithoutStabilization()
    {
        var (stabilize, merge) = ProgressBand.ForMerge(stabilize: false);

        Assert.Equal(0, stabilize.Width, 6);
        Assert.Equal(0, merge.At(0), 6);
        Assert.Equal(100, merge.At(100), 6);
    }

    [Fact]
    public void SplitsAnAiMergeIntoTheMergeAndTheUpscale()
    {
        var (merge, upscale) = ProgressBand.ForAiMerge();

        Assert.Equal(0, merge.At(0), 6);
        Assert.Equal(ProgressBand.MergeShareOfAiMerge * 100, merge.At(100), 6);
        // The upscale picks up exactly where the merge stopped and ends at 100.
        Assert.Equal(merge.At(100), upscale.At(0), 6);
        Assert.Equal(100, upscale.At(100), 6);
    }

    [Fact]
    public void SplitsAnAiConversionIntoFiltersAndTheUpscale()
    {
        var band = ProgressBand.ForFile(1, 4); // 25% to 50%
        var (filters, upscale) = band.SplitForAiUpscale();

        Assert.Equal(25, filters.At(0), 6);
        Assert.Equal(filters.At(100), upscale.At(0), 6);
        Assert.Equal(50, upscale.At(100), 6);
    }

    [Fact]
    public void GivesEachFileAnEqualShareOfAPhase()
    {
        var phase = ProgressBand.Whole.Slice(0, 0.4); // 0% to 40%
        double last = -1;

        for (int i = 0; i < 4; i++)
        {
            var clip = phase.ForFileWithin(i, 4);
            Assert.Equal(10, clip.Width, 6);
            Assert.True(clip.Start >= last);
            last = clip.Start;
        }

        Assert.Equal(40, phase.ForFileWithin(3, 4).End, 6);
    }

    [Fact]
    public void KeepsAPhaseWholeWhenThereAreNoFilesToSplitItBetween()
    {
        var phase = ProgressBand.Whole.Slice(0, 0.4);

        Assert.Equal(phase, phase.ForFileWithin(0, 0));
    }

    [Fact]
    public void ReportsWhereABandEnds()
    {
        Assert.Equal(50, ProgressBand.ForFile(1, 4).End, 6);
        Assert.Equal(100, ProgressBand.Whole.End, 6);
    }
}
