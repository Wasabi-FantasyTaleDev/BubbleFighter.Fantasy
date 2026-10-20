using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class ResourceScopeHostTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly ConcurrentDictionary<string, Func<IPluginContext, ValueTask>> Plans = new();
    public interface IValue { int Number { get; } }
    private sealed record Value(int Number = 9) : IValue;
    [Plugin("scope.provider", Provides = [typeof(IValue)])]
    public sealed class Provider : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        { context.Services.Register<IValue>(new Value()); return ValueTask.CompletedTask; }
    }
    [Plugin("scope.consumer", Requires = [typeof(IValue)])]
    public sealed class Consumer : IPlugin
    { public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken) => Plans[context.Row.Id](context); }
    private sealed class Resource(Func<ValueTask> release) : IAsyncDisposable
    { public ValueTask DisposeAsync() => release(); }

    private static (PluginHost Host, string Parent, string Child) Host(Func<IPluginContext, ValueTask> activate, int timeout = 5000)
    {
        var parent = Guid.NewGuid().ToString("N");
        var child = Guid.NewGuid().ToString("N");
        Plans[child] = activate;
        return (new PluginHost([
            new PluginRow { Id = parent, Plugin = "scope.provider", Source = "test" },
            new PluginRow { Id = child, Plugin = "scope.consumer", Source = "test",
                Lifecycle = new PluginLifecycleOptions { TeardownTimeoutMs = timeout } }],
            PluginCatalog.ScanAssemblies([typeof(ResourceScopeHostTests).Assembly]), NullLoggerFactory.Instance), parent, child);
    }

    [Theory]
    [InlineData("work-only")]
    [InlineData("empty-aggregate")]
    [InlineData("foreign-proof")]
    public async Task NestedCleanupRebasesOnlyProvenDrainedWorkFailures(string failureKind)
    {
        var fail = Signal();
        IOwnedTask? work = null;
        var cleanupCalls = 0;
        var h = Host(async context =>
        {
            var child = await context.CreateResourceScopeAsync("child");
            var leaf = await child.CreateResourceScopeAsync("leaf");
            work = await leaf.RunTaskAsync("nested-fault", async _ =>
            { await fail.Task; throw new InvalidOperationException("work-broke"); });
            if (failureKind == "empty-aggregate") leaf.Effect(() => { cleanupCalls++; throw new AggregateException(); });
            if (failureKind == "foreign-proof")
                await leaf.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new Resource(async () =>
                { cleanupCalls++; await work.DisposeAsync(); })));
        });
        try
        {
            await h.Host.StartAsync(default);
            fail.TrySetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(() => work!.Completion.WaitAsync(Limit));
            Assert.Equal(PluginState.WorkFailed, h.Host.Instances.Single(r => r.RowId == h.Child).State);
            await Assert.ThrowsAsync<AggregateException>(() => h.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit));
            if (failureKind == "work-only")
            {
                Assert.Equal(PluginState.Failed, h.Host.Instances.Single(r => r.RowId == h.Child).State);
                Assert.Equal(PluginState.Unloaded, h.Host.Instances.Single(r => r.RowId == h.Parent).State);
            }
            else
            {
                Assert.Equal(PluginState.CleanupFailed, h.Host.Instances.Single(r => r.RowId == h.Child).State);
                Assert.Equal(PluginState.Active, h.Host.Instances.Single(r => r.RowId == h.Parent).State);
                await Assert.ThrowsAsync<AggregateException>(() => h.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit));
                Assert.Equal(1, cleanupCalls);
            }
        }
        finally { fail.TrySetResult(); Plans.TryRemove(h.Child, out _); }
    }

    [Fact]
    public async Task TimedOutChildTaskRetainsProviderUntilTheOriginalDrainFinishes()
    {
        var entered = Signal();
        var finish = Signal();
        IOwnedTask? task = null;
        var h = Host(async context =>
        {
            var child = await context.CreateResourceScopeAsync("uncooperative");
            task = await child.RunTaskAsync("slow", async _ =>
            { entered.TrySetResult(); await finish.Task; Assert.Equal(9, context.Services.Get<IValue>().Number); });
        }, 50);
        try
        {
            await h.Host.StartAsync(default);
            await entered.Task.WaitAsync(Limit);
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Host.UnloadAsync(h.Child, default).AsTask().WaitAsync(Limit));
            Assert.Equal(PluginState.CleanupIncomplete, h.Host.Instances.Single(r => r.RowId == h.Child).State);
            Assert.Equal(PluginState.Active, h.Host.Instances.Single(r => r.RowId == h.Parent).State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Host.UnloadAsync(h.Parent, default).AsTask());
            Assert.False(task!.Completion.IsCompleted);
        }
        finally { finish.TrySetResult(); }
        try
        {
            await task!.Completion.WaitAsync(Limit);
            await h.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
            Assert.All(h.Host.Instances, row => Assert.Equal(PluginState.Unloaded, row.State));
        }
        finally { Plans.TryRemove(h.Child, out _); }
    }
}
