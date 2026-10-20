using Xunit;

namespace BubbleFighter.Fantasy.Party.Tests;

/// <summary>Pins bounded admission, close-event wakeups and cancellation at the party mutation boundary.</summary>
public sealed class PartyWorkQueueTests
{
    [Fact]
    public async Task FullRequestQueueStillSweepsClosedConnections()
    {
        using var queue = new PartyWorkQueue(1);
        using var lifetime = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        var swept = Signal();
        var closed = 0;
        var worker = queue.RunAsync(_ =>
        {
            if (Volatile.Read(ref closed) != 0) swept.TrySetResult();
            return ValueTask.CompletedTask;
        }, lifetime.Token).AsTask();
        try
        {
            var first = queue.ExecuteAsync(async ct =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }, default).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = queue.ExecuteAsync(_ => ValueTask.CompletedTask, default).AsTask();
            Assert.Throws<InvalidOperationException>(() => queue.ExecuteAsync(_ => ValueTask.CompletedTask, default));
            Volatile.Write(ref closed, 1);
            queue.Wake();
            release.TrySetResult();
            await Task.WhenAll(first, second, swept.Task).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker); }
    }

    [Fact]
    public async Task CancellationSkipsQueuedMutationButDoesNotInterruptCommittedPeerDelivery()
    {
        using var queue = new PartyWorkQueue(1);
        using var lifetime = new CancellationTokenSource();
        using var firstCaller = new CancellationTokenSource();
        using var queuedCaller = new CancellationTokenSource();
        var entered = Signal();
        var release = Signal();
        var delivered = false;
        var mutated = false;
        var worker = queue.RunAsync(_ => ValueTask.CompletedTask, lifetime.Token).AsTask();
        try
        {
            var first = queue.ExecuteAsync(async ct =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                delivered = true;
            }, firstCaller.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = queue.ExecuteAsync(_ => { mutated = true; return ValueTask.CompletedTask; }, queuedCaller.Token).AsTask();
            firstCaller.Cancel();
            queuedCaller.Cancel();
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.True(delivered);
            Assert.False(mutated);
        }
        finally { lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker); }
    }

    [Fact]
    public async Task UnloadCancelsBothRunningAndQueuedRequestsAndRejectsNewWork()
    {
        using var queue = new PartyWorkQueue(1);
        using var lifetime = new CancellationTokenSource();
        var entered = Signal();
        var worker = queue.RunAsync(_ => ValueTask.CompletedTask, lifetime.Token).AsTask();
        var first = queue.ExecuteAsync(async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = queue.ExecuteAsync(_ => throw new InvalidOperationException("Must never run."), default).AsTask();
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Throws<ObjectDisposedException>(() => queue.ExecuteAsync(_ => ValueTask.CompletedTask, default));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
