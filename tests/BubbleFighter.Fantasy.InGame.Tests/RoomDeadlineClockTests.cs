using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.RoomCore;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks that a rejected mode deadline remains owned until its failure cleanup finishes.</summary>
public sealed class RoomDeadlineClockTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>A full room queue rejects the deadline and clock completion waits for asynchronous cancellation cleanup.</summary>
    [Fact]
    public async Task QueueRejectionAwaitsFailureCleanupBeforeClockCompletion()
    {
        await using var executor = new RoomExecutor(1);
        var runningEntered = Signal();
        var releaseRunning = Signal();
        var failureEntered = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = Signal();
        var stepCalls = 0;
        var cleanupCalls = 0;
        RoomDeadlineClock? clock = null;
        var queued = Task.CompletedTask;
        var running = executor.ExecuteAsync(async ct =>
        {
            runningEntered.SetResult();
            await releaseRunning.Task.WaitAsync(ct);
        });
        try
        {
            await runningEntered.Task.WaitAsync(Timeout);
            queued = executor.ExecuteAsync(_ => ValueTask.CompletedTask);
            clock = new RoomDeadlineClock(executor, CancellationToken.None, (Func<Exception, ValueTask>)(async failure =>
            {
                failureEntered.SetResult(failure);
                await releaseCleanup.Task.WaitAsync(Timeout);
                Interlocked.Increment(ref cleanupCalls);
            }));
            clock.Schedule(0, _ =>
            {
                Interlocked.Increment(ref stepCalls);
                return ValueTask.CompletedTask;
            });

            var failure = await failureEntered.Task.WaitAsync(Timeout);
            Assert.IsType<InvalidOperationException>(failure);
            Assert.Equal("The room operation queue is full.", failure.Message);
            Assert.Equal(0, Volatile.Read(ref stepCalls));
            Assert.False(queued.IsCompleted);
            clock.Cancel();
            Assert.False(clock.Completion.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref cleanupCalls));

            releaseCleanup.SetResult();
            await clock.Completion.WaitAsync(Timeout);
            Assert.Equal(1, Volatile.Read(ref cleanupCalls));
            Assert.Equal(0, Volatile.Read(ref stepCalls));
        }
        finally
        {
            releaseCleanup.TrySetResult();
            releaseRunning.TrySetResult();
            clock?.Cancel();
            await Task.WhenAll(running, queued, clock?.Completion ?? Task.CompletedTask).WaitAsync(Timeout);
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
