using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class PluginAsyncLifecycleTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, Func<IPluginContext, CancellationToken, ValueTask>> Actions = new();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public interface IParent { }
    public sealed class Parent : IParent { }

    [Plugin("async.probe", Requires = [typeof(IPluginHost)])]
    public sealed class ProbePlugin : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken) => Actions[context.Row.Id](context, cancellationToken);
    }

    [Plugin("async.consumer", Requires = [typeof(IParent)])]
    public sealed class ConsumerPlugin : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken) => Actions[context.Row.Id](context, cancellationToken);
    }

    [Plugin("async.parent", Provides = [typeof(IParent)])]
    public sealed class ParentPlugin : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Register<IParent>(new Parent()));
            return ValueTask.CompletedTask;
        }
    }

    private static PluginRow Row(string plugin = "async.probe", int timeout = 1000) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Plugin = plugin, Source = "test",
        Lifecycle = new() { EffectCapacity = 16, TeardownTimeoutMs = timeout },
    };
    private static PluginHost Host(params PluginRow[] rows) => new(rows,
        PluginCatalog.ScanAssemblies([typeof(PluginAsyncLifecycleTests).Assembly]), NullLoggerFactory.Instance);
    private static PluginState State(PluginHost host, PluginRow row) => host.Instances.Single(info => info.RowId == row.Id).State;
    private sealed class Resource(Func<ValueTask> release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => release();
    }

    [Fact]
    public async Task HostWaitsForMixedEffectsAndShowsUnloadingWhileCleanupRuns()
    {
        var row = Row(timeout: 5000);
        var entered = Signal();
        var finish = Signal();
        var order = new List<int>();
        Actions[row.Id] = async (context, _) =>
        {
            context.Effect(() => order.Add(1));
            await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new Resource(async () =>
            {
                Assert.True(context.Lifetime.IsCancellationRequested);
                order.Add(2); entered.SetResult(); await finish.Task;
            })));
            context.Effect(() => order.Add(3));
        };
        var host = Host(row);
        Task? unload = null;
        try
        {
            await host.StartAsync(default);
            unload = host.UnloadAsync(row.Id, default).AsTask();
            await entered.Task.WaitAsync(Limit);
            Assert.Equal(PluginState.Unloading, State(host, row));
            Assert.False(unload.IsCompleted);
            Assert.Equal(new[] { 3, 2 }, order);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync(row.Id, default).AsTask());
        }
        finally { finish.TrySetResult(); }
        try
        {
            await unload!.WaitAsync(Limit);
            Assert.Equal(new[] { 3, 2, 1 }, order);
            Assert.Equal(PluginState.Unloaded, State(host, row));
        }
        finally { Actions.TryRemove(row.Id, out _); }
    }

    [Fact]
    public async Task SynchronouslyBlockedCleanupTimesOutAndRetainsProviderUntilAJoinSucceeds()
    {
        var row = Row("async.consumer", 100);
        var parent = Row("async.parent");
        var entered = Signal();
        using var finish = new ManualResetEventSlim();
        var calls = 0;
        Actions[row.Id] = (context, _) =>
        {
            context.Effect(() => { Interlocked.Increment(ref calls); entered.SetResult(); Assert.True(finish.Wait(Limit)); });
            return ValueTask.CompletedTask;
        };
        var host = Host(parent, row);
        try
        {
            await host.StartAsync(default);
            var unload = host.UnloadAsync(row.Id, default).AsTask();
            await entered.Task.WaitAsync(Limit);
            await Assert.ThrowsAsync<InvalidOperationException>(() => unload.WaitAsync(Limit));
            Assert.Equal(PluginState.CleanupIncomplete, State(host, row));
            Assert.Equal(PluginState.Active, State(host, parent));
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.UnloadAsync(parent.Id, default).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync(row.Id, default).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(default).AsTask());
        }
        finally { finish.Set(); }
        try
        {
            await host.UnloadAsync(row.Id, default).AsTask().WaitAsync(Limit);
            await host.ShutdownAsync(default);
            Assert.Equal(1, calls);
            Assert.All(host.Instances, info => Assert.Equal(PluginState.Unloaded, info.State));
        }
        finally { Actions.TryRemove(row.Id, out _); }
    }

    [Fact]
    public async Task LateBlockedFactoryIsOwnedAndItsResultIsReleasedAfterTimeout()
    {
        var row = Row(timeout: 100);
        var entered = Signal();
        using var finish = new ManualResetEventSlim();
        Task<IAsyncDisposable>? acquisition = null;
        var released = 0;
        Actions[row.Id] = (context, _) =>
        {
            acquisition = context.AcquireEffectAsync(_ =>
            {
                entered.SetResult(); Assert.True(finish.Wait(Limit));
                return ValueTask.FromResult<IAsyncDisposable>(new Resource(() =>
                {
                    Interlocked.Increment(ref released); return ValueTask.CompletedTask;
                }));
            }).AsTask();
            return ValueTask.CompletedTask;
        };
        var host = Host(row);
        try
        {
            await host.StartAsync(default).AsTask().WaitAsync(Limit);
            await entered.Task.WaitAsync(Limit);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.UnloadAsync(row.Id, default).AsTask().WaitAsync(Limit));
            Assert.Equal(PluginState.CleanupIncomplete, State(host, row));
            Assert.Equal(0, released);
        }
        finally { finish.Set(); }
        try
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => acquisition!.WaitAsync(Limit));
            await host.UnloadAsync(row.Id, default).AsTask().WaitAsync(Limit);
            Assert.Equal(1, released);
            Assert.Equal(PluginState.Unloaded, State(host, row));
        }
        finally { Actions.TryRemove(row.Id, out _); }
    }

    [Fact]
    public async Task CleanupFailureCannotBeRetriedOrReactivatedAndShutdownPreservesItsProvider()
    {
        var row = Row("async.consumer");
        var parent = Row("async.parent");
        var calls = 0;
        var survivor = false;
        Actions[row.Id] = async (context, _) =>
        {
            context.Effect(() => survivor = true);
            await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new Resource(() =>
            {
                Interlocked.Increment(ref calls); throw new InvalidOperationException("cleanup failure");
            })));
        };
        var host = Host(parent, row);
        try
        {
            await host.StartAsync(default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.UnloadAsync(row.Id, default).AsTask());
            Assert.Equal(PluginState.CleanupFailed, State(host, row));
            Assert.True(survivor);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReloadAsync(row.Id, default).AsTask());
            await Assert.ThrowsAsync<AggregateException>(() => host.ShutdownAsync(default).AsTask().WaitAsync(Limit));
            Assert.Equal(1, calls);
            Assert.Equal(PluginState.Active, State(host, parent));
            Assert.Equal(PluginState.CleanupFailed, State(host, row));
        }
        finally { Actions.TryRemove(row.Id, out _); }
    }

    [Fact]
    public async Task FailedActivationAwaitsRollbackBeforeReleasingPreviouslyStartedProvider()
    {
        var row = Row("async.consumer", 5000);
        var parent = Row("async.parent");
        var entered = Signal();
        var finish = Signal();
        IPluginContext? saved = null;
        Actions[row.Id] = async (context, _) =>
        {
            saved = context;
            await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new Resource(async () =>
            {
                entered.SetResult(); await finish.Task;
                Assert.NotNull(context.Services.Get<IParent>());
            })));
            throw new InvalidOperationException("activation failure");
        };
        var host = Host(parent, row);
        var startup = host.StartAsync(default).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            Assert.False(startup.IsCompleted);
            Assert.Equal(PluginState.Active, State(host, parent));
            Assert.Equal(PluginState.Unloading, State(host, row));
        }
        finally { finish.TrySetResult(); }
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => startup.WaitAsync(Limit));
            Assert.Equal(PluginState.Failed, State(host, row));
            Assert.Equal(PluginState.Unloaded, State(host, parent));
            Assert.Throws<ObjectDisposedException>(() => saved!.Services.TryGet<IParent>(out _));
            await host.ShutdownAsync(default);
        }
        finally { Actions.TryRemove(row.Id, out _); }
    }

    [Fact]
    public async Task CallerCancellationStopsObservationButDoesNotAbandonTeardown()
    {
        var row = Row(timeout: 5000);
        var entered = Signal();
        var finish = Signal();
        Actions[row.Id] = async (context, _) =>
        {
            await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new Resource(async () =>
            {
                entered.SetResult(); await finish.Task;
            })));
        };
        var host = Host(row);
        using var cancel = new CancellationTokenSource();
        try
        {
            await host.StartAsync(default);
            var unload = host.UnloadAsync(row.Id, cancel.Token).AsTask();
            await entered.Task.WaitAsync(Limit);
            cancel.Cancel();
            await Assert.ThrowsAsync<InvalidOperationException>(() => unload.WaitAsync(Limit));
            Assert.Equal(PluginState.CleanupIncomplete, State(host, row));
        }
        finally { finish.TrySetResult(); }
        try
        {
            await host.UnloadAsync(row.Id, default).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginState.Unloaded, State(host, row));
        }
        finally { Actions.TryRemove(row.Id, out _); }
    }

    [Fact]
    public async Task FailedActivationWithIncompleteRollbackCannotReleaseItsProvider()
    {
        var row = Row("async.consumer", 100);
        var parent = Row("async.parent");
        var finish = Signal();
        var released = Signal();
        Actions[row.Id] = async (context, _) =>
        {
            await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new Resource(async () =>
            {
                await finish.Task;
                Assert.NotNull(context.Services.Get<IParent>());
                released.SetResult();
            })));
            throw new InvalidOperationException("activation failure");
        };
        var host = Host(parent, row);
        try
        {
            var error = await Assert.ThrowsAsync<AggregateException>(() => host.StartAsync(default).AsTask().WaitAsync(Limit));
            Assert.Contains(error.InnerExceptions, failure => failure.Message.Contains("rollback", StringComparison.Ordinal));
            Assert.Equal(PluginState.CleanupIncomplete, State(host, row));
            Assert.Equal(PluginState.Active, State(host, parent));
        }
        finally { finish.TrySetResult(); }
        try
        {
            await released.Task.WaitAsync(Limit);
            await host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
            Assert.All(host.Instances, info => Assert.Equal(PluginState.Unloaded, info.State));
        }
        finally { Actions.TryRemove(row.Id, out _); }
    }

    [Fact]
    public async Task EarlyCleanupCannotUnloadItsOwnerAndDoesNotCorruptHostState()
    {
        var row = Row();
        IAsyncDisposable? handle = null;
        Actions[row.Id] = async (context, _) =>
        {
            var host = context.Services.Get<IPluginHost>();
            handle = await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new Resource(async () =>
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.UnloadAsync(row.Id, default).AsTask());
            })));
        };
        var host = Host(row);
        try
        {
            await host.StartAsync(default);
            await handle!.DisposeAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(PluginState.Active, State(host, row));
            await host.UnloadAsync(row.Id, default);
            Assert.Equal(PluginState.Unloaded, State(host, row));
        }
        finally { Actions.TryRemove(row.Id, out _); }
    }

    [Fact]
    public async Task ActivationAndCleanupReentryFailPromptlyWithoutHoldingALifecycleLock()
    {
        var row = Row();
        var rejected = 0;
        Actions[row.Id] = async (context, _) =>
        {
            var host = context.Services.Get<IPluginHost>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.UnloadAsync(row.Id, default).AsTask());
            Interlocked.Increment(ref rejected);
            await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new Resource(async () =>
            {
                await Task.Run(async () =>
                    await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync(row.Id, default).AsTask())).WaitAsync(Limit);
                Interlocked.Increment(ref rejected);
            })));
        };
        var host = Host(row);
        try
        {
            await host.StartAsync(default).AsTask().WaitAsync(Limit);
            await host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
            Assert.Equal(2, rejected);
        }
        finally { Actions.TryRemove(row.Id, out _); }
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 0)]
    [InlineData(100001, 100)]
    public void InvalidLifecycleLimitsFailBeforeActivation(int capacity, int timeout)
    {
        var row = Row() with { Lifecycle = new() { EffectCapacity = capacity, TeardownTimeoutMs = timeout } };
        Assert.Throws<ConfigException>(() => Host(row));
    }
}
