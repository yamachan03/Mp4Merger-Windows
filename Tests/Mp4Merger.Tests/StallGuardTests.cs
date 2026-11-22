using Mp4Merger.Core;

namespace Mp4Merger.Tests;

/// The piece every long run is wrapped in. The allowance and the check interval
/// are constructor arguments, so these finish in milliseconds instead of minutes.
public class StallGuardTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);

    private static async Task<bool> FiredWithinAsync(StallGuard guard, int millis)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(millis);
        while (DateTime.UtcNow < deadline)
        {
            if (guard.Stalled) return true;
            await Task.Delay(5);
        }
        return guard.Stalled;
    }

    [Fact]
    public async Task FiresWhenNothingIsReported()
    {
        using var guard = new StallGuard(CancellationToken.None, checksAllowed: 3, checkEvery: Fast);

        Assert.True(await FiredWithinAsync(guard, 2000));
        Assert.True(guard.Token.IsCancellationRequested);
        Assert.Null(guard.Position);
    }

    [Fact]
    public async Task LeavesARunThatKeepsMovingAlone()
    {
        using var guard = new StallGuard(CancellationToken.None, checksAllowed: 3, checkEvery: Fast);

        for (int i = 1; i <= 60; i++)
        {
            guard.Advance(i);
            await Task.Delay(5);
        }

        Assert.False(guard.Stalled);
        Assert.False(guard.Token.IsCancellationRequested);
        Assert.Equal(60, guard.Position!.Value, 6);
    }

    [Fact]
    public async Task FiresOnceAMovingRunStops()
    {
        using var guard = new StallGuard(CancellationToken.None, checksAllowed: 3, checkEvery: Fast);

        guard.Advance(42);
        Assert.True(await FiredWithinAsync(guard, 2000));
        // The position it reached is what the message needs.
        Assert.Equal(42, guard.Position!.Value, 6);
    }

    [Fact]
    public async Task TheUsersCancelIsNotCalledAStall()
    {
        // This is the race the message used to lose: whoever got there first won,
        // and a cancel landing in the same moment threw away the position.
        using var source = new CancellationTokenSource();
        using var guard = new StallGuard(source.Token, checksAllowed: 3, checkEvery: Fast);

        source.Cancel();
        await Task.Delay(300);

        Assert.True(guard.Token.IsCancellationRequested);
        Assert.False(guard.Stalled);
    }

    [Fact]
    public async Task CancellingTheUserTokenCancelsTheRun()
    {
        using var source = new CancellationTokenSource();
        using var guard = new StallGuard(source.Token, checksAllowed: 1000, checkEvery: Fast);

        Assert.False(guard.Token.IsCancellationRequested);
        source.Cancel();
        await Task.Delay(50);

        Assert.True(guard.Token.IsCancellationRequested);
    }

    [Fact]
    public void KeepsTheFurthestPositionReported()
    {
        using var guard = new StallGuard(CancellationToken.None, checksAllowed: 1000, checkEvery: Fast);

        guard.Advance(10);
        guard.Advance(5);
        guard.Advance(null);

        Assert.Equal(10, guard.Position!.Value, 6);
    }

    [Fact]
    public async Task StopsCheckingOnceDisposed()
    {
        var guard = new StallGuard(CancellationToken.None, checksAllowed: 3, checkEvery: Fast);
        guard.Dispose();

        // Nothing is reported, so an undisposed guard would have fired by now.
        await Task.Delay(300);

        Assert.False(guard.Stalled);
    }

    [Fact]
    public void TheShippedAllowanceIsThreeMinutesOfChecks()
    {
        // The two values have to stay consistent: the message tells the user
        // "three minutes", so the allowance has to work out to that.
        Assert.Equal(TimeSpan.FromMinutes(3), StallGuard.Limit);
        Assert.Equal(TimeSpan.FromSeconds(10), StallGuard.CheckEvery);
        Assert.Equal(18, (int)(StallGuard.Limit / StallGuard.CheckEvery));
    }
}
