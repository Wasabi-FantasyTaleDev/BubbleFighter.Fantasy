using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class ResourceScopeSafetyTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static PluginContext Context() => new(
        new PluginRow { Id = Guid.NewGuid().ToString("N"), Plugin = "scope.test", Source = "test" },
        new ServiceRegistry(), new EventBus(NullLogger.Instance), NullLogger.Instance);
    private sealed class Resource(Func<ValueTask> release) : IAsyncDisposable
    { public ValueTask DisposeAsync() => release(); }

    [Theory]
    [InlineData("factory")]
    [InlineData("work")]
    [InlineData("release")]
    public async Task DescendantCallbacksCannotWaitForTheirAncestors(string phase)
    {
        var root = Context();
        var child = await root.CreateResourceScopeAsync("child");
        var leaf = await child.CreateResourceScopeAsync("leaf");
        var checks = 0;
        async ValueTask Check()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => root.UnwindAsync().AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => child.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => leaf.DisposeAsync().AsTask());
            Interlocked.Increment(ref checks);
        }
        if (phase == "work")
        {
            var task = await leaf.RunTaskAsync("self-wait", _ => Check());
            await task.Completion.WaitAsync(Limit);
        }
        else
        {
            await leaf.AcquireEffectAsync(async _ =>
            {
                if (phase == "factory") await Check();
                return new Resource(phase == "release" ? Check : () => ValueTask.CompletedTask);
            });
        }
        await root.UnwindAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(1, checks);
        Assert.Equal(0, root.RetainedEffectCount);
    }

    [Fact]
    public async Task SiblingClosureDoesNotTriggerAnAncestorSelfWaitError()
    {
        var root = Context();
        var left = await root.CreateResourceScopeAsync("left");
        var right = await root.CreateResourceScopeAsync("right");
        var releases = 0;
        right.Effect(() => releases++);
        var task = await left.RunTaskAsync("close-sibling", _ => right.DisposeAsync());
        await task.Completion.WaitAsync(Limit);
        Assert.Equal(1, releases);
        await root.UnwindAsync();
    }

    [Fact]
    public async Task OwnershipHandlesCannotBeReacquiredOrMovedBetweenScopes()
    {
        var root = Context();
        var child = await root.CreateResourceScopeAsync("child");
        var releases = 0;
        var sync = child.Effect(() => releases++);
        Assert.Same(sync, child.Effect(sync));
        Assert.Throws<InvalidOperationException>(() => root.Effect(sync));
        var raw = await child.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(
            new Resource(() => { releases++; return ValueTask.CompletedTask; })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => root.AcquireEffectAsync(_ => ValueTask.FromResult(raw)).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => child.AcquireEffectAsync(_ => ValueTask.FromResult(raw)).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => root.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(child)).AsTask());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = await child.RunTaskAsync("owned", async token =>
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); });
        await entered.Task.WaitAsync(Limit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => root.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(work)).AsTask());
        Assert.Equal(0, releases);
        Assert.Equal(4, root.RetainedEffectCount);
        await root.UnwindAsync();
        Assert.Equal(2, releases);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.Completion.WaitAsync(Limit));
    }

    [Fact]
    public async Task ParentCloseRacingChildCreationLeavesNoOwnedResourcesOrScopeMetadata()
    {
        for (var i = 0; i < 100; i++)
        {
            var root = Context();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var create = Task.Run(async () =>
            {
                await gate.Task;
                try
                {
                    var child = await root.CreateResourceScopeAsync("racing");
                    await child.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new Resource(() => ValueTask.CompletedTask)));
                }
                catch (ObjectDisposedException) { }
            });
            var close = Task.Run(async () => { await gate.Task; await root.UnwindAsync(); });
            gate.TrySetResult();
            await Task.WhenAll(create, close).WaitAsync(Limit);
            Assert.Equal(0, root.RetainedEffectCount);
            Assert.Equal(0, root.ResourceScopeCount);
        }
    }
}
