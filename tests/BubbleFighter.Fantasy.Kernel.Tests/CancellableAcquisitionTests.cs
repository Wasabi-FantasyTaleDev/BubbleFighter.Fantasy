using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class CancellableAcquisitionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static PluginContext Context() => new(
        new PluginRow { Id = "cancel", Plugin = "cancel.test", Source = "test",
            Lifecycle = new PluginLifecycleOptions { EffectCapacity = 3 } },
        new ServiceRegistry(), new EventBus(NullLogger.Instance), NullLogger.Instance);
    private sealed class Resource(Func<ValueTask> release) : IAsyncDisposable
    { public ValueTask DisposeAsync() => release(); }

    [Fact]
    public async Task PreCanceledAcquisitionNeverInvokesFactoryOrConsumesCapacity()
    {
        var root = Context();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => root.AcquireEffectAsync(_ =>
        { calls++; return ValueTask.FromResult<IAsyncDisposable>(new Resource(() => ValueTask.CompletedTask)); }, cancel.Token).AsTask());
        Assert.Equal(0, calls);
        Assert.Equal(0, root.RetainedEffectCount);
        await root.UnwindAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledFactoryMustReturnAndLateResourceMustReleaseBeforeFailure(bool closeParent)
    {
        var root = Context();
        var child = await root.CreateResourceScopeAsync("child");
        using var cancel = new CancellationTokenSource();
        var entered = Signal();
        var result = Signal();
        var releaseEntered = Signal();
        var release = Signal();
        var calls = 0;
        var acquiring = child.AcquireEffectAsync(async token =>
        {
            entered.TrySetResult();
            await result.Task;
            Assert.True(token.IsCancellationRequested);
            return new Resource(async () => { calls++; releaseEntered.TrySetResult(); await release.Task; });
        }, cancel.Token).AsTask();
        await entered.Task.WaitAsync(Limit);
        cancel.Cancel();
        Task? closing = closeParent ? root.UnwindAsync().AsTask() : null;
        try
        {
            Assert.False(acquiring.IsCompleted);
            result.TrySetResult();
            await releaseEntered.Task.WaitAsync(Limit);
            Assert.False(acquiring.IsCompleted);
            Assert.Equal(2, root.RetainedEffectCount);
        }
        finally { result.TrySetResult(); release.TrySetResult(); }
        if (closeParent) await Assert.ThrowsAsync<ObjectDisposedException>(() => acquiring.WaitAsync(Limit));
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquiring.WaitAsync(Limit));
        if (closing is not null) await closing.WaitAsync(Limit);
        else await root.UnwindAsync();
        Assert.Equal(1, calls);
        Assert.Equal(0, root.RetainedEffectCount);
    }

    [Fact]
    public async Task FailedLateCleanupIsRetainedAndNeverRetried()
    {
        var root = Context();
        using var cancel = new CancellationTokenSource();
        var entered = Signal();
        var result = Signal();
        var calls = 0;
        var acquiring = root.AcquireEffectAsync(async _ =>
        {
            entered.TrySetResult(); await result.Task;
            return new Resource(() => { calls++; throw new InvalidOperationException("release-failed"); });
        }, cancel.Token).AsTask();
        await entered.Task.WaitAsync(Limit);
        cancel.Cancel(); result.TrySetResult();
        var error = await Assert.ThrowsAsync<AggregateException>(() => acquiring.WaitAsync(Limit));
        Assert.Contains(error.InnerExceptions, ex => ex is OperationCanceledException);
        Assert.Contains("release-failed", error.ToString());
        Assert.Equal(1, root.RetainedEffectCount);
        await Assert.ThrowsAsync<AggregateException>(() => root.UnwindAsync().AsTask().WaitAsync(Limit));
        await Assert.ThrowsAsync<AggregateException>(() => root.UnwindAsync().AsTask().WaitAsync(Limit));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmittedFactoryTokenStaysLinkedUntilRelease(bool ownerCancellation)
    {
        var root = Context();
        using var cancel = new CancellationTokenSource();
        var canceled = Signal();
        var releases = 0;
        var resource = await root.AcquireEffectAsync(token =>
        {
            var registration = token.Register(() => canceled.TrySetResult());
            return ValueTask.FromResult<IAsyncDisposable>(new Resource(() =>
            { registration.Dispose(); releases++; return ValueTask.CompletedTask; }));
        }, cancel.Token);
        if (ownerCancellation) await root.UnwindAsync();
        else cancel.Cancel();
        await canceled.Task.WaitAsync(Limit);
        if (!ownerCancellation)
        {
            Assert.Equal(0, releases);
            Assert.Equal(1, root.RetainedEffectCount);
        }
        await resource.DisposeAsync();
        await root.UnwindAsync();
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task ThrowingCanceledFactoryReturnsItsReservation()
    {
        var root = Context();
        using var cancel = new CancellationTokenSource();
        var entered = Signal();
        var acquiring = root.AcquireEffectAsync(async token =>
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException(); }, cancel.Token).AsTask();
        await entered.Task.WaitAsync(Limit);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquiring.WaitAsync(Limit));
        Assert.Equal(0, root.RetainedEffectCount);
        await root.UnwindAsync();
    }

    [Fact]
    public async Task ReleaseJoinsAnAlreadyRunningCallerCancellationCallback()
    {
        var root = Context();
        using var cancel = new CancellationTokenSource();
        using var finish = new ManualResetEventSlim();
        var entered = Signal();
        var releasing = Signal();
        await root.AcquireEffectAsync(token =>
        {
            token.Register(() => { entered.TrySetResult(); finish.Wait(); });
            return ValueTask.FromResult<IAsyncDisposable>(new Resource(() =>
            { releasing.TrySetResult(); return ValueTask.CompletedTask; }));
        }, cancel.Token);
        var canceling = Task.Run(cancel.Cancel);
        await entered.Task.WaitAsync(Limit);
        var closing = root.UnwindAsync().AsTask();
        try
        {
            await releasing.Task.WaitAsync(Limit);
            Assert.False(closing.IsCompleted);
            Assert.False(canceling.IsCompleted);
        }
        finally { finish.Set(); }
        await Task.WhenAll(canceling, closing).WaitAsync(Limit);
        Assert.Equal(0, root.RetainedEffectCount);
    }
}
