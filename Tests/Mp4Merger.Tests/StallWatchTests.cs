using Mp4Merger.Core;

namespace Mp4Merger.Tests;

/// Deciding that an ffmpeg run has stopped getting anywhere.
///
/// Without this the app waited on a frozen ffmpeg until the user pressed cancel,
/// and the log said nothing (2026-11-08, cause still unknown). The clock is a
/// parameter, so these run instantly instead of waiting out real minutes.
public class StallWatchTests
{
    private static readonly DateTime Start = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(3);

    private static StallWatch NewWatch() => new(Limit, Start);

    [Fact]
    public void HasNoPositionBeforeFfmpegReportsOne()
    {
        Assert.Null(NewWatch().Position);
    }

    [Fact]
    public void IsNotStalledWhileItIsStillWithinTheLimit()
    {
        var watch = NewWatch();

        Assert.False(watch.HasStalled(Start));
        Assert.False(watch.HasStalled(Start + Limit));
    }

    [Fact]
    public void StallsWhenNothingIsEverReported()
    {
        // ffmpeg that hangs before the first frame, which is what a frozen
        // encoder initialisation would look like.
        var watch = NewWatch();

        Assert.True(watch.HasStalled(Start + Limit + TimeSpan.FromSeconds(1)));
        Assert.Null(watch.Position);
    }

    [Fact]
    public void KeepsTheFurthestPositionItWasGiven()
    {
        var watch = NewWatch();

        watch.Advance(10, Start);
        watch.Advance(125.5, Start + TimeSpan.FromSeconds(5));

        Assert.Equal(125.5, watch.Position!.Value, 3);
    }

    [Fact]
    public void MovingOnResetsTheClock()
    {
        var watch = NewWatch();

        // Two and a half minutes of silence, then one more frame.
        var late = Start + TimeSpan.FromMinutes(2.5);
        watch.Advance(42, late);

        Assert.False(watch.HasStalled(late + TimeSpan.FromMinutes(2)));
        Assert.True(watch.HasStalled(late + Limit + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void RepeatingTheSamePositionIsNotProgress()
    {
        // This is the real failure: ffmpeg keeps rewriting its status line with
        // the position it is stuck at, so taking any line as a sign of life
        // would let a frozen run sit there forever.
        var watch = NewWatch();
        watch.Advance(120, Start);

        for (int second = 1; second <= 240; second++)
        {
            watch.Advance(120, Start + TimeSpan.FromSeconds(second));
        }

        Assert.True(watch.HasStalled(Start + TimeSpan.FromSeconds(240)));
        Assert.Equal(120, watch.Position!.Value, 3);
    }

    [Fact]
    public void GoingBackwardsIsNotProgressEither()
    {
        var watch = NewWatch();
        watch.Advance(120, Start);
        watch.Advance(100, Start + TimeSpan.FromMinutes(2));

        Assert.Equal(120, watch.Position!.Value, 3);
        Assert.True(watch.HasStalled(Start + Limit + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void IgnoresALineThatCarriedNoPosition()
    {
        var watch = NewWatch();
        watch.Advance(60, Start);
        watch.Advance(null, Start + TimeSpan.FromMinutes(2));

        Assert.Equal(60, watch.Position!.Value, 3);
        Assert.True(watch.HasStalled(Start + Limit + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ReportsHowLongItHasBeenSinceAnythingMoved()
    {
        var watch = NewWatch();
        watch.Advance(10, Start + TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(90),
            watch.IdleFor(Start + TimeSpan.FromSeconds(120)));
    }

    [Fact]
    public void NeverReportsTimeRunningBackwards()
    {
        // The clock can step back over a system time change.
        var watch = NewWatch();

        Assert.Equal(TimeSpan.Zero, watch.IdleFor(Start - TimeSpan.FromMinutes(10)));
        Assert.False(watch.HasStalled(Start - TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void SurvivesAWholeRunThatKeepsMovingSlowly()
    {
        // A two-hour conversion at a position update every half second: slow is
        // not stalled, and this must never fire on it.
        var watch = NewWatch();
        var now = Start;

        for (int tick = 1; tick <= 2000; tick++)
        {
            now = Start + TimeSpan.FromSeconds(tick * 0.5);
            watch.Advance(tick * 0.1, now);
            Assert.False(watch.HasStalled(now));
        }

        Assert.Equal(200, watch.Position!.Value, 3);
    }
}
