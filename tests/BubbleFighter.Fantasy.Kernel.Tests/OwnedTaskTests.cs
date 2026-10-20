using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class OwnedTaskTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static PluginContext Context(int capacity = 4) => new(
        new PluginRow { Id = Guid.NewGuid().ToString("N"), Plugin = "task.test", Source = "test",
            Lifecycle = new PluginLifecycleOptions { EffectCapacity = capacity } },
        new ServiceRegistry(), new EventBus(NullLogger.Instance), NullLogger.Instance);

    public interface IValue { int Number { get; } }
    private sealed record Value(int Number = 7) : IValue;
    private static readonly ConcurrentDictionary<string, Func<IPluginContext, ValueTask>> Plans = new();
    [Plugin("task.provider", Provides = [typeof(IValue)])]
    public sealed class Provider : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken ct)
        { context.Services.Register<IValue>(new Value()); return ValueTask.CompletedTask; }
    }
    [Plugin("task.consumer", Requires = [typeof(IValue)])]
    public sealed class Consumer : IPlugin
    { public ValueTask ActivateAsync(IPluginContext context, CancellationToken ct) => Plans[context.Row.Id](context); }

    private static (PluginHost Host, string Parent, string Child) Host(Func<IPluginContext, ValueTask> activate, int timeout = 5000)
    {
        var parent = Guid.NewGuid().ToString("N");
        var child = Guid.NewGuid().ToString("N");
        Plans[child] = activate;
        return (new PluginHost([
            new PluginRow { Id = parent, Plugin = "task.provider", Source = "test" },
            new PluginRow { Id = child, Plugin = "task.consumer", Source = "test",
                Lifecycle = new PluginLifecycleOptions { TeardownTimeoutMs = timeout } }],
            PluginCatalog.ScanAssemblies([typeof(OwnedTaskTests).Assembly]), NullLoggerFactory.Instance), parent, child);
    }

    [Fact]
    public async Task WorkFailureIsVisibleAndShutdownReportsItAfterReleasingDependencies()
    {
        var fail = Signal();
        IOwnedTask? task = null;
        var h = Host(async context => task = await context.RunTaskAsync("later-failure", async _ =>
        { await fail.Task; throw new ArgumentException("later work failure"); }));
        try
        {
            await h.Host.StartAsync(default);
            fail.TrySetResult();
            await Assert.ThrowsAsync<ArgumentException>(() => task!.Completion.WaitAsync(Limit));
            var failed = h.Host.Instances.Single(row => row.RowId == h.Child);
            Assert.Equal(PluginState.WorkFailed, failed.State);
            Assert.Contains("later-failure", failed.Error);
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Host.StartAsync(default).AsTask());
            await Assert.ThrowsAsync<AggregateException>(() => h.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit));
            Assert.Equal(PluginState.Failed, h.Host.Instances.Single(row => row.RowId == h.Child).State);
            Assert.Equal(PluginState.Unloaded, h.Host.Instances.Single(row => row.RowId == h.Parent).State);
            Plans[h.Child] = _ => ValueTask.CompletedTask;
            await h.Host.StartAsync(default);
            Assert.All(h.Host.Instances, row => Assert.Equal(PluginState.Active, row.State));
            await h.Host.ShutdownAsync(default);
        }
        finally { fail.TrySetResult(); Plans.TryRemove(h.Child, out _); }
    }

    [Fact]
    public async Task IncompleteTaskTerminationRetainsTheProviderUntilTheSameDrainCompletes()
    {
        var entered = Signal();
        var finish = Signal();
        IOwnedTask? task = null;
        var h = Host(async context => task = await context.RunTaskAsync("uncooperative", async _ =>
        { entered.TrySetResult(); await finish.Task; Assert.Equal(7, context.Services.Get<IValue>().Number); }), 50);
        try
        {
            await h.Host.StartAsync(default);
            await entered.Task.WaitAsync(Limit);
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Host.UnloadAsync(h.Child, default).AsTask().WaitAsync(Limit));
            Assert.Equal(PluginState.CleanupIncomplete, h.Host.Instances.Single(row => row.RowId == h.Child).State);
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

    [Fact]
    public async Task CaughtInitialWorkFailureCannotBeCommittedAsAnActivePlugin()
    {
        var h = Host(async context =>
        {
            var task = await context.RunTaskAsync("initial-failure", _ => throw new ArgumentException("initial"));
            await Assert.ThrowsAsync<ArgumentException>(() => task.Completion.WaitAsync(Limit));
        });
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Host.StartAsync(default).AsTask().WaitAsync(Limit));
            Assert.Contains("rollback completed", failure.Message);
            Assert.Equal(PluginState.Failed, h.Host.Instances.Single(row => row.RowId == h.Child).State);
            Assert.Equal(PluginState.Unloaded, h.Host.Instances.Single(row => row.RowId == h.Parent).State);
            await h.Host.ShutdownAsync(default);
        }
        finally { Plans.TryRemove(h.Child, out _); }
    }

    [Fact]
    public async Task CancellationCleanupFailureRetainsTheHostDependencyGraph()
    {
        var entered = Signal();
        var calls = 0;
        IOwnedTask? task = null;
        var h = Host(async context => task = await context.RunTaskAsync("bad-cancellation", async token =>
        {
            using var registration = token.Register(() => { Interlocked.Increment(ref calls); throw new InvalidOperationException("stop failure"); });
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }));
        try
        {
            await h.Host.StartAsync(default);
            await entered.Task.WaitAsync(Limit);
            await Assert.ThrowsAsync<AggregateException>(() => task!.DisposeAsync().AsTask().WaitAsync(Limit));
            await Assert.ThrowsAsync<AggregateException>(() => task!.Completion.WaitAsync(Limit));
            await Assert.ThrowsAsync<AggregateException>(() => h.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit));
            Assert.Equal(PluginState.CleanupFailed, h.Host.Instances.Single(row => row.RowId == h.Child).State);
            Assert.Equal(PluginState.Active, h.Host.Instances.Single(row => row.RowId == h.Parent).State);
            Assert.Equal(1, calls);
        }
        finally { Plans.TryRemove(h.Child, out _); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyCleanupAggregatesCannotMasqueradeAsDrainedWork(bool includeWorkFailure)
    {
        var fail = Signal();
        IOwnedTask? task = null;
        var h = Host(async context =>
        {
            context.Effect(() => throw new AggregateException("empty cleanup failure"));
            if (includeWorkFailure) task = await context.RunTaskAsync("also-failed", async _ =>
            { await fail.Task; throw new ArgumentException("work failure"); });
        });
        try
        {
            await h.Host.StartAsync(default);
            fail.TrySetResult();
            if (task is not null) await Assert.ThrowsAsync<ArgumentException>(() => task.Completion.WaitAsync(Limit));
            await Assert.ThrowsAsync<AggregateException>(() => h.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit));
            Assert.Equal(PluginState.CleanupFailed, h.Host.Instances.Single(row => row.RowId == h.Child).State);
            Assert.Equal(PluginState.Active, h.Host.Instances.Single(row => row.RowId == h.Parent).State);
        }
        finally { fail.TrySetResult(); Plans.TryRemove(h.Child, out _); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PropagatedTaskErrorsDoNotProveUnrelatedCleanupHasCompleted(bool cancellationCallback)
    {
        var foreign = Context();
        var failedTask = await foreign.RunTaskAsync("foreign", _ => throw new ArgumentException("foreign work"));
        await Assert.ThrowsAsync<ArgumentException>(() => failedTask.Completion.WaitAsync(Limit));
        var finishedCleanup = false;
        var h = Host(context =>
        {
            void Cleanup()
            {
                failedTask.DisposeAsync().AsTask().GetAwaiter().GetResult();
                finishedCleanup = true;
            }
            if (cancellationCallback) context.Lifetime.Register(Cleanup);
            else context.Effect(Cleanup);
            return ValueTask.CompletedTask;
        });
        try
        {
            await h.Host.StartAsync(default);
            await Assert.ThrowsAsync<AggregateException>(() => h.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit));
            Assert.False(finishedCleanup);
            Assert.Equal(PluginState.CleanupFailed, h.Host.Instances.Single(row => row.RowId == h.Child).State);
            Assert.Equal(PluginState.Active, h.Host.Instances.Single(row => row.RowId == h.Parent).State);
        }
        finally
        {
            Plans.TryRemove(h.Child, out _);
            await Assert.ThrowsAsync<AggregateException>(() => foreign.UnwindAsync().AsTask());
        }
    }

    [Fact]
    public async Task WorkStartsOnlyAfterAdmissionAndSuccessReturnsItsSlot()
    {
        var context = Context(1);
        var task = await context.RunTaskAsync("owned", _ =>
        { Assert.Equal(1, context.RetainedEffectCount); return ValueTask.CompletedTask; });
        Assert.Equal("owned", task.Name);
        await task.Completion.WaitAsync(Limit);
        Assert.Equal(0, context.RetainedEffectCount);
        await task.DisposeAsync();
        await task.DisposeAsync();
        await context.UnwindAsync();
    }

    /// <summary>Scope closure observes task completion even while the supervisor is paused after its real ownership release.</summary>
    [Fact]
    public async Task ScopeClosureDoesNotOvertakeCompletionWhenSupervisorContinuationIsDelayed()
    {
        var scope = new EffectScope(1);
        var reports = new ConcurrentQueue<Exception>();
        OwnedTask? task = null;
        var ownership = await scope.AcquireAsync(_ =>
        {
            task = new OwnedTask("delayed-supervisor", scope, default, _ => ValueTask.CompletedTask,
                (_, failure) => reports.Enqueue(failure));
            return ValueTask.FromResult(task.Resource);
        });
        var delayed = new DelayedOwnership(ownership);
        task!.Attach(delayed);
        try
        {
            await delayed.Released.Task.WaitAsync(Limit);
            Assert.False(delayed.AllowReturn.Task.IsCompleted);
            Assert.Equal(0, scope.TotalRetainedCount);
            await scope.DisposeAsync().AsTask().WaitAsync(Limit);
            Assert.True(task.Completion.IsCompletedSuccessfully);
            Assert.Empty(reports);
        }
        finally
        {
            delayed.AllowReturn.TrySetResult();
            await delayed.Returned.Task.WaitAsync(Limit);
            await task.Completion.WaitAsync(Limit);
            await scope.DisposeAsync().AsTask().WaitAsync(Limit);
        }
    }

    /// <summary>Cleanup failure is reported once and published before explicit stop returns, while its failed slot remains owned.</summary>
    [Fact]
    public async Task ExplicitStopPublishesCleanupFailureBeforeItsSupervisorContinues()
    {
        var scope = new EffectScope(1);
        var reports = new ConcurrentQueue<Exception>();
        var entered = Signal();
        var callbackRan = Signal();
        OwnedTask? task = null;
        var ownership = await scope.AcquireAsync(_ =>
        {
            task = new OwnedTask("failed-stop", scope, default, async token =>
            {
                using var registration = token.Register(() =>
                {
                    callbackRan.TrySetResult();
                    throw new InvalidOperationException("stop callback");
                });
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { await callbackRan.Task; }
            }, (_, failure) => reports.Enqueue(failure));
            return ValueTask.FromResult(task.Resource);
        });
        task!.Attach(ownership);
        await entered.Task.WaitAsync(Limit);
        await Assert.ThrowsAsync<AggregateException>(() => task.DisposeAsync().AsTask().WaitAsync(Limit));
        Assert.True(task.Completion.IsFaulted);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => task.Completion.WaitAsync(Limit));
        Assert.Same(failure, Assert.Single(reports));
        Assert.Contains(failure.Flatten().InnerExceptions, error => error.Message == "stop callback");
        Assert.Equal(1, scope.TotalRetainedCount);
        await Assert.ThrowsAsync<AggregateException>(() => scope.DisposeAsync().AsTask().WaitAsync(Limit));
        Assert.Equal(0, scope.TotalRetainedCount);
        Assert.Single(reports);
    }

    /// <summary>Cancellation during failure reporting cannot reclassify an already faulted work body as cooperative cancellation.</summary>
    [Fact]
    public async Task CancellationDuringReportingDoesNotEraseAnUnrequestedCancellationFault()
    {
        var scope = new EffectScope(1);
        using var cancellation = new CancellationTokenSource();
        var reports = new ConcurrentQueue<Exception>();
        OwnedTask? task = null;
        var ownership = await scope.AcquireAsync(_ =>
        {
            task = new OwnedTask("unrequested-cancel", scope, cancellation.Token,
                token => throw new OperationCanceledException(token), (_, failure) =>
                {
                    reports.Enqueue(failure);
                    cancellation.Cancel();
                });
            return ValueTask.FromResult(task.Resource);
        });
        task!.Attach(ownership);
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.Completion.WaitAsync(Limit));
        Assert.True(task.Completion.IsFaulted);
        Assert.Same(failure, Assert.Single(reports));
        Assert.Equal(1, scope.TotalRetainedCount);
        var cleanup = await Assert.ThrowsAsync<AggregateException>(() => scope.DisposeAsync().AsTask().WaitAsync(Limit));
        Assert.All(cleanup.Flatten().InnerExceptions, error => Assert.IsType<OwnedWorkException>(error));
        Assert.Equal(0, scope.TotalRetainedCount);
    }

    private sealed class DelayedOwnership(IAsyncDisposable ownership) : IAsyncDisposable
    {
        internal TaskCompletionSource Released { get; } = Signal();
        internal TaskCompletionSource AllowReturn { get; } = Signal();
        internal TaskCompletionSource Returned { get; } = Signal();

        /// <summary>Releases the real effect, then holds the task supervisor at a separately controlled continuation gate.</summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await ownership.DisposeAsync().ConfigureAwait(false);
                Released.TrySetResult();
                await AllowReturn.Task.ConfigureAwait(false);
            }
            finally { Returned.TrySetResult(); }
        }
    }

    [Fact]
    public async Task ExhaustedCapacityAndPrecancellationNeverInvokeWork()
    {
        var context = Context(1);
        context.Effect(() => { });
        var called = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.RunTaskAsync("rejected", _ =>
        { called = true; return ValueTask.CompletedTask; }).AsTask());
        Assert.False(called);
        await context.UnwindAsync();
        var canceled = Context();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.RunTaskAsync("canceled", _ =>
        { called = true; return ValueTask.CompletedTask; }, new CancellationToken(true)).AsTask());
        Assert.Equal(0, canceled.RetainedEffectCount);
        Assert.False(called);
        await canceled.UnwindAsync();
    }

    [Fact]
    public async Task CompletedWorkDoesNotAccumulateHistory()
    {
        var context = Context(1);
        for (var i = 0; i < 1000; i++)
        {
            var task = await context.RunTaskAsync("iteration", _ => ValueTask.CompletedTask);
            await task.Completion.WaitAsync(Limit);
            Assert.Equal(0, context.RetainedEffectCount);
        }
        await context.UnwindAsync();
    }

    [Fact]
    public async Task SynchronouslyBlockedWorkCannotBlockStartingOrObservingClosure()
    {
        var context = Context();
        var entered = Signal();
        using var finish = new ManualResetEventSlim();
        var task = await context.RunTaskAsync("blocked", _ =>
        { entered.TrySetResult(); Assert.True(finish.Wait(Limit)); return ValueTask.CompletedTask; });
        await entered.Task.WaitAsync(Limit);
        var close = context.UnwindAsync().AsTask();
        try
        {
            Assert.False(close.IsCompleted);
            Assert.Equal(1, context.RetainedEffectCount);
        }
        finally { finish.Set(); }
        await Task.WhenAll(task.Completion, close).WaitAsync(Limit);
        Assert.Equal(0, context.RetainedEffectCount);
    }

    [Fact]
    public async Task WorkCannotAwaitItsOwnStopOrOwnerTeardown()
    {
        var context = Context();
        var go = Signal();
        IOwnedTask? task = null;
        task = await context.RunTaskAsync("self-wait", async _ =>
        {
            await go.Task;
            await Assert.ThrowsAsync<InvalidOperationException>(() => task!.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.UnwindAsync().AsTask());
        });
        go.TrySetResult();
        await task.Completion.WaitAsync(Limit);
        await context.UnwindAsync();
    }

    [Fact]
    public async Task CancellationCallbackFailureIsNotReportedAsSuccessfulTermination()
    {
        var context = Context();
        var entered = Signal();
        var calls = 0;
        var task = await context.RunTaskAsync("cancel-failure", async token =>
        {
            using var registration = token.Register(() => { Interlocked.Increment(ref calls); throw new InvalidOperationException("cancel callback"); });
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        await entered.Task.WaitAsync(Limit);
        await Assert.ThrowsAsync<AggregateException>(() => task.DisposeAsync().AsTask().WaitAsync(Limit));
        await Assert.ThrowsAsync<AggregateException>(() => task.Completion.WaitAsync(Limit));
        Assert.NotNull(context.WorkFailure);
        var cleanup = await Assert.ThrowsAsync<AggregateException>(() => context.UnwindAsync().AsTask());
        Assert.Contains(cleanup.Flatten().InnerExceptions, error => error.Message == "cancel callback");
        await Assert.ThrowsAsync<AggregateException>(() => task.DisposeAsync().AsTask());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ClosingRacesWithStartingWithoutLosingWorkOwnership()
    {
        for (var i = 0; i < 100; i++)
        {
            var context = Context();
            var start = Task.Run(async () => await context.RunTaskAsync("race", async token =>
                await Task.Delay(1, token)));
            var close = context.UnwindAsync().AsTask();
            IOwnedTask? task = null;
            try { task = await start.WaitAsync(Limit); }
            catch (ObjectDisposedException) { }
            if (task is not null)
            {
                try { await task.Completion.WaitAsync(Limit); }
                catch (OperationCanceledException) { Assert.True(task.Completion.IsCanceled); }
            }
            await close.WaitAsync(Limit);
            Assert.Equal(0, context.RetainedEffectCount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyStopAndExternalCancellationJoinCooperativeWork(bool external)
    {
        var context = Context();
        var entered = Signal();
        using var cancel = new CancellationTokenSource();
        var task = await context.RunTaskAsync("waiting", async token =>
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }, cancel.Token);
        await entered.Task.WaitAsync(Limit);
        if (external) cancel.Cancel();
        else await task.DisposeAsync().AsTask().WaitAsync(Limit);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.Completion.WaitAsync(Limit));
        Assert.True(task.Completion.IsCanceled);
        Assert.Null(context.WorkFailure);
        Assert.Equal(0, context.RetainedEffectCount);
        await task.DisposeAsync();
        await context.UnwindAsync();
    }

    [Fact]
    public async Task WorkFaultIsObservableAndRetainedUntilTeardownReportsIt()
    {
        var context = Context();
        var calls = 0;
        var task = await context.RunTaskAsync("broken", _ =>
        { calls++; throw new ArgumentException("work failure"); });
        var error = await Assert.ThrowsAsync<ArgumentException>(() => task.Completion.WaitAsync(Limit));
        Assert.Equal("work failure", error.Message);
        Assert.Equal(1, context.RetainedEffectCount);
        Assert.NotNull(context.WorkFailure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.RunTaskAsync("refused", _ => ValueTask.CompletedTask).AsTask());
        await Assert.ThrowsAsync<OwnedWorkException>(() => task.DisposeAsync().AsTask());
        var cleanup = await Assert.ThrowsAsync<AggregateException>(() => context.UnwindAsync().AsTask());
        Assert.All(cleanup.Flatten().InnerExceptions, failure => Assert.IsType<OwnedWorkException>(failure));
        Assert.Equal(0, context.RetainedEffectCount);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrequestedCancellationIsAWorkFault(bool useOwnedToken)
    {
        var context = Context();
        var task = await context.RunTaskAsync("unexpected-cancel", token =>
            throw new OperationCanceledException(useOwnedToken ? token : new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.Completion.WaitAsync(Limit));
        Assert.True(task.Completion.IsFaulted);
        Assert.NotNull(context.WorkFailure);
        await Assert.ThrowsAsync<AggregateException>(() => context.UnwindAsync().AsTask());
    }
}
