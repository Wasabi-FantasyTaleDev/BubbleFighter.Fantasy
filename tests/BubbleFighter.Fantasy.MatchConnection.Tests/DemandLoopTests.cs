using BubbleFighter.Fantasy.Contracts.Signals;
using Xunit;

namespace BubbleFighter.Fantasy.MatchConnection.Tests;

/// <summary>The periodic loops must not wake while there is nothing to do, and must never miss the work that arrives.</summary>
public sealed class DemandLoopTests
{
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(10);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "The loop did not reach the expected state in time.");
            await Task.Delay(5);
        }
    }

    private static (Task Run, Func<int> Ticks) Start(PresenceSignal signal, CancellationToken ct, Action? onTick = null)
    {
        var ticks = 0;
        var run = DemandLoop.RunAsync(Period, () => signal.WhenPresent, _ =>
        {
            Interlocked.Increment(ref ticks);
            onTick?.Invoke();
            return ValueTask.CompletedTask;
        }, ct).AsTask();
        return (run, () => Volatile.Read(ref ticks));
    }

    [Fact]
    public async Task NothingTicksWhileNothingIsPresentAndCancellationEndsTheWait()
    {
        var signal = new PresenceSignal();
        using var stop = new CancellationTokenSource();
        var (run, ticks) = Start(signal, stop.Token);
        await Task.Delay(200);
        Assert.Equal(0, ticks());
        Assert.False(run.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task TheLoopTicksWhilePresentSleepsWhenAbsentAndWakesAgain()
    {
        var signal = new PresenceSignal();
        using var stop = new CancellationTokenSource();
        var (run, ticks) = Start(signal, stop.Token);
        signal.Refresh(() => true);
        await Until(() => ticks() >= 3);

        signal.Refresh(() => false);
        // The tick already waiting on the timer may still run; after that the loop is parked.
        await Task.Delay(100);
        var settled = ticks();
        await Task.Delay(200);
        Assert.Equal(settled, ticks());

        signal.Refresh(() => true);
        await Until(() => ticks() >= settled + 3);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    /// <summary>Work that is present before the loop looks is seen: the signal is a level, not an edge.</summary>
    [Fact]
    public async Task WorkThatIsAlreadyPresentIsNotMissed()
    {
        var signal = new PresenceSignal();
        signal.Refresh(() => true);
        using var stop = new CancellationTokenSource();
        var (run, ticks) = Start(signal, stop.Token);
        await Until(() => ticks() >= 2);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task ATickThatEndsTheWorkIsTheLastUntilMoreArrives()
    {
        var signal = new PresenceSignal();
        using var stop = new CancellationTokenSource();
        var (run, ticks) = Start(signal, stop.Token, onTick: () => signal.Refresh(() => false));
        signal.Refresh(() => true);
        await Until(() => ticks() == 1);
        await Task.Delay(150);
        Assert.Equal(1, ticks());
        signal.Refresh(() => true);
        await Until(() => ticks() == 2);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public void TheSignalIsALevelWithOneTaskPerPresence()
    {
        var signal = new PresenceSignal();
        var absent = signal.WhenPresent;
        Assert.False(signal.IsPresent);
        Assert.False(absent.IsCompleted);
        signal.Refresh(() => false);
        Assert.Same(absent, signal.WhenPresent);

        signal.Refresh(() => true);
        Assert.True(signal.IsPresent);
        Assert.True(absent.IsCompletedSuccessfully);
        Assert.Same(absent, signal.WhenPresent);
        signal.Refresh(() => true);
        Assert.Same(absent, signal.WhenPresent);

        signal.Refresh(() => false);
        Assert.False(signal.IsPresent);
        Assert.NotSame(absent, signal.WhenPresent);
        Assert.False(signal.WhenPresent.IsCompleted);
        Assert.True(absent.IsCompletedSuccessfully);
    }

    /// <summary>Owners refresh after each change from many threads; the signal must end on the state that is really there.</summary>
    [Fact]
    public void RacingRefreshesEndOnTheRealState()
    {
        for (var round = 0; round < 50; round++)
        {
            var signal = new PresenceSignal();
            var count = 0;
            Parallel.For(0, 2000, i =>
            {
                if (i % 2 == 0) Interlocked.Increment(ref count);
                else Interlocked.Decrement(ref count);
                signal.Refresh(() => Volatile.Read(ref count) > 0);
            });
            Assert.Equal(0, count);
            Assert.False(signal.IsPresent);
            Assert.False(signal.WhenPresent.IsCompleted);
            Interlocked.Increment(ref count);
            signal.Refresh(() => Volatile.Read(ref count) > 0);
            Assert.True(signal.WhenPresent.IsCompleted);
        }
    }
}
