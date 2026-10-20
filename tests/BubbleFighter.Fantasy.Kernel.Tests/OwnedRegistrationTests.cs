using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class OwnedRegistrationTests
{
    public interface IValue { int Read(); }
    private sealed record Value(int Number = 1) : IValue { public int Read() => Number; }
    private sealed record Wrapper(IValue Inner) : IValue { public int Read() => Inner.Read() + 1; }
    private sealed record Ping : IEvent;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Harness
    {
        public ServiceRegistry Registry { get; } = new();
        public EventBus Bus { get; }
        public PluginContext Context { get; }
        public Harness(int capacity = 32, ILogger? logger = null)
        {
            Bus = new EventBus(logger ?? NullLogger.Instance, new EventBusOptions { DrainTimeoutMs = 20 });
            Registry.Register<IServiceRegistry>(Registry);
            Registry.Register<IEventBus>(Bus);
            Context = new PluginContext(new PluginRow
            {
                Id = Guid.NewGuid().ToString("N"), Plugin = "owned.test", Source = "test",
                Lifecycle = new PluginLifecycleOptions { EffectCapacity = capacity },
            }, Registry, Bus, NullLogger.Instance);
        }
    }

    private sealed class DrainLog : ILogger
    {
        public TaskCompletionSource Reported { get; } = Signal();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (level == LogLevel.Error) Reported.TrySetResult(); }
    }

    private sealed class AsyncResource(Func<ValueTask> close) : IAsyncDisposable
    { public ValueTask DisposeAsync() => close(); }

    [Fact]
    public async Task ClosingAdmissionSkipsCallbacksBeforeTheirRegistrationIsReached()
    {
        var h = new Harness();
        var finish = Signal();
        var entered = Signal();
        var count = 0;
        h.Context.Events.On<Ping>(_ => count++);
        await h.Context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new AsyncResource(async () =>
        { entered.TrySetResult(); await finish.Task; })));
        var close = h.Context.UnwindAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            h.Bus.Emit(new Ping());
            Assert.Equal(0, count);
        }
        finally { finish.TrySetResult(); }
        await close.WaitAsync(Limit);
    }

    [Fact]
    public async Task TaskOutlivingItsCallbackCanCloseTheContext()
    {
        var h = new Harness();
        var finish = Signal();
        Task? later = null;
        h.Context.Events.On<Ping>(_ => later = Task.Run(async () =>
        {
            await finish.Task;
            await h.Context.UnwindAsync();
        }));
        h.Bus.Emit(new Ping());
        finish.TrySetResult();
        await later!.WaitAsync(Limit);
        Assert.Equal(0, h.Context.RetainedEffectCount);
    }

    [Fact]
    public async Task InfrastructureLookupsKeepTheCallingOwnerAndForeignEffectsAreRejected()
    {
        var h = new Harness();
        Assert.Same(h.Context.Services, h.Context.Services.Get<IServiceRegistry>());
        Assert.True(h.Context.Services.TryGet<IEventBus>(out var bus));
        Assert.Same(h.Context.Events, bus);
        h.Context.Services.WhenAvailable<IServiceRegistry>(registry => Assert.Same(h.Context.Services, registry));
        h.Context.Services.WhenAvailable<IEventBus>(events => Assert.Same(h.Context.Events, events));
        var handle = h.Context.Services.Register<IValue>(new Value());
        var other = new Harness();
        Assert.Throws<InvalidOperationException>(() => other.Context.Effect(handle));
        Assert.Equal(0, other.Context.RetainedEffectCount);
        Assert.Equal(1, h.Registry.Get<IValue>().Read());
        await h.Context.UnwindAsync();
        await other.Context.UnwindAsync();
    }

    [Fact]
    public async Task AllEventModesPreservePriorityResultsAndAutomaticRemoval()
    {
        var h = new Harness();
        var calls = new List<int>();
        h.Context.Events.On<Ping>(_ => calls.Add(1), 10);
        h.Context.Events.OnAsync<Ping>((_, _) => { calls.Add(2); return ValueTask.CompletedTask; });
        h.Context.Events.OnBail<Ping, string>(_ => "answer");
        h.Context.Events.Intercept<Ping, int>(async (_, next, ct) => await next(ct) + 1);
        await h.Context.Events.SerialAsync(new Ping(), default);
        Assert.Equal(new[] { 1, 2 }, calls);
        Assert.Equal("answer", h.Context.Events.Bail<Ping, string>(new Ping()));
        Assert.Equal(8, await h.Context.Events.WaterfallAsync(new Ping(), _ => ValueTask.FromResult(7), default));
        await h.Context.UnwindAsync();
        h.Bus.Emit(new Ping());
        await h.Bus.ParallelAsync(new Ping(), default);
        Assert.Equal(2, calls.Count);
        Assert.Null(h.Bus.Bail<Ping, string>(new Ping()));
        Assert.Equal(7, await h.Bus.WaterfallAsync(new Ping(), _ => ValueTask.FromResult(7), default));
        Assert.Throws<ObjectDisposedException>(() => h.Context.Events.On<Ping>(_ => { }));
        Assert.Throws<ObjectDisposedException>(() => h.Context.Services.Register<IValue>(new Value()));
    }

    [Fact]
    public async Task DecoratorFactoryDoesNotHoldRegistryOrChainLocks()
    {
        var h = new Harness();
        h.Context.Services.RegisterDecoratable<IValue>(new Value());
        h.Context.Services.Decorate<IValue>(inner =>
        {
            var read = Task.Run(() =>
            {
                Assert.Equal(1, h.Context.Services.Get<IValue>().Read());
                Assert.Empty(h.Registry.Decorations);
            });
            Assert.True(read.Wait(Limit));
            return new Wrapper(inner);
        });
        Assert.Equal(2, h.Context.Services.Get<IValue>().Read());
        await h.Context.UnwindAsync();
    }

    private static readonly ConcurrentDictionary<string, Func<IPluginContext, ValueTask>> Plans = new();
    [Plugin("owned.failure", Provides = [typeof(IValue)])]
    public sealed class FailurePlugin : IPlugin
    { public ValueTask ActivateAsync(IPluginContext context, CancellationToken ct) => Plans[context.Row.Id](context); }

    [Plugin("owned.consumer", Requires = [typeof(IValue)])]
    public sealed class ConsumerPlugin : IPlugin
    { public ValueTask ActivateAsync(IPluginContext context, CancellationToken ct) => Plans[context.Row.Id](context); }

    [Fact]
    public async Task HostRetainsProviderUntilOwnedEventCallbackHasDrained()
    {
        var provider = Guid.NewGuid().ToString("N");
        var consumer = Guid.NewGuid().ToString("N");
        var entered = Signal();
        var finish = Signal();
        IPluginContext? saved = null;
        Plans[provider] = context => { context.Services.Register<IValue>(new Value()); return ValueTask.CompletedTask; };
        Plans[consumer] = context =>
        {
            saved = context;
            context.Events.OnAsync<Ping>(async (_, _) =>
            {
                entered.TrySetResult();
                await finish.Task;
                Assert.Equal(1, context.Services.Get<IValue>().Read());
            });
            return ValueTask.CompletedTask;
        };
        var host = new PluginHost([
            new PluginRow { Id = provider, Plugin = "owned.failure", Source = "test" },
            new PluginRow { Id = consumer, Plugin = "owned.consumer", Source = "test",
                Lifecycle = new PluginLifecycleOptions { TeardownTimeoutMs = 50 } }],
            PluginCatalog.ScanAssemblies([typeof(OwnedRegistrationTests).Assembly]), NullLoggerFactory.Instance);
        Task? dispatch = null;
        try
        {
            await host.StartAsync(default);
            dispatch = saved!.Events.SerialAsync(new Ping(), default).AsTask();
            await entered.Task.WaitAsync(Limit);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.UnloadAsync(consumer, default).AsTask().WaitAsync(Limit));
            Assert.Equal(PluginState.CleanupIncomplete, host.Instances.Single(row => row.RowId == consumer).State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.UnloadAsync(provider, default).AsTask());
        }
        finally
        {
            finish.TrySetResult();
            if (dispatch is not null) await dispatch.WaitAsync(Limit);
            await host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
            Plans.TryRemove(provider, out _);
            Plans.TryRemove(consumer, out _);
        }
        Assert.All(host.Instances, row => Assert.Equal(PluginState.Unloaded, row.State));
    }

    [Fact]
    public async Task EventHandlerCannotUnloadItsOwnActivation()
    {
        var id = Guid.NewGuid().ToString("N");
        IPluginContext? saved = null;
        Plans[id] = context =>
        {
            saved = context;
            context.Services.Register<IValue>(new Value());
            context.Events.OnAsync<Ping>(async (_, _) =>
                await Assert.ThrowsAsync<InvalidOperationException>(() => context.Services.Get<IPluginHost>().UnloadAsync(id, default).AsTask()));
            return ValueTask.CompletedTask;
        };
        var host = new PluginHost([new PluginRow { Id = id, Plugin = "owned.failure", Source = "test" }],
            PluginCatalog.ScanAssemblies([typeof(OwnedRegistrationTests).Assembly]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(default);
            await saved!.Events.SerialAsync(new Ping(), default).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginState.Active, Assert.Single(host.Instances).State);
            await host.ShutdownAsync(default);
        }
        finally { Plans.TryRemove(id, out _); }
    }

    [Fact]
    public async Task HostRollsBackForgottenRegistrationsAfterPartialActivationFailure()
    {
        var id = Guid.NewGuid().ToString("N");
        IPluginContext? saved = null;
        var called = false;
        Plans[id] = context =>
        {
            saved = context;
            context.Services.Register<IValue>(new Value());
            context.Events.On<Ping>(_ => called = true);
            throw new InvalidOperationException("activation failure");
        };
        var host = new PluginHost([new PluginRow { Id = id, Plugin = "owned.failure", Source = "test" }],
            PluginCatalog.ScanAssemblies([typeof(OwnedRegistrationTests).Assembly]), NullLoggerFactory.Instance);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(default).AsTask());
            Assert.Equal(PluginState.Failed, Assert.Single(host.Instances).State);
            Assert.NotNull(saved);
            Assert.Throws<ObjectDisposedException>(() => saved.Services.TryGet<IValue>(out _));
            saved.Events.Emit(new Ping());
            Assert.False(called);
            await host.ShutdownAsync(default);
        }
        finally { Plans.TryRemove(id, out _); }
    }

    [Fact]
    public async Task UnderlyingDrainTimeoutCannotReleaseOwnershipWhileHandlerStillRuns()
    {
        var log = new DrainLog();
        var h = new Harness(logger: log);
        var entered = Signal();
        var finish = Signal();
        h.Context.Events.OnAsync<Ping>(async (_, _) => { entered.TrySetResult(); await finish.Task; });
        var dispatch = h.Bus.SerialAsync(new Ping(), default).AsTask();
        await entered.Task.WaitAsync(Limit);
        var close = h.Context.UnwindAsync().AsTask();
        try
        {
            await log.Reported.Task.WaitAsync(Limit);
            Assert.False(close.IsCompleted);
            Assert.Equal(1, h.Context.RetainedEffectCount);
        }
        finally { finish.TrySetResult(); }
        await Task.WhenAll(dispatch, close).WaitAsync(Limit);
    }

    [Fact]
    public async Task SelfUnsubscriptionRetainsOwnershipUntilTheHandlerReturns()
    {
        var h = new Harness();
        var removed = Signal();
        var finish = Signal();
        var count = 0;
        IDisposable? handle = null;
        handle = h.Context.Events.OnAsync<Ping>(async (_, _) =>
        {
            count++;
            Assert.Throws<InvalidOperationException>(h.Context.Unwind);
            handle!.Dispose();
            removed.TrySetResult();
            await finish.Task;
        });
        var dispatch = h.Bus.SerialAsync(new Ping(), default).AsTask();
        try
        {
            await removed.Task.WaitAsync(Limit);
            Assert.Equal(1, h.Context.RetainedEffectCount);
            await h.Bus.SerialAsync(new Ping(), default);
            Assert.Equal(1, count);
        }
        finally { finish.TrySetResult(); }
        await dispatch.WaitAsync(Limit);
        await h.Context.UnwindAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(0, h.Context.RetainedEffectCount);
    }

    [Fact]
    public async Task PendingAvailabilityIsRemovedAndRunningAvailabilityIsDrained()
    {
        var h = new Harness();
        var entered = Signal();
        using var finish = new ManualResetEventSlim();
        h.Context.Services.WhenAvailable<IValue>(_ =>
        {
            entered.TrySetResult();
            Assert.True(finish.Wait(Limit));
        });
        var publish = Task.Run(() => h.Registry.Register<IValue>(new Value()));
        await entered.Task.WaitAsync(Limit);
        var close = h.Context.UnwindAsync().AsTask();
        try { Assert.False(close.IsCompleted); }
        finally { finish.Set(); }
        using var registration = await publish.WaitAsync(Limit);
        await close.WaitAsync(Limit);
        var pending = new Harness();
        var called = false;
        pending.Context.Services.WhenAvailable<IValue>(_ => called = true);
        await pending.Context.UnwindAsync();
        using var value = pending.Registry.Register<IValue>(new Value());
        Assert.False(called);
    }

    [Fact]
    public async Task LateRegistrationIsRemovedBeforeTheRegisteringCallerIsRejected()
    {
        var h = new Harness();
        var entered = Signal();
        using var finish = new ManualResetEventSlim();
        using var waiting = h.Registry.WhenAvailable<IValue>(_ =>
        { entered.TrySetResult(); Assert.True(finish.Wait(Limit)); });
        var acquire = Task.Run(() => h.Context.Services.Register<IValue>(new Value()));
        await entered.Task.WaitAsync(Limit);
        var close = h.Context.UnwindAsync().AsTask();
        try { Assert.False(close.IsCompleted); }
        finally { finish.Set(); }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => acquire.WaitAsync(Limit));
        await close.WaitAsync(Limit);
        Assert.False(h.Registry.IsRegistered(typeof(IValue)));
    }

    [Theory]
    [InlineData("single")]
    [InlineData("many")]
    [InlineData("decoratable")]
    public async Task ServiceRegistrationsUnwindWithoutManualEffects(string mode)
    {
        var h = new Harness(1);
        var value = new Value();
        var handle = mode switch
        {
            "single" => h.Context.Services.Register<IValue>(value),
            "many" => h.Context.Services.RegisterMany<IValue>(value),
            _ => h.Context.Services.RegisterDecoratable<IValue>(value),
        };
        Assert.Equal(1, h.Context.RetainedEffectCount);
        Assert.Same(handle, h.Context.Effect(handle));
        Assert.Equal(1, h.Context.RetainedEffectCount);
        await h.Context.UnwindAsync();
        Assert.False(h.Registry.IsRegistered(typeof(IValue)));
        handle.Dispose();
        Assert.Equal(0, h.Context.RetainedEffectCount);
    }

    [Theory]
    [InlineData("single")]
    [InlineData("many")]
    [InlineData("decoratable")]
    [InlineData("decorate")]
    [InlineData("available")]
    [InlineData("sync")]
    [InlineData("async")]
    [InlineData("bail")]
    [InlineData("middleware")]
    public async Task ExhaustedCapacityRejectsBeforeAnyRegistrationSideEffect(string mode)
    {
        var h = new Harness(1);
        var called = 0;
        using var basis = mode == "decorate" ? h.Registry.RegisterDecoratable<IValue>(new Value()) : null;
        h.Context.Effect(() => { });
        Assert.Throws<InvalidOperationException>(() =>
        {
            IDisposable registration = mode switch
            {
                "single" => h.Context.Services.Register<IValue>(new Value()),
                "many" => h.Context.Services.RegisterMany<IValue>(new Value()),
                "decoratable" => h.Context.Services.RegisterDecoratable<IValue>(new Value()),
                "decorate" => h.Context.Services.Decorate<IValue>(inner => { called++; return new Wrapper(inner); }),
                "available" => h.Context.Services.WhenAvailable<IValue>(_ => called++),
                "sync" => h.Context.Events.On<Ping>(_ => called++),
                "async" => h.Context.Events.OnAsync<Ping>((_, _) => { called++; return ValueTask.CompletedTask; }),
                "bail" => h.Context.Events.OnBail<Ping, string>(_ => { called++; return "value"; }),
                _ => h.Context.Events.Intercept<Ping, int>((_, next, ct) => { called++; return next(ct); }),
            };
            registration.Dispose();
        });
        if (basis is null) h.Registry.Register<IValue>(new Value());
        h.Bus.Emit(new Ping());
        await h.Bus.SerialAsync(new Ping(), default);
        Assert.Null(h.Bus.Bail<Ping, string>(new Ping()));
        Assert.Equal(7, await h.Bus.WaterfallAsync(new Ping(), _ => ValueTask.FromResult(7), default));
        Assert.Equal(0, called);
        Assert.Equal(1, h.Context.RetainedEffectCount);
        await h.Context.UnwindAsync();
    }
}
