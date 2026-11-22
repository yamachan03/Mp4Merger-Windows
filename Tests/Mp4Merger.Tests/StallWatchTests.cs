using Mp4Merger.Core;

namespace Mp4Merger.Tests;

/// Deciding that an ffmpeg run has stopped getting anywhere.
///
/// Without this the app waited on a frozen ffmpeg until the user pressed cancel,
/// and the log said nothing (2026-11-08, cause still unknown). Progress is
/// counted in watchdog checks rather than clock time, so these run instantly and
/// sleeping the machine cannot be mistaken for a stall.
public class StallWatchTests
{
    private const int Allowed = 18;   // 18 checks x 10 seconds = three minutes

    private static StallWatch NewWatch() => new(Allowed);

    /// Runs `checks` checks with nothing reported in between.
    private static bool QuietFor(StallWatch watch, int checks)
    {
        bool stalled = false;
        for (int i = 0; i < checks; i++) stalled = watch.CheckSaysStalled();
        return stalled;
    }

    [Fact]
    public void HasNoPositionBeforeFfmpegReportsOne()
    {
        var watch = NewWatch();

        Assert.Null(watch.Position);
        Assert.Equal(0, watch.QuietChecks);
    }

    [Fact]
    public void IsNotStalledWhileItIsStillWithinTheAllowance()
    {
        var watch = NewWatch();

        Assert.False(QuietFor(watch, Allowed - 1));
        Assert.Equal(Allowed - 1, watch.QuietChecks);
    }

    [Fact]
    public void StallsOnTheCheckThatUsesUpTheAllowance()
    {
        // The boundary: one short is fine, the next one is not.
        Assert.False(QuietFor(NewWatch(), Allowed - 1));
        Assert.True(QuietFor(NewWatch(), Allowed));
    }

    [Fact]
    public void StallsWhenNothingIsEverReported()
    {
        // ffmpeg that hangs before the first frame, which is what a frozen
        // encoder initialisation would look like.
        var watch = NewWatch();

        Assert.True(QuietFor(watch, Allowed));
        Assert.Null(watch.Position);
    }

    [Fact]
    public void KeepsTheFurthestPositionItWasGiven()
    {
        var watch = NewWatch();

        watch.Advance(10);
        watch.Advance(125.5);

        Assert.Equal(125.5, watch.Position!.Value, 3);
    }

    [Fact]
    public void MovingOnResetsTheCount()
    {
        var watch = NewWatch();

        Assert.False(QuietFor(watch, Allowed - 1));
        watch.Advance(42);

        Assert.False(watch.CheckSaysStalled());
        Assert.Equal(0, watch.QuietChecks);
        Assert.False(QuietFor(watch, Allowed - 1));
        Assert.True(watch.CheckSaysStalled());
    }

    [Fact]
    public void RepeatingTheSamePositionIsNotProgress()
    {
        // This is the real failure: ffmpeg keeps rewriting its status line with
        // the position it is stuck at, so taking any line as a sign of life
        // would let a frozen run sit there forever.
        var watch = NewWatch();
        watch.Advance(120);

        bool stalled = false;
        for (int check = 0; check < Allowed + 1; check++)
        {
            watch.Advance(120);
            stalled = watch.CheckSaysStalled();
        }

        Assert.True(stalled);
        Assert.Equal(120, watch.Position!.Value, 3);
    }

    [Fact]
    public void GoingBackwardsIsNotProgressEither()
    {
        var watch = NewWatch();
        watch.Advance(120);
        Assert.False(watch.CheckSaysStalled());

        watch.Advance(100);

        Assert.Equal(120, watch.Position!.Value, 3);
        Assert.True(QuietFor(watch, Allowed));
    }

    [Fact]
    public void IgnoresALineThatCarriedNoPosition()
    {
        var watch = NewWatch();
        watch.Advance(60);
        Assert.False(watch.CheckSaysStalled());

        watch.Advance(null);

        Assert.Equal(60, watch.Position!.Value, 3);
        Assert.True(QuietFor(watch, Allowed));
    }

    [Fact]
    public void CountsPositionZeroAsSomethingReported()
    {
        // FfmpegProgress turns the negative position ffmpeg prints before the
        // first frame into 0, so 0 is a real report: the watch has to take it,
        // which also means a run that hangs right at the start says
        // "from 00:00:00" rather than "after it started".
        var watch = NewWatch();

        watch.Advance(0);

        Assert.NotNull(watch.Position);
        Assert.Equal(0, watch.Position!.Value, 6);
    }

    [Fact]
    public void TakesPositionZeroOnlyOnce()
    {
        var watch = NewWatch();

        // ffmpeg stuck before the first frame repeats the same 0 forever.
        bool stalled = false;
        for (int check = 0; check < Allowed + 1; check++)
        {
            watch.Advance(0);
            stalled = watch.CheckSaysStalled();
        }

        Assert.True(stalled);
    }

    [Fact]
    public void SurvivesAWholeRunThatKeepsMovingSlowly()
    {
        // A two-hour conversion reporting its position every half second: slow is
        // not stalled, and this must never fire on it.
        var watch = NewWatch();

        for (int check = 1; check <= 720; check++)      // two hours of checks
        {
            for (int report = 0; report < 20; report++) // 20 reports per check
            {
                watch.Advance(check * 10.0 + report * 0.5);
            }
            Assert.False(watch.CheckSaysStalled());
        }
    }

    [Fact]
    public void SurvivesARunThatOnlyJustKeepsUp()
    {
        // One report every 17 checks: still inside the allowance, so a very slow
        // but living run is left alone however long it goes on.
        var watch = NewWatch();

        for (int round = 1; round <= 20; round++)
        {
            Assert.False(QuietFor(watch, Allowed - 1));
            watch.Advance(round);
            Assert.False(watch.CheckSaysStalled());
        }
    }

    [Fact]
    public void NeverAllowsFewerThanOneCheck()
    {
        // A nonsense allowance must not make the watchdog fire before it has
        // checked anything at all.
        var watch = new StallWatch(0);

        Assert.True(watch.CheckSaysStalled());
        Assert.Equal(1, watch.QuietChecks);
    }
}
