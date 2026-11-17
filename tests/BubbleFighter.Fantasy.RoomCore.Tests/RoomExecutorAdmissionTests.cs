using Xunit;

namespace BubbleFighter.Fantasy.RoomCore.Tests;

public sealed class RoomExecutorAdmissionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly AsyncLocal<string?> Ambient = new();

    [Fact]
    public async Task FullQueueWaitsWithoutChangingImmediateAdmissionAndPreservesCallerContext()
    {
        await using var executor = new RoomExecutor(1);
        await using var other = new RoomExecutor(1);
        var entered = Signal();
        var release = Signal();
        var sequence = new List<int>();
        var running = executor.ExecuteAsync(async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            sequence.Add(1);
        });
        try
        {
            await entered.Task.WaitAsync(Timeout);
            var queued = executor.ExecuteAsync(_ => { sequence.Add(2); return ValueTask.CompletedTask; });
            Ambient.Value = "waiting-player";
            var waiting = executor.ExecuteWhenAvailableAsync(async ignored =>
            {
                Assert.Equal("waiting-player", Ambient.Value);
                await Task.Yield();
                Assert.Equal("waiting-player", Ambient.Value);
                Assert.Throws<InvalidOperationException>(() =>
                {
                    _ = executor.ExecuteWhenAvailableAsync(_ => ValueTask.CompletedTask);
                });
                Assert.Throws<InvalidOperationException>(() =>
                {
                    _ = other.ExecuteWhenAvailableAsync(_ => ValueTask.CompletedTask);
                });
                sequence.Add(3);
            });
            Ambient.Value = "changed-after-submission";
            Assert.False(waiting.IsCompleted);
            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = executor.ExecuteAsync(_ => ValueTask.CompletedTask);
            });
            release.SetResult();
            await Task.WhenAll(running, queued, waiting).WaitAsync(Timeout);
            Assert.Equal([1, 2, 3], sequence);
        }
        finally
        {
            Ambient.Value = null;
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task CancellationWhileWaitingDoesNotInvokeWorkOrCancelAcceptedOperations()
    {
        await using var executor = new RoomExecutor(1);
        var entered = Signal();
        var release = Signal();
        var running = executor.ExecuteAsync(async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        });
        try
        {
            await entered.Task.WaitAsync(Timeout);
            var acceptedRan = false;
            var queued = executor.ExecuteAsync(_ => { acceptedRan = true; return ValueTask.CompletedTask; });
            using var cancellation = new CancellationTokenSource();
            var waitingRan = false;
            var waiting = executor.ExecuteWhenAvailableAsync(_ =>
            {
                waitingRan = true;
                return ValueTask.CompletedTask;
            }, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Timeout));
            Assert.True(waiting.IsCanceled);
            Assert.False(running.IsCompleted);
            Assert.False(queued.IsCompleted);
            release.SetResult();
            await Task.WhenAll(running, queued).WaitAsync(Timeout);
            await executor.StopAsync().WaitAsync(Timeout);
            Assert.True(acceptedRan);
            Assert.False(waitingRan);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task StopRejectsWaitingAdmissionAndStillWaitsForRunningCleanup()
    {
        await using var executor = new RoomExecutor(1);
        var entered = Signal();
        var canceled = Signal();
        var cleanup = Signal();
        var running = executor.ExecuteAsync(async token =>
        {
            entered.SetResult();
            try { await Task.Delay(System.Threading.Timeout.Infinite, token); }
            finally
            {
                canceled.SetResult();
                await cleanup.Task.WaitAsync(Timeout);
            }
        });
        try
        {
            await entered.Task.WaitAsync(Timeout);
            var invoked = false;
            var queued = executor.ExecuteAsync(_ => { invoked = true; return ValueTask.CompletedTask; });
            var waiting = executor.ExecuteWhenAvailableAsync(_ => { invoked = true; return ValueTask.CompletedTask; });
            var stopping = executor.StopAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(Timeout));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
            await canceled.Task.WaitAsync(Timeout);
            Assert.False(stopping.IsCompleted);
            Assert.False(invoked);
            Assert.Throws<ObjectDisposedException>(() =>
            {
                _ = executor.ExecuteWhenAvailableAsync(_ => ValueTask.CompletedTask);
            });
            cleanup.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Timeout));
            await stopping.WaitAsync(Timeout);
        }
        finally { cleanup.TrySetResult(); }
    }

    [Fact]
    public async Task CancellationAfterExecutionStartsWaitsForCallbackCleanup()
    {
        await using var executor = new RoomExecutor(1);
        using var cancellation = new CancellationTokenSource();
        var entered = Signal();
        var canceled = Signal();
        var cleanup = Signal();
        var operation = executor.ExecuteWhenAvailableAsync(async token =>
        {
            entered.SetResult();
            try { await Task.Delay(System.Threading.Timeout.Infinite, token); }
            finally
            {
                canceled.SetResult();
                await cleanup.Task.WaitAsync(Timeout);
            }
        }, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(Timeout);
            cancellation.Cancel();
            await canceled.Task.WaitAsync(Timeout);
            Assert.False(operation.IsCompleted);
            cleanup.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(Timeout));
        }
        finally { cleanup.TrySetResult(); }
    }

    [Fact]
    public async Task SuppressedContextDoesNotReappearAfterWaitingForCapacity()
    {
        await using var executor = new RoomExecutor(1);
        var entered = Signal();
        var release = Signal();
        var running = executor.ExecuteAsync(async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        });
        try
        {
            await entered.Task.WaitAsync(Timeout);
            var queued = executor.ExecuteAsync(_ => ValueTask.CompletedTask);
            Ambient.Value = "must-not-flow";
            Task waiting;
            using (ExecutionContext.SuppressFlow())
                waiting = executor.ExecuteWhenAvailableAsync(async _ =>
                {
                    Assert.Null(Ambient.Value);
                    await Task.Yield();
                    Assert.Null(Ambient.Value);
                });
            Assert.False(waiting.IsCompleted);
            release.SetResult();
            await Task.WhenAll(running, queued, waiting).WaitAsync(Timeout);
        }
        finally
        {
            Ambient.Value = null;
            release.TrySetResult();
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
