using BubbleFighter.Fantasy.RoomCore;
using Xunit;

namespace BubbleFighter.Fantasy.RoomCore.Tests;

public sealed class RoomExecutorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CapacityMustBePositive(int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RoomExecutor(capacity));

    [Fact]
    public async Task CapacityExcludesRunningOperationAndRejectsOverflow()
    {
        await using var executor = new RoomExecutor(2);
        var entered = Signal();
        var release = Signal();
        var sequence = new List<int>();
        var first = executor.ExecuteAsync(async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            sequence.Add(1);
        });
        await entered.Task.WaitAsync(Timeout);
        var second = executor.ExecuteAsync(_ => { sequence.Add(2); return ValueTask.CompletedTask; });
        var third = executor.ExecuteAsync(_ => { sequence.Add(3); return ValueTask.CompletedTask; });
        Assert.Throws<InvalidOperationException>(() => { _ = executor.ExecuteAsync(_ => ValueTask.CompletedTask); });
        release.SetResult();
        await Task.WhenAll(first, second, third).WaitAsync(Timeout);
        Assert.Equal([1, 2, 3], sequence);
    }

    [Fact]
    public async Task ConcurrentProducersNeverOverlapAcrossAwait()
    {
        await using var executor = new RoomExecutor(128);
        var running = 0;
        var completed = 0;
        var submissions = Enumerable.Range(0, 128).Select(_ => Task.Run(async () =>
            await executor.ExecuteAsync(async token =>
            {
                Assert.Equal(1, Interlocked.Increment(ref running));
                await Task.Yield();
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref completed);
                Assert.Equal(0, Interlocked.Decrement(ref running));
            }))).ToArray();
        await Task.WhenAll(submissions).WaitAsync(Timeout);
        Assert.Equal(128, completed);
    }

    [Fact]
    public async Task OperationFailureIsObservedWithoutPoisoningNextOperation()
    {
        await using var executor = new RoomExecutor(2);
        var expected = new FormatException("Invalid room operation.");
        var failed = executor.ExecuteAsync(_ => throw expected);
        Assert.Same(expected, await Assert.ThrowsAsync<FormatException>(() => failed));
        var ran = false;
        await executor.ExecuteAsync(_ => { ran = true; return ValueTask.CompletedTask; }).WaitAsync(Timeout);
        Assert.True(ran);
    }

    [Fact]
    public async Task UnrelatedCancellationExceptionIsAFault()
    {
        await using var executor = new RoomExecutor(1);
        var failed = executor.ExecuteAsync(_ => throw new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failed);
        Assert.True(failed.IsFaulted);
    }

    [Fact]
    public async Task QueuedCallerCancellationCompletesBeforeRunningWorkAndNeverInvokesCallback()
    {
        await using var executor = new RoomExecutor(1);
        var entered = Signal();
        var release = Signal();
        var running = executor.ExecuteAsync(async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        });
        await entered.Task.WaitAsync(Timeout);
        using var cancellation = new CancellationTokenSource();
        var invoked = false;
        var queued = executor.ExecuteAsync(_ => { invoked = true; return ValueTask.CompletedTask; }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
        Assert.False(running.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => { _ = executor.ExecuteAsync(_ => ValueTask.CompletedTask); });
        release.SetResult();
        await running.WaitAsync(Timeout);
        await executor.StopAsync().WaitAsync(Timeout);
        Assert.False(invoked);
    }

    [Fact]
    public async Task RunningCancellationWaitsForCallbackCleanup()
    {
        await using var executor = new RoomExecutor(1);
        var entered = Signal();
        var canceled = Signal();
        var cleanup = Signal();
        using var cancellation = new CancellationTokenSource();
        var operation = executor.ExecuteAsync(async token =>
        {
            entered.SetResult();
            try { await Task.Delay(System.Threading.Timeout.Infinite, token); }
            finally
            {
                canceled.SetResult();
                await cleanup.Task.WaitAsync(Timeout);
            }
        }, cancellation.Token);
        await entered.Task.WaitAsync(Timeout);
        cancellation.Cancel();
        await canceled.Task.WaitAsync(Timeout);
        Assert.False(operation.IsCompleted);
        cleanup.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(Timeout));
    }

    [Fact]
    public async Task StopCancelsQueueAndRunningWorkRejectsAdmissionAndWaitsForCleanup()
    {
        await using var executor = new RoomExecutor(2);
        var entered = Signal();
        var canceled = Signal();
        var cleanup = Signal();
        var operation = executor.ExecuteAsync(async token =>
        {
            entered.SetResult();
            try { await Task.Delay(System.Threading.Timeout.Infinite, token); }
            finally
            {
                canceled.SetResult();
                await cleanup.Task.WaitAsync(Timeout);
            }
        });
        await entered.Task.WaitAsync(Timeout);
        var invoked = false;
        var queued = executor.ExecuteAsync(_ => { invoked = true; return ValueTask.CompletedTask; });
        var stopped = executor.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
        await canceled.Task.WaitAsync(Timeout);
        Assert.False(stopped.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => { _ = executor.ExecuteAsync(_ => ValueTask.CompletedTask); });
        cleanup.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(Timeout));
        await stopped.WaitAsync(Timeout);
        Assert.False(invoked);
        await executor.StopAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task CancelingStopWaitDoesNotPretendUncooperativeWorkHasStopped()
    {
        await using var executor = new RoomExecutor(1);
        var entered = Signal();
        var release = Signal();
        var operation = executor.ExecuteAsync(async ignored => { entered.SetResult(); await release.Task.WaitAsync(Timeout); });
        await entered.Task.WaitAsync(Timeout);
        using var waitCancellation = new CancellationTokenSource();
        var waiting = executor.StopAsync(waitCancellation.Token);
        waitCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Timeout));
        Assert.False(executor.StopAsync().IsCompleted);
        release.SetResult();
        await operation.WaitAsync(Timeout);
        await executor.StopAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task SameExecutorEnqueueStopAndDisposeAreRejectedInsideCallback()
    {
        await using var executor = new RoomExecutor(1);
        await executor.ExecuteAsync(async ignored =>
        {
            await Task.Yield();
            Assert.Throws<InvalidOperationException>(() => { _ = executor.ExecuteAsync(_ => ValueTask.CompletedTask); });
            Assert.Throws<InvalidOperationException>(() => { _ = executor.StopAsync(); });
            await Assert.ThrowsAsync<InvalidOperationException>(() => executor.DisposeAsync().AsTask());
        }).WaitAsync(Timeout);
        await executor.ExecuteAsync(_ => ValueTask.CompletedTask).WaitAsync(Timeout);
    }

    [Fact]
    public async Task CrossExecutorSubmissionAndStopAreRejected()
    {
        await using var first = new RoomExecutor(1);
        await using var second = new RoomExecutor(1);
        await first.ExecuteAsync(async ignored =>
        {
            await Task.Yield();
            Assert.Throws<InvalidOperationException>(() => { _ = second.ExecuteAsync(_ => ValueTask.CompletedTask); });
            Assert.Throws<InvalidOperationException>(() => { _ = second.StopAsync(); });
        }).WaitAsync(Timeout);
        await second.ExecuteAsync(_ => ValueTask.CompletedTask).WaitAsync(Timeout);
    }

    [Fact]
    public async Task IndependentCallbacksCannotCreateReciprocalWaits()
    {
        await using var first = new RoomExecutor(1);
        await using var second = new RoomExecutor(1);
        var firstEntered = Signal();
        var secondEntered = Signal();
        var firstWork = first.ExecuteAsync(async ignored =>
        {
            firstEntered.SetResult();
            await secondEntered.Task.WaitAsync(Timeout);
            Assert.Throws<InvalidOperationException>(() => { _ = second.ExecuteAsync(_ => ValueTask.CompletedTask); });
        });
        var secondWork = second.ExecuteAsync(async ignored =>
        {
            secondEntered.SetResult();
            await firstEntered.Task.WaitAsync(Timeout);
            Assert.Throws<InvalidOperationException>(() => { _ = first.ExecuteAsync(_ => ValueTask.CompletedTask); });
        });
        await Task.WhenAll(firstWork, secondWork).WaitAsync(Timeout);
    }

    [Fact]
    public async Task AlreadyCanceledSubmissionDoesNotConsumeCapacity()
    {
        await using var executor = new RoomExecutor(1);
        Assert.Throws<OperationCanceledException>(() =>
        {
            _ = executor.ExecuteAsync(_ => throw new InvalidOperationException("Must not run."), new CancellationToken(true));
        });
        await executor.ExecuteAsync(_ => ValueTask.CompletedTask).WaitAsync(Timeout);
    }

    [Fact]
    public async Task ConcurrentAdmissionAndShutdownLeaveNoAcceptedTaskIncomplete()
    {
        var executor = new RoomExecutor(64);
        var start = Signal();
        var attempted = Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            Task accepted;
            try { accepted = executor.ExecuteAsync(async token => { await Task.Yield(); token.ThrowIfCancellationRequested(); }); }
            catch (ObjectDisposedException) { return; }
            try { await accepted; }
            catch (OperationCanceledException) { }
        })).ToArray();
        var stopping = Task.Run(async () => { await start.Task; await executor.StopAsync(); });
        start.SetResult();
        await Task.WhenAll(attempted.Append(stopping)).WaitAsync(Timeout);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => executor.DisposeAsync().AsTask())).WaitAsync(Timeout);
    }

    [Fact]
    public async Task ThrowingCancellationCallbackIsObservableAndDoesNotSkipConsumerDrain()
    {
        var executor = new RoomExecutor(1);
        var entered = Signal();
        var canceled = Signal();
        var release = Signal();
        var operation = executor.ExecuteAsync(async token =>
        {
            using var registration = token.Register(() => { canceled.SetResult(); throw new FormatException("Cancellation failed."); });
            entered.SetResult();
            await release.Task.WaitAsync(Timeout);
        });
        await entered.Task.WaitAsync(Timeout);
        var stopping = executor.StopAsync();
        await canceled.Task.WaitAsync(Timeout);
        Assert.False(stopping.IsCompleted);
        release.SetResult();
        await operation.WaitAsync(Timeout);
        await Assert.ThrowsAnyAsync<Exception>(() => stopping.WaitAsync(Timeout));
        // Disposal observes the same shutdown error, so consume it explicitly below.
        await Assert.ThrowsAnyAsync<Exception>(() => executor.DisposeAsync().AsTask());
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
