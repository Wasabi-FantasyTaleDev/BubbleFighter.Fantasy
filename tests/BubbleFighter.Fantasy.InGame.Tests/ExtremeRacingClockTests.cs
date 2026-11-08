using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.RoomCore;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Exercises replacement and shutdown of the race's single room-owned deadline.</summary>
public sealed class ExtremeRacingClockTests
{
    /// <summary>Replacing a long wait executes the new callback, which can schedule its successor without reentry.</summary>
    [Fact]
    public async Task ReplacementAndChainedDeadlineStayOnTheRoomExecutor()
    {
        await using var executor = new RoomExecutor(4);
        var failures = new List<Exception>();
        var clock = new RoomDeadlineClock(executor, default, failures.Add);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var obsolete = 0;
        try
        {
            clock.Schedule(60_000, _ => { obsolete++; return ValueTask.CompletedTask; });
            clock.Schedule(0, _ =>
            {
                clock.Schedule(0, _ => { finished.SetResult(); return ValueTask.CompletedTask; });
                return ValueTask.CompletedTask;
            });
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            clock.Cancel();
            await clock.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(0, obsolete);
        Assert.Empty(failures);
    }

    /// <summary>Cancellation drains a deadline even while the executor is occupied; it cannot fire after release.</summary>
    [Fact]
    public async Task CancellationDrainsWithoutWaitingForAnOccupiedExecutor()
    {
        await using var executor = new RoomExecutor(4);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var occupied = executor.ExecuteAsync(async _ => { entered.SetResult(); await release.Task; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var failures = new List<Exception>();
        var clock = new RoomDeadlineClock(executor, default, failures.Add);
        var invoked = 0;
        try
        {
            clock.Schedule(0, _ => { invoked++; return ValueTask.CompletedTask; });
            clock.Cancel();
            await clock.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Schedule(0, _ => { invoked++; return ValueTask.CompletedTask; });
            clock.Cancel();
        }
        finally { release.TrySetResult(); await occupied; }
        await executor.ExecuteAsync(_ => ValueTask.CompletedTask);
        Assert.Equal(0, invoked);
        Assert.Empty(failures);
    }
}
