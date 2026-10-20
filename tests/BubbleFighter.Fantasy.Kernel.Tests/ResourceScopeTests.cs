using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class ResourceScopeTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static PluginContext Context(int capacity = 8) => new(
        new PluginRow { Id = Guid.NewGuid().ToString("N"), Plugin = "scope.test", Source = "test",
            Lifecycle = new PluginLifecycleOptions { EffectCapacity = capacity } },
        new ServiceRegistry(), new EventBus(NullLogger.Instance), NullLogger.Instance);
    private sealed class Resource(Func<ValueTask> release) : IAsyncDisposable
    { public ValueTask DisposeAsync() => release(); }

    [Fact]
    public async Task ChildrenAndTheirResourcesShareOneActivationCapacity()
    {
        var root = Context(3);
        var left = await root.CreateResourceScopeAsync("left");
        var right = await root.CreateResourceScopeAsync("right");
        var resource = await left.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new Resource(() => ValueTask.CompletedTask)));
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => right.AcquireEffectAsync(_ =>
        { calls++; return ValueTask.FromResult<IAsyncDisposable>(new Resource(() => ValueTask.CompletedTask)); }).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => right.CreateResourceScopeAsync("over-budget").AsTask());
        Assert.Equal(0, calls);
        Assert.Equal(3, root.RetainedEffectCount);
        Assert.Equal(3, root.ResourceScopeCount);
        await resource.DisposeAsync();
        var work = await right.RunTaskAsync("slot-reuse", _ => ValueTask.CompletedTask);
        await work.Completion.WaitAsync(Limit);
        Assert.Equal(2, root.RetainedEffectCount);
        await root.UnwindAsync();
        Assert.Equal(0, root.RetainedEffectCount);
        Assert.Equal(0, root.ResourceScopeCount);
    }

    [Fact]
    public async Task ParentCancelsDescendantsBeforeWaitingForAnUnrelatedLateDisposer()
    {
        var root = Context();
        var child = await root.CreateResourceScopeAsync("child");
        var grandchild = await child.CreateResourceScopeAsync("grandchild");
        var entered = Signal();
        var finish = Signal();
        var canceled = Signal();
        using var cancellation = grandchild.Lifetime.Register(() => canceled.TrySetResult());
        var order = new ConcurrentQueue<string>();
        child.Effect(() => order.Enqueue("child"));
        grandchild.Effect(() => order.Enqueue("grandchild"));
        await root.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new Resource(async () =>
        { entered.TrySetResult(); await finish.Task; order.Enqueue("last-root"); })));
        var closing = root.UnwindAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            await canceled.Task.WaitAsync(Limit);
            Assert.True(child.Lifetime.IsCancellationRequested);
            Assert.False(closing.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => child.CreateResourceScopeAsync("too-late").AsTask());
            var calls = 0;
            await Assert.ThrowsAsync<ObjectDisposedException>(() => grandchild.AcquireEffectAsync(_ =>
            { calls++; return ValueTask.FromResult<IAsyncDisposable>(new Resource(() => ValueTask.CompletedTask)); }).AsTask());
            Assert.Equal(0, calls);
        }
        finally { finish.TrySetResult(); }
        await closing.WaitAsync(Limit);
        Assert.Equal(new[] { "last-root", "child", "grandchild" }, order);
        Assert.Equal(0, root.RetainedEffectCount);
    }

    [Fact]
    public async Task EarlyChildClosureAndParentClosureJoinOneRelease()
    {
        var root = Context();
        var child = await root.CreateResourceScopeAsync("early");
        var entered = Signal();
        var finish = Signal();
        var calls = 0;
        await child.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new Resource(async () =>
        { Interlocked.Increment(ref calls); entered.TrySetResult(); await finish.Task; })));
        var first = child.DisposeAsync().AsTask();
        await entered.Task.WaitAsync(Limit);
        var second = child.DisposeAsync().AsTask();
        var parent = root.UnwindAsync().AsTask();
        try { Assert.False(parent.IsCompleted); Assert.False(second.IsCompleted); }
        finally { finish.TrySetResult(); }
        await Task.WhenAll(first, second, parent).WaitAsync(Limit);
        Assert.Equal(1, calls);
        Assert.Equal(0, root.RetainedEffectCount);
    }

    [Fact]
    public async Task RepeatedChildCreationRecoversBothSlotsAndScopeMetadata()
    {
        var root = Context(3);
        for (var i = 0; i < 500; i++)
        {
            var child = await root.CreateResourceScopeAsync("iteration");
            var grandchild = await child.CreateResourceScopeAsync("nested");
            grandchild.Effect(() => { });
            await child.DisposeAsync();
            Assert.Equal(0, root.RetainedEffectCount);
            Assert.Equal(1, root.ResourceScopeCount);
        }
        await root.UnwindAsync();
    }

    [Fact]
    public async Task DeepHierarchyCancellationDoesNotUseRecursiveLinkedTokens()
    {
        var root = Context(1200);
        IResourceScope scope = await root.CreateResourceScopeAsync("0");
        for (var i = 1; i < 1000; i++) scope = await scope.CreateResourceScopeAsync(i.ToString());
        var token = scope.Lifetime;
        await root.UnwindAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(0, root.RetainedEffectCount);
        Assert.Equal(0, root.ResourceScopeCount);
    }

    [Fact]
    public async Task FailedChildCancellationRetainsItsParentSlotWithoutRetry()
    {
        var root = Context(1);
        var child = await root.CreateResourceScopeAsync("failed-cancellation");
        var calls = 0;
        using var callback = child.Lifetime.Register(() =>
        { calls++; throw new InvalidOperationException("cancellation-failed"); });
        await Assert.ThrowsAsync<AggregateException>(() => child.DisposeAsync().AsTask().WaitAsync(Limit));
        Assert.Equal(1, root.RetainedEffectCount);
        Assert.Equal(1, root.ResourceScopeCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => root.CreateResourceScopeAsync("over-budget").AsTask());
        await Assert.ThrowsAsync<AggregateException>(() => root.UnwindAsync().AsTask().WaitAsync(Limit));
        Assert.Equal(1, calls);
        Assert.Equal(0, root.ResourceScopeCount);
    }
}
