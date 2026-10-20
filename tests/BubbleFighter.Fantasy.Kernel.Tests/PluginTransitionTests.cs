using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class PluginTransitionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, Observation> States = new();
    public interface IA { bool Alive { get; } }
    public interface IB { }
    private sealed record Value(Observation State) : IA { public bool Alive => State.Alive; }
    private sealed record MiddleValue : IB;
    private sealed class Observation
    {
        internal int Starts;
        internal bool Alive;
        internal IPluginContext? Context;
        internal IDisposable? Registration;
        internal IA? Seen;
        internal IReadOnlyList<IA> Many = [];
        internal Func<IPluginContext, CancellationToken, ValueTask>? Start;
        internal Func<IPluginContext, CancellationToken, ValueTask>? AfterFirst;
        internal int Factories;
        internal int Observed;
        internal bool ObserverThrows;
        internal Func<IPluginContext, ValueTask>? Stop;
    }
    private sealed class Resource(Func<ValueTask> dispose) : IAsyncDisposable
    { public ValueTask DisposeAsync() => dispose(); }
    private static async ValueTask Begin(IPluginContext context, CancellationToken ct)
    {
        var state = States[context.Row.Id];
        Interlocked.Increment(ref state.Starts);
        state.Context = context;
        state.Alive = true;
        await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new Resource(async () =>
        {
            if (state.Stop is { } stop) await stop(context);
            state.Alive = false;
        })));
        if (state.Start is { } start) await start(context, ct);
    }
    [Plugin("transition.a", Requires = [typeof(IPluginHost)], Provides = [typeof(IA)])]
    public sealed class Provider : IPlugin
    {
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken ct)
        { await Begin(context, ct); States[context.Row.Id].Registration = context.Services.Register<IA>(new Value(States[context.Row.Id])); }
    }
    [Plugin("transition.b", Requires = [typeof(IA)], Provides = [typeof(IB)])]
    public sealed class Middle : IPlugin
    {
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken ct)
        { States[context.Row.Id].Seen = context.Services.Get<IA>(); await Begin(context, ct); context.Services.Register<IB>(new MiddleValue()); }
    }
    [Plugin("transition.c", Requires = [typeof(IB)])]
    public sealed class Leaf : IPlugin
    { public ValueTask ActivateAsync(IPluginContext context, CancellationToken ct) => Begin(context, ct); }
    [Plugin("transition.d", Requires = [typeof(IPluginHost)])]
    public sealed class Independent : IPlugin
    { public ValueTask ActivateAsync(IPluginContext context, CancellationToken ct) => Begin(context, ct); }
    [Plugin("transition.optional", Optional = [typeof(IA)])]
    public sealed class Optional : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken ct)
        { context.Services.TryGet<IA>(out States[context.Row.Id].Seen); return Begin(context, ct); }
    }
    [Plugin("transition.many", ProvidesMany = [typeof(IA)])]
    public sealed class Extension : IPlugin
    {
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken ct)
        { await Begin(context, ct); context.Services.RegisterMany<IA>(new Value(States[context.Row.Id])); }
    }
    [Plugin("transition.extensions", RequiresMany = [typeof(IA)])]
    public sealed class Extensions : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken ct)
        { States[context.Row.Id].Many = context.Services.GetAll<IA>(); return Begin(context, ct); }
    }
    [Plugin("transition.base", Provides = [typeof(IA)])]
    public sealed class Decoratable : IPlugin
    {
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken ct)
        { await Begin(context, ct); context.Services.RegisterDecoratable<IA>(new Value(States[context.Row.Id])); }
    }
    private sealed record Inverted(IA Inner) : IA { public bool Alive => !Inner.Alive; }
    [Plugin("transition.layer", Decorates = [typeof(IA)])]
    public sealed class Decorator : IPlugin
    {
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken ct)
        { await Begin(context, ct); context.Services.Decorate<IA>(inner => new Inverted(inner)); }
    }

    [Fact]
    public async Task ExtensionChangesRebindSnapshotsWithoutRestartingOtherProviders()
    {
        await using var h = new Harness(("transition.many", false), ("transition.many", true), ("transition.extensions", false));
        await h.Host.StartAsync(default);
        var old = h[2].Many;
        await h.Request(1, true).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Single(old); Assert.Equal(2, h[2].Many.Count);
        Assert.Equal(1, h[0].Starts);
        await h.Request(0, false).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Single(h[2].Many); Assert.Equal(1, h[1].Starts);
        Assert.Equal(3, h[2].Starts);
    }

    [Fact]
    public async Task LayerChangesRebindConsumersButDoNotMutateTheirPreviousProjection()
    {
        await using var h = new Harness(("transition.base", false), ("transition.layer", true), ("transition.optional", false));
        await h.Host.StartAsync(default);
        var old = h[2].Seen!;
        await h.Request(1, true).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.True(old.Alive); Assert.False(h[2].Seen!.Alive);
        await h.Request(1, false).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.True(h[2].Seen!.Alive);
        Assert.Equal(1, h[0].Starts); Assert.Equal(3, h[2].Starts);
    }

    [Fact]
    public async Task ProviderLossSuspendsOnlyItsDependentsAndRestoresThem()
    {
        await using var h = new Harness(("transition.c", false), ("transition.b", false), ("transition.a", false), ("transition.d", false));
        var stopped = new ConcurrentQueue<int>();
        h[0].Stop = _ => { stopped.Enqueue(0); return ValueTask.CompletedTask; };
        h[1].Stop = context => { Assert.True(context.Services.Get<IA>().Alive); stopped.Enqueue(1); return ValueTask.CompletedTask; };
        h[2].Stop = _ => { stopped.Enqueue(2); return ValueTask.CompletedTask; };
        await h.Host.StartAsync(default);
        var original = h[1].Seen;
        var stop = await h.Request(2, false).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.Converged, stop.Outcome);
        Assert.Equal(new[] { 0, 1, 2 }, stopped.ToArray());
        Assert.Equal(PluginState.Pending, h.Status(0));
        Assert.Equal(PluginState.Pending, h.Status(1));
        Assert.Equal(PluginState.Unloaded, h.Status(2));
        Assert.Equal(1, h[3].Starts);
        var load = await h.Request(2, true).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.Converged, load.Outcome);
        Assert.All(h.Host.Instances, info => Assert.Equal(PluginState.Active, info.State));
        Assert.Equal(2, h[0].Starts);
        Assert.Equal(2, h[1].Starts);
        Assert.NotSame(original, h[1].Seen);
        Assert.Equal(1, h[3].Starts);
    }

    [Fact]
    public async Task LateOptionalProviderRebindsConsumerAndItsAbsenceRebindsAgain()
    {
        await using var h = new Harness(("transition.optional", false), ("transition.a", true), ("transition.d", false));
        await h.Host.StartAsync(default);
        Assert.Null(h[0].Seen);
        await h.Request(1, true).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.NotNull(h[0].Seen);
        Assert.Equal(2, h[0].Starts);
        await h.Request(1, false).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Null(h[0].Seen);
        Assert.Equal(3, h[0].Starts);
        Assert.Equal(1, h[2].Starts);
    }

    [Fact]
    public async Task RepeatedTargetsCoalesceWhileAnotherTransitionRuns()
    {
        await using var h = new Harness(("transition.d", false));
        var entered = Signal(); var release = Signal();
        h[0].Start = async (_, _) => { entered.TrySetResult(); await release.Task; };
        var startup = h.Host.StartAsync(default).AsTask();
        await entered.Task.WaitAsync(Limit);
        var receipts = new List<IPluginTransition>();
        try
        {
            for (var i = 0; i < 1000; i++) receipts.Add(h.Request(0, i % 2 == 0));
            var last = h.Request(0, true, true);
            Assert.Equal(1, h.Host.PendingIntentCount);
            foreach (var receipt in receipts) Assert.Equal(PluginTransitionOutcome.Superseded, (await receipt.WaitAsync()).Outcome);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => last.WaitAsync(cancellation.Token).AsTask());
            Assert.False(last.IsCompleted);
            release.TrySetResult(); await startup.WaitAsync(Limit);
            Assert.Equal(PluginTransitionOutcome.Converged, (await last.WaitAsync().AsTask().WaitAsync(Limit)).Outcome);
            Assert.Equal(2, h[0].Starts);
            Assert.Equal(0, h.Host.PendingIntentCount);
        }
        finally { release.TrySetResult(); await startup.WaitAsync(Limit); }
    }

    [Fact]
    public async Task ActivationCanRequestItsOwnReloadWithoutWaitingForItself()
    {
        await using var h = new Harness(("transition.d", false));
        IPluginTransition? requested = null;
        h[0].Start = (context, _) =>
        {
            if (h[0].Starts == 1)
            {
                requested = context.Services.Get<IPluginHost>().RequestState(context.Row.Id, true, true);
                Assert.Throws<InvalidOperationException>(() => requested.WaitAsync());
            }
            return ValueTask.CompletedTask;
        };
        await h.Host.StartAsync(default);
        Assert.NotNull(requested);
        Assert.Equal(PluginTransitionOutcome.Converged, (await requested.WaitAsync().AsTask().WaitAsync(Limit)).Outcome);
        Assert.Equal(2, h[0].Starts);
    }

    [Fact]
    public async Task SupersededActivationCannotPublishOrCreateWorkInAnExistingChild()
    {
        await using var h = new Harness(("transition.a", true), ("transition.d", false));
        var entered = Signal(); var release = Signal(); var rejected = Signal();
        h[0].Start = async (context, token) =>
        {
            var child = await context.CreateResourceScopeAsync("operation");
            entered.TrySetResult(); await release.Task;
            Assert.True(token.IsCancellationRequested);
            Assert.Throws<ObjectDisposedException>(() => context.Services.Register<IA>(new Value(h[0])));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => child.RunTaskAsync("late", _ => ValueTask.CompletedTask).AsTask());
            rejected.TrySetResult();
        };
        await h.Host.StartAsync(default);
        var first = h.Request(0, true);
        await entered.Task.WaitAsync(Limit);
        var latest = h.Request(0, false);
        try
        {
            Assert.Equal(PluginTransitionOutcome.Superseded, (await first.WaitAsync()).Outcome);
            Assert.False(latest.IsCompleted);
            Assert.True(h[0].Alive);
            Assert.Equal(1, h[1].Starts);
        }
        finally { release.TrySetResult(); }
        await rejected.Task.WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.Converged, (await latest.WaitAsync().AsTask().WaitAsync(Limit)).Outcome);
        Assert.False(h[0].Alive);
        Assert.Equal(PluginState.Unloaded, h.Status(0));
    }

    [Fact]
    public async Task CleanupMayChangeItsOwnTargetButCannotWaitForThatTarget()
    {
        await using var h = new Harness(("transition.d", false));
        var submitted = new TaskCompletionSource<IPluginTransition>(TaskCreationOptions.RunContinuationsAsynchronously);
        var once = 0;
        h[0].Stop = context =>
        {
            if (Interlocked.Exchange(ref once, 1) == 0)
            {
                var receipt = context.Services.Get<IPluginHost>().RequestState(context.Row.Id, true);
                Assert.Throws<InvalidOperationException>(() => receipt.WaitAsync());
                submitted.SetResult(receipt);
            }
            return ValueTask.CompletedTask;
        };
        await h.Host.StartAsync(default);
        var stop = h.Request(0, false);
        var latest = await submitted.Task.WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.Superseded, (await stop.WaitAsync()).Outcome);
        Assert.Equal(PluginTransitionOutcome.Converged, (await latest.WaitAsync().AsTask().WaitAsync(Limit)).Outcome);
        Assert.Equal(2, h[0].Starts);
        Assert.Equal(PluginState.Active, h.Status(0));
    }

    [Fact]
    public async Task IncompleteConsumerCleanupRetainsProviderUntilAJoinedRetryDrains()
    {
        await using var h = new Harness(100, ("transition.a", false), ("transition.b", false), ("transition.d", false));
        var entered = Signal(); var release = Signal();
        h[1].Stop = async context => { entered.TrySetResult(); await release.Task; Assert.True(context.Services.Get<IA>().Alive); };
        await h.Host.StartAsync(default);
        var stop = h.Request(0, false);
        try
        {
            await entered.Task.WaitAsync(Limit);
            await Assert.ThrowsAnyAsync<Exception>(() => stop.WaitAsync().AsTask().WaitAsync(Limit));
            Assert.Equal(PluginState.CleanupIncomplete, h.Status(1));
            Assert.True(h[0].Alive);
            Assert.Equal(1, h[0].Starts);
            Assert.Equal(1, h[2].Starts);
        }
        finally { release.TrySetResult(); }
        await h.Request(0, false).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.False(h[0].Alive);
        Assert.Equal(PluginState.Pending, h.Status(1));
    }

    [Fact]
    public async Task FailedNewProviderPreservesConsumersOfTheUnchangedProvider()
    {
        await using var h = new Harness(("transition.a", false), ("transition.b", false), ("transition.a", true));
        await h.Host.StartAsync(default);
        var original = h[1].Seen;
        var failed = h.Request(2, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed.WaitAsync().AsTask().WaitAsync(Limit));
        Assert.Equal(PluginState.Failed, h.Status(2));
        Assert.Equal(PluginState.Active, h.Status(1));
        Assert.True(h[0].Alive);
        Assert.Same(original, h[1].Seen);
        Assert.Equal(1, h[1].Starts);
    }

    [Fact]
    public async Task MissingRequiredServicesRemainPendingAndRecoverAfterProviderAppears()
    {
        await using var h = new Harness(("transition.b", true), ("transition.a", true));
        var pending = await h.Request(0, true).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.WaitingForDependencies, pending.Outcome);
        Assert.Equal(PluginState.Pending, h.Status(0));
        await h.Request(1, true).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(PluginState.Active, h.Status(0));
        Assert.Equal(1, h[0].Starts);
    }

    [Fact]
    public async Task OldWorkFailureCannotOverwriteTheReplacementGeneration()
    {
        await using var h = new Harness(("transition.d", false));
        await h.Host.StartAsync(default);
        var old = Assert.IsType<PluginContext>(h[0].Context);
        await h.Request(0, true, true).WaitAsync().AsTask().WaitAsync(Limit);
        var current = Assert.IsType<PluginContext>(h[0].Context);
        Assert.NotEqual(old.NativeServices!.Activation.Generation, current.NativeServices!.Activation.Generation);
        old.WorkFailureObserver!(new InvalidOperationException("late failure"));
        Assert.Equal(PluginState.Active, h.Status(0));
    }

    [Fact]
    public async Task RepeatedRestartsReleasePriorOwnershipAndNoOpTargetsPreserveState()
    {
        await using var h = new Harness(("transition.a", false));
        await h.Host.StartAsync(default);
        for (var i = 0; i < 50; i++)
        {
            var old = Assert.IsType<PluginContext>(h[0].Context);
            var noOp = await h.Request(0, true).WaitAsync().AsTask().WaitAsync(Limit);
            Assert.Empty(noOp.ChangedRowIds); Assert.Same(old, h[0].Context);
            await h.Request(0, true, true).WaitAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(0, old.RetainedEffectCount);
        }
        var current = Assert.IsType<PluginContext>(h[0].Context);
        Assert.Equal(4, current.NativeServices!.Registry.RetainedMetadataCount);
        Assert.Equal(0, h.Host.PendingIntentCount);
        Assert.Equal(51, h[0].Starts);
    }

    [Fact]
    public async Task HostShutdownSupersedesCleanupReloadButAllowsLaterExternalTargets()
    {
        await using var h = new Harness(("transition.d", false));
        IPluginTransition? fromCleanup = null;
        h[0].Stop = context =>
        { fromCleanup = context.Services.Get<IPluginHost>().RequestState(context.Row.Id, true, true); return ValueTask.CompletedTask; };
        await h.Host.StartAsync(default);
        await h.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        Assert.NotNull(fromCleanup);
        Assert.Equal(PluginTransitionOutcome.Superseded, (await fromCleanup.WaitAsync()).Outcome);
        Assert.Equal(PluginState.Unloaded, h.Status(0));
        Assert.False(h[0].Alive); Assert.Equal(1, h[0].Starts);
        Assert.Equal(0, h.Host.PendingIntentCount);
        await h.Request(0, true).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(PluginState.Active, h.Status(0)); Assert.Equal(2, h[0].Starts);
    }

    [Plugin("transition.conditional", ConditionalProvides = [typeof(IA)])]
    public sealed class Conditional : IPlugin
    { public ValueTask ActivateAsync(IPluginContext context, CancellationToken ct) => Begin(context, ct); }

    [Fact]
    public async Task DirectWithdrawalAndSameInstanceReturnAutomaticallyRebindDependents()
    {
        await using var h = new Harness(("transition.a", false), ("transition.b", false), ("transition.c", false), ("transition.d", false));
        await h.Host.StartAsync(default);
        var original = h[1].Seen!;
        h[0].Registration!.Dispose();
        await h.Host.WaitForBindingChangesAsync().WaitAsync(Limit);
        Assert.Equal(PluginState.Pending, h.Status(1)); Assert.Equal(PluginState.Pending, h.Status(2));
        Assert.True(original.Alive); Assert.Equal(1, h[0].Starts); Assert.Equal(1, h[3].Starts);
        h[0].Registration = h[0].Context!.Services.Register<IA>(original);
        await h.Host.WaitForBindingChangesAsync().WaitAsync(Limit);
        Assert.Same(original, h[1].Seen); Assert.Equal(2, h[1].Starts); Assert.Equal(2, h[2].Starts);
        Assert.Equal(1, h[0].Starts); Assert.Equal(1, h[3].Starts);
    }

    [Fact]
    public async Task EmptyConditionalProviderDoesNotRestartAnUnchangedConsumer()
    {
        await using var h = new Harness(("transition.optional", false), ("transition.conditional", true));
        await h.Host.StartAsync(default); var old = h[0].Context;
        await h.Request(1, true).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(1, h[0].Starts); Assert.Same(old, h[0].Context); Assert.Null(h[0].Seen);
        using var contribution = h[1].Context!.Services.Register<IA>(new Value(h[1]));
        await h.Host.WaitForBindingChangesAsync().WaitAsync(Limit);
        Assert.Equal(2, h[0].Starts); Assert.NotNull(h[0].Seen);
        contribution.Dispose();
        await h.Host.WaitForBindingChangesAsync().WaitAsync(Limit);
        Assert.Equal(3, h[0].Starts); Assert.Null(h[0].Seen); Assert.Equal(1, h[1].Starts);
    }

    [Fact]
    public async Task TransientPublicationBurstCoalescesWithoutChangingAnAbsentView()
    {
        await using var h = new Harness(("transition.optional", false), ("transition.conditional", false), ("transition.conditional", false));
        await h.Host.StartAsync(default); var old = h[0].Context;
        var entered = Signal(); var release = Signal();
        h[2].Stop = async _ => { entered.SetResult(); await release.Task; };
        var blocked = h.Host.UnloadAsync(h.Rows[2].Id, default).AsTask();
        await entered.Task.WaitAsync(Limit);
        try
        {
            for (var i = 0; i < 1000; i++)
                h[1].Context!.Services.Register<IA>(new Value(h[1])).Dispose();
        }
        finally { release.TrySetResult(); await blocked.WaitAsync(Limit); }
        await h.Host.WaitForBindingChangesAsync().WaitAsync(Limit);
        Assert.Equal(1, h[0].Starts); Assert.Same(old, h[0].Context);
        Assert.Equal(1, Assert.IsType<PluginContext>(h[1].Context).RetainedEffectCount);
    }

    [Fact]
    public async Task AnUnrelatedRealmDoesNotInvalidateRootConsumers()
    {
        await using var h = new Harness(("transition.a", false), ("transition.b", false));
        await h.Host.StartAsync(default); var original = h[1].Context;
        var registry = Assert.IsType<PluginContext>(h[0].Context).NativeServices!.Registry;
        var scope = registry.Runtime.RootScope.CreateChild([typeof(IA)]);
        using var other = registry.For(scope, registry.Runtime.NewActivation("isolated")).Register<IA>(new Value(h[0]));
        await h.Host.WaitForBindingChangesAsync().WaitAsync(Limit);
        Assert.Same(original, h[1].Context); Assert.Equal(1, h[1].Starts);
    }

    [Fact]
    public async Task DirectLayerChangesPreserveDecoratorBaseViews()
    {
        await using var h = new Harness(("transition.base", false), ("transition.layer", false), ("transition.optional", false));
        await h.Host.StartAsync(default);
        Assert.False(h[2].Seen!.Alive);
        using var extra = h[1].Context!.Services.Decorate<IA>(inner => new Inverted(inner));
        await h.Host.WaitForBindingChangesAsync().WaitAsync(Limit);
        Assert.True(h[2].Seen!.Alive); Assert.Equal(2, h[2].Starts);
        Assert.Equal(1, h[1].Starts); Assert.Equal(1, h[0].Starts);
        extra.Dispose(); await h.Host.WaitForBindingChangesAsync().WaitAsync(Limit);
        Assert.False(h[2].Seen!.Alive); Assert.Equal(3, h[2].Starts); Assert.Equal(1, h[1].Starts);
    }

    [Fact]
    public async Task UnstableDependenciesStopAtTheConfiguredAutomaticAttemptLimit()
    {
        await using var h = new Harness(10000, 3, ("transition.base", false), ("transition.optional", false));
        await h.Host.StartAsync(default);
        var registry = Assert.IsType<PluginContext>(h[0].Context).NativeServices!.Registry;
        var layers = new ConcurrentBag<IDisposable>();
        h[1].Start = (_, _) => { layers.Add(registry.Decorate<IA>(inner => new Inverted(inner))); return ValueTask.CompletedTask; };
        try
        {
            layers.Add(registry.Decorate<IA>(inner => new Inverted(inner)));
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Host.WaitForBindingChangesAsync().WaitAsync(Limit));
            Assert.Contains("attempt limit 3", error.Message);
            Assert.Equal(PluginState.Failed, h.Status(1)); Assert.Equal(4, h[1].Starts);
            Assert.Equal(1, h[0].Starts); Assert.True(h[0].Alive);
        }
        finally
        {
            h[1].Start = null;
            foreach (var layer in layers) layer.Dispose();
            await h.Host.WaitForBindingChangesAsync().WaitAsync(Limit);
        }
    }

    [Plugin("transaction.provider", Requires = [typeof(IPluginHost)], Provides = [typeof(IA), typeof(IB)])]
    public sealed class TransactionProvider : IPlugin
    {
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken ct)
        {
            await Begin(context, ct);
            context.Services.Register<IA>(new Value(States[context.Row.Id]));
            if (States[context.Row.Id].AfterFirst is { } hook) await hook(context, ct);
            context.Services.Register<IB>(new MiddleValue());
        }
    }
    [Plugin("transaction.layer", Decorates = [typeof(IA)])]
    public sealed class TransactionLayer : IPlugin
    {
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken ct)
        {
            await Begin(context, ct);
            context.Services.Decorate<IA>(inner => { Interlocked.Increment(ref States[context.Row.Id].Factories); return new Inverted(inner); });
            if (States[context.Row.Id].AfterFirst is { } hook) await hook(context, ct);
        }
    }
    [Plugin("transaction.observer", Observes = [typeof(IA)])]
    public sealed class TransactionObserver : IPlugin
    {
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken ct)
        {
            await Begin(context, ct);
            context.Services.WhenAvailable<IA>(value =>
            {
                var state = States[context.Row.Id]; state.Seen = value; state.Observed++;
                if (state.ObserverThrows) throw new FormatException("observer rejected");
            });
        }
    }

    [Fact]
    public async Task ActivationPublishesBothServicesOnlyAfterTheBodyCompletes()
    {
        await using var h = new Harness(("transaction.provider", false));
        var entered = Signal(); var release = Signal();
        h[0].AfterFirst = async (context, _) => { Assert.NotNull(context.Services.Get<IA>()); entered.SetResult(); await release.Task; };
        var startup = h.Host.StartAsync(default).AsTask(); await entered.Task.WaitAsync(Limit);
        var registry = Assert.IsType<PluginContext>(h[0].Context).NativeServices!.Registry;
        var observed = 0;
        using var waiter = registry.WhenAvailable<IA>(value => { Assert.True(registry.TryGet<IB>(out _)); observed++; });
        try
        {
            Assert.False(registry.TryGet<IA>(out _)); Assert.False(registry.TryGet<IB>(out _));
            Assert.False(registry.IsRegistered(typeof(IA))); Assert.Equal(0, observed);
        }
        finally { release.TrySetResult(); }
        await startup.WaitAsync(Limit);
        Assert.Equal(1, observed); Assert.True(registry.TryGet<IA>(out _)); Assert.True(registry.TryGet<IB>(out _));
    }

    [Fact]
    public async Task FailedActivationDoesNotPublishAServiceOrRestartItsConsumer()
    {
        await using var h = new Harness(("transition.optional", false), ("transaction.provider", true));
        await h.Host.StartAsync(default); var old = h[0].Context;
        h[1].AfterFirst = (_, _) => throw new InvalidOperationException("incomplete activation");
        var request = h.Request(1, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => request.WaitAsync().AsTask().WaitAsync(Limit));
        var registry = Assert.IsType<PluginContext>(h[1].Context).NativeServices!.Registry;
        Assert.False(registry.TryGet<IA>(out _)); Assert.False(registry.TryGet<IB>(out _));
        Assert.Equal(PluginState.Failed, h.Status(1)); Assert.Equal(1, h[0].Starts); Assert.Same(old, h[0].Context);
    }

    [Fact]
    public async Task StagedDecoratorDoesNotChangeLiveOrFrozenViewsUntilCommit()
    {
        await using var h = new Harness(("transition.base", false), ("transition.optional", false), ("transaction.layer", true));
        await h.Host.StartAsync(default);
        var registry = Assert.IsType<PluginContext>(h[0].Context).NativeServices!.Registry;
        var live = registry.Get<IA>(); var frozen = h[1].Seen!;
        var entered = Signal(); var release = Signal();
        h[2].AfterFirst = async (_, _) => { entered.SetResult(); await release.Task; };
        var request = h.Request(2, true); await entered.Task.WaitAsync(Limit);
        try { Assert.True(live.Alive); Assert.True(frozen.Alive); Assert.Equal(1, h[1].Starts); }
        finally { release.TrySetResult(); }
        await request.WaitAsync().AsTask().WaitAsync(Limit);
        Assert.False(live.Alive); Assert.False(h[1].Seen!.Alive); Assert.True(frozen.Alive);
        Assert.Equal(1, h[2].Factories); Assert.Equal(2, h[1].Starts);
    }

    [Fact]
    public async Task FailedStagedDecoratorLeavesTheOriginalProjectionUntouched()
    {
        await using var h = new Harness(("transition.base", false), ("transition.optional", false), ("transaction.layer", true));
        await h.Host.StartAsync(default); var old = h[1].Context;
        h[2].AfterFirst = (_, _) => throw new InvalidOperationException("layer initialization failed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Request(2, true).WaitAsync().AsTask().WaitAsync(Limit));
        Assert.True(h[1].Seen!.Alive); Assert.Same(old, h[1].Context); Assert.Equal(1, h[1].Starts); Assert.Equal(1, h[2].Factories);
    }

    [Fact]
    public async Task AvailabilityObserverFailureDoesNotUndoACommittedProvider()
    {
        await using var h = new Harness(("transaction.observer", false), ("transaction.provider", false));
        h[0].ObserverThrows = true;
        await h.Host.StartAsync(default);
        Assert.Equal(1, h[0].Observed); Assert.Equal(PluginState.WorkFailed, h.Status(0));
        Assert.Equal(PluginState.Active, h.Status(1)); Assert.True(h[1].Alive);
        var registry = Assert.IsType<PluginContext>(h[1].Context).NativeServices!.Registry;
        Assert.True(registry.TryGet<IA>(out _)); Assert.True(registry.TryGet<IB>(out _));
    }

    [Plugin("transaction.multiple", ProvidesMany = [typeof(IA)])]
    public sealed class TransactionMultiple : IPlugin
    {
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken ct)
        {
            await Begin(context, ct); context.Services.RegisterMany<IA>(new Value(States[context.Row.Id]));
            if (States[context.Row.Id].AfterFirst is { } hook) await hook(context, ct);
            context.Services.RegisterMany<IA>(new Value(States[context.Row.Id]));
        }
    }
    [Plugin("transaction.mixed", Provides = [typeof(IB)], Decorates = [typeof(IA)])]
    public sealed class TransactionMixed : IPlugin
    {
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken ct)
        {
            await Begin(context, ct); context.Services.Register<IB>(new MiddleValue());
            context.Services.Decorate<IA>(inner => new Inverted(inner));
            if (States[context.Row.Id].AfterFirst is { } hook) await hook(context, ct);
        }
    }

    [Fact]
    public async Task MultipleContributionsRemainHiddenUntilTheyCanBeSelectedTogether()
    {
        await using var h = new Harness(("transaction.multiple", false), ("transition.extensions", false));
        var entered = Signal(); var release = Signal();
        h[0].AfterFirst = async (context, _) => { Assert.Single(context.Services.GetAll<IA>()); entered.SetResult(); await release.Task; };
        var startup = h.Host.StartAsync(default).AsTask(); await entered.Task.WaitAsync(Limit);
        var registry = Assert.IsType<PluginContext>(h[0].Context).NativeServices!.Registry;
        try { Assert.Empty(registry.GetAll<IA>()); Assert.False(registry.TryGet<IA>(out _)); Assert.Equal(0, h[1].Starts); }
        finally { release.TrySetResult(); }
        await startup.WaitAsync(Limit); Assert.Equal(2, h[1].Many.Count); Assert.Equal(1, h[1].Starts);
    }

    [Fact]
    public async Task AvailabilitySeesTheCompleteServiceAndDecoratorTransaction()
    {
        await using var h = new Harness(("transition.base", false), ("transaction.mixed", true));
        await h.Host.StartAsync(default);
        var registry = Assert.IsType<PluginContext>(h[0].Context).NativeServices!.Registry; var live = registry.Get<IA>();
        var entered = Signal(); var release = Signal(); var observed = 0;
        h[1].AfterFirst = async (_, _) => { entered.SetResult(); await release.Task; };
        using var waiter = registry.WhenAvailable<IB>(value => { Assert.False(live.Alive); observed++; });
        var request = h.Request(1, true); await entered.Task.WaitAsync(Limit);
        try { Assert.True(live.Alive); Assert.False(registry.TryGet<IB>(out _)); Assert.Equal(0, observed); }
        finally { release.TrySetResult(); }
        await request.WaitAsync().AsTask().WaitAsync(Limit);
        Assert.False(live.Alive); Assert.True(registry.TryGet<IB>(out _)); Assert.Equal(1, observed);
    }

    [Fact]
    public async Task SupersededServiceTransactionNeverReachesAvailabilityObservers()
    {
        await using var h = new Harness(("transaction.provider", true));
        var entered = Signal(); var release = Signal();
        h[0].AfterFirst = async (_, _) => { entered.SetResult(); await release.Task; };
        var first = h.Request(0, true); await entered.Task.WaitAsync(Limit);
        var registry = Assert.IsType<PluginContext>(h[0].Context).NativeServices!.Registry;
        var observed = 0; using var waiter = registry.WhenAvailable<IA>(value => observed++);
        var stop = h.Request(0, false);
        try { Assert.Equal(PluginTransitionOutcome.Superseded, (await first.WaitAsync()).Outcome); Assert.False(registry.TryGet<IA>(out _)); }
        finally { release.TrySetResult(); }
        await stop.WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(0, observed); Assert.False(registry.TryGet<IA>(out _)); Assert.False(registry.TryGet<IB>(out _));
        Assert.Equal(PluginState.Unloaded, h.Status(0));
    }

    [Plugin("parallel.reader", Requires = [typeof(IA)])]
    public sealed class ParallelReader : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken ct)
        { States[context.Row.Id].Seen = context.Services.Get<IA>(); return Begin(context, ct); }
    }

    [Fact]
    public async Task BlockedActivationDoesNotBlockAnIndependentTarget()
    {
        await using var h = new Harness(("transition.d", true), ("transition.d", true));
        var entered = Signal(); var release = Signal();
        h[0].Start = async (_, _) => { entered.SetResult(); await release.Task; };
        var blocked = h.Request(0, true); await entered.Task.WaitAsync(Limit);
        try
        {
            var independent = await h.Request(1, true).WaitAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(PluginTransitionOutcome.Converged, independent.Outcome);
            Assert.Equal(PluginState.Active, h.Status(1)); Assert.False(blocked.IsCompleted);
        }
        finally { release.TrySetResult(); await blocked.WaitAsync().AsTask().WaitAsync(Limit); }
    }

    [Fact]
    public async Task ConsumersCanActivateInParallelWhileSharingAReadOnlyProvider()
    {
        await using var h = new Harness(("transition.a", false), ("parallel.reader", true), ("parallel.reader", true));
        await h.Host.StartAsync(default);
        var firstEntered = Signal(); var secondEntered = Signal(); var release = Signal();
        h[1].Start = async (_, _) => { firstEntered.SetResult(); await release.Task; };
        h[2].Start = async (_, _) => { secondEntered.SetResult(); await release.Task; };
        var first = h.Request(1, true); var second = h.Request(2, true);
        try
        {
            await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(Limit);
            Assert.Equal(2, Assert.IsType<PluginContext>(h[0].Context).NativeServices!.Activation.Publication.Dependents);
            Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(first.WaitAsync().AsTask(), second.WaitAsync().AsTask()).WaitAsync(Limit);
        }
        Assert.Equal(1, h[0].Starts);
    }

    [Fact]
    public async Task ARowNeverRunsTwoActivationBodiesForSupersededTargets()
    {
        await using var h = new Harness(("transition.d", true));
        var entered = Signal(); var release = Signal(); var running = 0; var maximum = 0;
        h[0].Start = async (_, _) =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref running));
            entered.TrySetResult(); await release.Task; Interlocked.Decrement(ref running);
        };
        var first = h.Request(0, true); await entered.Task.WaitAsync(Limit);
        var middle = h.Request(0, false); var last = h.Request(0, true);
        try
        {
            Assert.Equal(PluginTransitionOutcome.Superseded, (await first.WaitAsync()).Outcome);
            Assert.Equal(PluginTransitionOutcome.Superseded, (await middle.WaitAsync()).Outcome);
            Assert.False(last.IsCompleted); Assert.Equal(1, h[0].Starts);
        }
        finally { release.TrySetResult(); }
        await last.WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(1, maximum); Assert.Equal(2, h[0].Starts);
    }

    [Fact]
    public async Task IncompleteCleanupDoesNotQuarantineAnIndependentTarget()
    {
        await using var h = new Harness(100, ("transition.a", false), ("parallel.reader", false), ("transition.d", true));
        await h.Host.StartAsync(default);
        var entered = Signal(); var release = Signal();
        h[1].Stop = async context => { entered.TrySetResult(); await release.Task; Assert.True(context.Services.Get<IA>().Alive); };
        var closing = h.Request(1, false); await entered.Task.WaitAsync(Limit);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => closing.WaitAsync().AsTask().WaitAsync(Limit));
            Assert.Equal(PluginState.CleanupIncomplete, h.Status(1)); Assert.True(h[0].Alive);
            var independent = await h.Request(2, true).WaitAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(PluginTransitionOutcome.Converged, independent.Outcome); Assert.Equal(PluginState.Active, h.Status(2));
        }
        finally { release.TrySetResult(); await h.Request(1, false).WaitAsync().AsTask().WaitAsync(Limit); }
        Assert.Equal(PluginState.Unloaded, h.Status(1)); Assert.True(h[0].Alive);
    }

    [Fact]
    public async Task DependencyWithdrawalFencesBothConcurrentConsumerActivations()
    {
        await using var h = new Harness(("transition.a", false), ("parallel.reader", true), ("parallel.reader", true));
        await h.Host.StartAsync(default);
        var one = Signal(); var two = Signal(); var rejected = 0;
        h[1].Start = (context, token) => Body(1, one, context, token);
        h[2].Start = (context, token) => Body(2, two, context, token);
        async ValueTask Body(int index, TaskCompletionSource entered, IPluginContext context, CancellationToken token)
        {
            if (h[index].Starts != 1) return;
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                await Assert.ThrowsAsync<ObjectDisposedException>(() => context.RunTaskAsync("late", _ => ValueTask.CompletedTask).AsTask());
                Interlocked.Increment(ref rejected); throw;
            }
        }
        var first = h.Request(1, true); var second = h.Request(2, true);
        await Task.WhenAll(one.Task, two.Task).WaitAsync(Limit);
        h[0].Registration!.Dispose();
        await h.Host.WaitForBindingChangesAsync().WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.Superseded, (await first.WaitAsync()).Outcome);
        Assert.Equal(PluginTransitionOutcome.Superseded, (await second.WaitAsync()).Outcome);
        Assert.Equal(2, rejected); Assert.Equal(PluginState.Pending, h.Status(1)); Assert.Equal(PluginState.Pending, h.Status(2));
        Assert.Equal(0, Assert.IsType<PluginContext>(h[0].Context).NativeServices!.Activation.Publication.Dependents);
        h[0].Registration = h[0].Context!.Services.Register<IA>(new Value(h[0]));
        await h.Host.WaitForBindingChangesAsync().WaitAsync(Limit);
        Assert.Equal(PluginState.Active, h.Status(1)); Assert.Equal(PluginState.Active, h.Status(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderTransitionWaitsForBothConcurrentConsumersToDrain(bool restart)
    {
        await using var h = new Harness(("transition.a", false), ("parallel.reader", true),
            ("parallel.reader", true), ("transition.d", true), ("parallel.reader", true));
        await h.Host.StartAsync(default);
        var provider = Assert.IsType<PluginContext>(h[0].Context);
        var activationEntered = new[] { Signal(), Signal() };
        var cleanupEntered = new[] { Signal(), Signal() };
        var activationRelease = Signal();
        var cleanupRelease = new[] { Signal(), Signal() };
        var cleanupCount = 0; var providerCloses = 0; var cleanupsAtProviderClose = -1;
        for (var index = 0; index < 2; index++)
        {
            var slot = index; var row = slot + 1;
            h[row].Start = async (_, _) =>
            {
                if (h[row].Starts != 1) return;
                activationEntered[slot].TrySetResult();
                await activationRelease.Task;
            };
            h[row].Stop = async context =>
            {
                cleanupEntered[slot].TrySetResult();
                await cleanupRelease[slot].Task;
                Assert.True(context.Services.Get<IA>().Alive);
                Interlocked.Increment(ref cleanupCount);
            };
        }
        h[0].Stop = _ =>
        {
            Volatile.Write(ref cleanupsAtProviderClose, Volatile.Read(ref cleanupCount));
            Interlocked.Increment(ref providerCloses);
            return ValueTask.CompletedTask;
        };
        var first = h.Request(1, true); var second = h.Request(2, true);
        IPluginTransition? target = null; IPluginTransition? laterReader = null;
        try
        {
            await Task.WhenAll(activationEntered.Select(item => item.Task)).WaitAsync(Limit);
            var firstContext = Assert.IsType<PluginContext>(h[1].Context);
            var secondContext = Assert.IsType<PluginContext>(h[2].Context);
            Assert.Equal(2, provider.NativeServices!.Activation.Publication.Dependents);
            target = h.Request(0, restart, restart);
            laterReader = h.Request(4, true);
            Assert.Equal(PluginTransitionOutcome.Superseded, (await first.WaitAsync()).Outcome);
            Assert.Equal(PluginTransitionOutcome.Superseded, (await second.WaitAsync()).Outcome);
            Assert.Equal(PluginTransitionOutcome.Converged,
                (await h.Request(3, true).WaitAsync().AsTask().WaitAsync(Limit)).Outcome);
            Assert.False(target.IsCompleted); Assert.True(h[0].Alive);
            Assert.False(laterReader.IsCompleted); Assert.Equal(0, h[4].Starts);
            Assert.Equal(0, Volatile.Read(ref providerCloses));
            activationRelease.TrySetResult();
            await Task.WhenAll(cleanupEntered.Select(item => item.Task)).WaitAsync(Limit);
            Assert.Equal(2, provider.NativeServices.Activation.Publication.Dependents);
            cleanupRelease[0].TrySetResult();
            await firstContext.UnwindAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(1, provider.NativeServices.Activation.Publication.Dependents);
            Assert.False(target.IsCompleted); Assert.True(h[0].Alive);
            Assert.Equal(0, Volatile.Read(ref providerCloses));
            cleanupRelease[1].TrySetResult();
            await secondContext.UnwindAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(PluginTransitionOutcome.Converged,
                (await target.WaitAsync().AsTask().WaitAsync(Limit)).Outcome);
            Assert.Equal(restart ? PluginTransitionOutcome.Converged : PluginTransitionOutcome.WaitingForDependencies,
                (await laterReader.WaitAsync().AsTask().WaitAsync(Limit)).Outcome);
            await h.Host.WaitForBindingChangesAsync().WaitAsync(Limit);
            Assert.Equal(0, provider.NativeServices.Activation.Publication.Dependents);
            Assert.Equal(1, Volatile.Read(ref providerCloses));
            Assert.Equal(2, Volatile.Read(ref cleanupsAtProviderClose));
            Assert.Equal(restart ? PluginState.Active : PluginState.Unloaded, h.Status(0));
            Assert.Equal(restart ? PluginState.Active : PluginState.Pending, h.Status(1));
            Assert.Equal(restart ? PluginState.Active : PluginState.Pending, h.Status(2));
            Assert.Equal(restart ? 2 : 1, h[0].Starts);
            Assert.Equal(restart ? 2 : 1, h[1].Starts);
            Assert.Equal(restart ? 2 : 1, h[2].Starts);
            Assert.Equal(restart ? 1 : 0, h[4].Starts);
            Assert.Equal(restart ? PluginState.Active : PluginState.Pending, h.Status(4));
            Assert.Equal(1, h[3].Starts);
            if (restart)
            {
                var replacement = Assert.IsType<PluginContext>(h[0].Context);
                Assert.NotSame(provider, replacement);
                Assert.Equal(3, replacement.NativeServices!.Activation.Publication.Dependents);
                Assert.NotSame(firstContext, h[1].Context);
                Assert.NotSame(secondContext, h[2].Context);
            }
        }
        finally
        {
            activationRelease.TrySetResult();
            foreach (var release in cleanupRelease) release.TrySetResult();
            h[0].Stop = null;
            if (target is not null) await target.WaitAsync().AsTask().WaitAsync(Limit);
            if (laterReader is not null) await laterReader.WaitAsync().AsTask().WaitAsync(Limit);
            await h.Host.WaitForBindingChangesAsync().WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task CompletedExecutionContextDoesNotRestrictLaterHostOperations()
    {
        await using var h = new Harness(("transition.d", true), ("transition.a", true));
        var release = Signal(); Task? continuation = null;
        h[0].Start = (_, _) =>
        {
            continuation = Task.Run(async () => { await release.Task; await h.Host.LoadAsync(h.Rows[1].Id, default); });
            return ValueTask.CompletedTask;
        };
        await h.Request(0, true).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.NotNull(continuation); release.SetResult(); await continuation.WaitAsync(Limit);
        Assert.Equal(PluginState.Active, h.Status(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedGraphTransitionsDrainNestedWorkAndReturnMetadataToBaseline(bool restart)
    {
        const int cycles = 12;
        await using var h = new Harness(("transition.a", false), ("parallel.reader", false), ("parallel.reader", false));
        IOwnedTask? currentWork = null; var workStopped = 0; var released = 0;
        h[1].Start = async (context, _) =>
        {
            var child = await context.CreateResourceScopeAsync("child");
            var leaf = await child.CreateResourceScopeAsync("leaf");
            leaf.Effect(() => { Assert.True(context.Services.Get<IA>().Alive); Interlocked.Increment(ref released); });
            var started = Signal();
            currentWork = await leaf.RunTaskAsync("wait-for-close", async token =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                finally { Interlocked.Increment(ref workStopped); }
            });
            await started.Task.WaitAsync(Limit);
        };
        await h.Host.StartAsync(default);
        var registry = Assert.IsType<PluginContext>(h[0].Context).NativeServices!.Registry;
        var activeMetadata = registry.RetainedMetadataCount;
        var activeEffects = Enumerable.Range(0, 3).Select(index => Assert.IsType<PluginContext>(h[index].Context).RetainedEffectCount).ToArray();
        Assert.Equal(3, Assert.IsType<PluginContext>(h[1].Context).ResourceScopeCount);
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var previous = Enumerable.Range(0, 3).Select(index => Assert.IsType<PluginContext>(h[index].Context)).ToArray();
            var oldWork = Assert.IsAssignableFrom<IOwnedTask>(currentWork);
            await h.Request(0, restart, restart).WaitAsync().AsTask().WaitAsync(Limit);
            AssertDrained(previous);
            Assert.True(oldWork.Completion.IsCompletedSuccessfully);
            if (!restart)
            {
                Assert.Equal(3, registry.RetainedMetadataCount);
                Assert.False(registry.TryGet<IA>(out _));
                Assert.Equal(PluginState.Pending, h.Status(1));
                Assert.Equal(PluginState.Pending, h.Status(2));
                await h.Request(0, true).WaitAsync().AsTask().WaitAsync(Limit);
            }
            Assert.Equal(activeMetadata, registry.RetainedMetadataCount);
            for (var index = 0; index < 3; index++)
            {
                var current = Assert.IsType<PluginContext>(h[index].Context);
                Assert.NotSame(previous[index], current);
                Assert.True(current.NativeServices!.Activation.Generation > previous[index].NativeServices!.Activation.Generation);
                Assert.Equal(activeEffects[index], current.RetainedEffectCount);
                Assert.Equal(index == 1 ? 3 : 1, current.ResourceScopeCount);
            }
            Assert.Equal(2, Assert.IsType<PluginContext>(h[0].Context).NativeServices!.Activation.Publication.Dependents);
            Assert.Equal(cycle + 1, Volatile.Read(ref workStopped));
            Assert.Equal(cycle + 1, Volatile.Read(ref released));
            Assert.Equal(0, h.Host.PendingIntentCount);
        }
        var final = Enumerable.Range(0, 3).Select(index => Assert.IsType<PluginContext>(h[index].Context)).ToArray();
        await h.Request(0, false).WaitAsync().AsTask().WaitAsync(Limit);
        AssertDrained(final);
        Assert.Equal(3, registry.RetainedMetadataCount);
        Assert.Equal(cycles + 1, workStopped); Assert.Equal(cycles + 1, released);
        Assert.True(currentWork!.Completion.IsCompletedSuccessfully);

        static void AssertDrained(IEnumerable<PluginContext> contexts)
        {
            foreach (var context in contexts)
            {
                Assert.Equal(0, context.RetainedEffectCount);
                Assert.Equal(0, context.RetainedExecutionCount);
                Assert.Equal(0, context.ResourceScopeCount);
                Assert.Equal(0, context.NativeServices!.Activation.Publication.Dependents);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownDrainsConcurrentActivationsBeforeReleasingTheirProvider(bool cooperative)
    {
        await using var h = new Harness(("transition.a", false), ("parallel.reader", true),
            ("parallel.reader", true), ("transition.d", false));
        await h.Host.StartAsync(default);
        var entered = new[] { Signal(), Signal() }; var release = Signal();
        var providerClosing = Signal(); var closeRelease = Signal(); var closes = 0;
        h[0].Stop = async _ => { Interlocked.Increment(ref closes); providerClosing.TrySetResult(); await closeRelease.Task; };
        for (var index = 0; index < 2; index++)
        {
            var slot = index;
            h[index + 1].Start = async (_, token) =>
            {
                entered[slot].TrySetResult();
                if (cooperative) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                else { await release.Task; token.ThrowIfCancellationRequested(); }
            };
        }
        var first = h.Request(1, true); var second = h.Request(2, true);
        Task? shutdown = null; Task? joined = null;
        try
        {
            await Task.WhenAll(entered.Select(item => item.Task)).WaitAsync(Limit);
            var provider = Assert.IsType<PluginContext>(h[0].Context);
            Assert.Equal(2, provider.NativeServices!.Activation.Publication.Dependents);
            shutdown = h.Host.ShutdownAsync(default).AsTask();
            Assert.Equal(PluginTransitionOutcome.Superseded, (await first.WaitAsync()).Outcome);
            Assert.Equal(PluginTransitionOutcome.Superseded, (await second.WaitAsync()).Outcome);
            if (!cooperative)
            {
                Assert.False(shutdown.IsCompleted); Assert.Equal(0, closes);
                Assert.True(h[0].Alive); Assert.Equal(2, provider.NativeServices.Activation.Publication.Dependents);
            }
            release.TrySetResult();
            await providerClosing.Task.WaitAsync(Limit);
            Assert.Equal(0, provider.NativeServices.Activation.Publication.Dependents);
            Assert.Equal(PluginTransitionOutcome.Superseded, (await h.Request(3, true, true).WaitAsync()).Outcome);
            joined = h.Host.ShutdownAsync(default).AsTask();
            Assert.False(shutdown.IsCompleted); Assert.False(joined.IsCompleted); Assert.Equal(1, closes);
        }
        finally
        {
            release.TrySetResult(); closeRelease.TrySetResult();
            if (shutdown is not null) await shutdown.WaitAsync(Limit);
            if (joined is not null) await joined.WaitAsync(Limit);
        }
        Assert.All(h.Host.Instances, item => Assert.Equal(PluginState.Unloaded, item.State));
        Assert.Equal(1, closes); Assert.Equal(0, h.Host.PendingIntentCount);
        for (var index = 0; index < 4; index++)
            Assert.Equal(0, Assert.IsType<PluginContext>(h[index].Context).RetainedEffectCount);
        Assert.Equal(3, Assert.IsType<PluginContext>(h[0].Context).NativeServices!.Registry.RetainedMetadataCount);
        await h.Request(3, true).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(PluginState.Active, h.Status(3)); Assert.Equal(2, h[3].Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelingShutdownObservationLeavesTheDrainAndAdmissionFenceOwned(bool strictLoad)
    {
        await using var h = new Harness(("transition.a", true), ("transition.d", true));
        using var cancellation = new CancellationTokenSource();
        var entered = Signal(); var release = Signal(); var closes = 0;
        h[0].Start = async (_, token) => { entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested(); };
        h[0].Stop = _ => { Interlocked.Increment(ref closes); return ValueTask.CompletedTask; };
        IPluginTransition? request = strictLoad ? null : h.Request(0, true);
        var loading = strictLoad ? h.Host.LoadAsync(h.Rows[0].Id, default).AsTask() : null;
        Task? observer = null; Task? joined = null;
        try
        {
            await entered.Task.WaitAsync(Limit);
            observer = h.Host.ShutdownAsync(cancellation.Token).AsTask();
            if (request is not null) Assert.Equal(PluginTransitionOutcome.Superseded, (await request.WaitAsync()).Outcome);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer.WaitAsync(Limit));
            Assert.Equal(PluginTransitionOutcome.Superseded, (await h.Request(1, true).WaitAsync()).Outcome);
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Host.LoadAsync(h.Rows[1].Id, default).AsTask());
            joined = h.Host.ShutdownAsync(default).AsTask();
            Assert.False(joined.IsCompleted); Assert.Equal(0, closes);
            Assert.False(Assert.IsType<PluginContext>(h[0].Context).NativeServices!.Registry.TryGet<IA>(out _));
        }
        finally
        {
            cancellation.Cancel(); release.TrySetResult();
            if (joined is not null) await joined.WaitAsync(Limit);
            else await h.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
            if (loading is not null) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading.WaitAsync(Limit));
        }
        Assert.Equal(1, closes);
        Assert.All(h.Host.Instances, item => Assert.Equal(PluginState.Unloaded, item.State));
        Assert.Equal(0, Assert.IsType<PluginContext>(h[0].Context).RetainedEffectCount);
        Assert.Equal(0, h[1].Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrictOperationsAndTargetsAllowIndependentScopes(bool strictFirst)
    {
        await using var h = new Harness(("transition.d", true), ("transition.d", true));
        var entered = Signal(); var release = Signal();
        h[0].Start = async (_, _) =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Host.LoadAsync(h.Rows[1].Id, default).AsTask());
            entered.TrySetResult(); await release.Task;
        };
        Task first = strictFirst ? h.Host.LoadAsync(h.Rows[0].Id, default).AsTask() : h.Request(0, true).WaitAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Host.LoadAsync(h.Rows[0].Id, default).AsTask());
            if (strictFirst) await h.Request(1, true).WaitAsync().AsTask().WaitAsync(Limit);
            else await h.Host.LoadAsync(h.Rows[1].Id, default).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginState.Active, h.Status(1)); Assert.False(first.IsCompleted);
            var reloaded = await h.Host.ReloadAsync(h.Rows[1].Id, default).AsTask().WaitAsync(Limit);
            Assert.Single(reloaded.ReloadedRowIds);
            await h.Host.UnloadAsync(h.Rows[1].Id, default).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginState.Unloaded, h.Status(1));
            await h.Host.LoadAsync(h.Rows[1].Id, default).AsTask().WaitAsync(Limit);
            Assert.Equal(3, h[1].Starts); Assert.Equal(1, h[0].Starts); Assert.False(first.IsCompleted);
        }
        finally { release.TrySetResult(); await first.WaitAsync(Limit); }
    }

    [Fact]
    public async Task StrictConsumersCanActivateTogetherWhileRetainingTheSameProvider()
    {
        await using var h = new Harness(("transition.a", false), ("parallel.reader", true), ("parallel.reader", true));
        await h.Host.StartAsync(default);
        var one = Signal(); var two = Signal(); var release = Signal();
        h[1].Start = async (_, _) => { one.TrySetResult(); await release.Task; };
        h[2].Start = async (_, _) => { two.TrySetResult(); await release.Task; };
        var first = h.Host.LoadAsync(h.Rows[1].Id, default).AsTask();
        var second = h.Host.LoadAsync(h.Rows[2].Id, default).AsTask();
        try
        {
            await Task.WhenAll(one.Task, two.Task).WaitAsync(Limit);
            Assert.Equal(2, Assert.IsType<PluginContext>(h[0].Context).NativeServices!.Activation.Publication.Dependents);
            Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
        }
        finally { release.TrySetResult(); await Task.WhenAll(first, second).WaitAsync(Limit); }
        Assert.Equal(1, h[0].Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrictLoadCancellationReleasesItsScopeWithoutPublishing(bool supersede)
    {
        await using var h = new Harness(("transition.a", true), ("transition.d", true));
        using var cancellation = new CancellationTokenSource();
        var entered = Signal(); var release = Signal();
        h[0].Start = async (_, token) =>
        {
            entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested();
        };
        var loading = h.Host.LoadAsync(h.Rows[0].Id, cancellation.Token).AsTask();
        IPluginTransition? target = null;
        try
        {
            await entered.Task.WaitAsync(Limit);
            var context = Assert.IsType<PluginContext>(h[0].Context);
            if (supersede) target = h.Request(0, false); else cancellation.Cancel();
            await h.Host.LoadAsync(h.Rows[1].Id, default).AsTask().WaitAsync(Limit);
            Assert.False(loading.IsCompleted);
            Assert.False(context.NativeServices!.Registry.TryGet<IA>(out _));
            if (target is not null) Assert.False(target.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading.WaitAsync(Limit));
            if (target is not null) await target.WaitAsync().AsTask().WaitAsync(Limit);
        }
        Assert.False(Assert.IsType<PluginContext>(h[0].Context).NativeServices!.Registry.TryGet<IA>(out _));
        await h.Host.LoadAsync(h.Rows[0].Id, default).AsTask().WaitAsync(Limit);
        Assert.Equal(PluginState.Active, h.Status(0)); Assert.Equal(2, h[0].Starts);
    }

    [Fact]
    public async Task StrictCancellationDrainsCallbacksWithoutBlockingIndependentAdmission()
    {
        await using var h = new Harness(("transition.d", true), ("transition.d", true));
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var activated = Signal(); var callbackEntered = Signal();
        h[0].Start = async (context, token) =>
        {
            context.Effect(token.Register(() =>
            {
                callbackEntered.TrySetResult(); Assert.True(release.Wait(Limit));
            }));
            activated.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var loading = h.Host.LoadAsync(h.Rows[0].Id, cancellation.Token).AsTask();
        try
        {
            await activated.Task.WaitAsync(Limit);
            cancellation.Cancel();
            await callbackEntered.Task.WaitAsync(Limit);
            await h.Host.LoadAsync(h.Rows[1].Id, default).AsTask().WaitAsync(Limit);
            Assert.False(loading.IsCompleted); Assert.Equal(PluginState.Active, h.Status(1));
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Host.LoadAsync(h.Rows[0].Id, default).AsTask());
        }
        finally
        {
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading.WaitAsync(Limit));
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Harness : IAsyncDisposable
    {
        internal readonly PluginRow[] Rows;
        internal readonly PluginHost Host;
        internal Harness(params (string Plugin, bool Disabled)[] rows) : this(10000, rows) { }
        internal Harness(int timeout, params (string Plugin, bool Disabled)[] rows) : this(timeout, 32, rows) { }
        internal Harness(int timeout, int attempts, params (string Plugin, bool Disabled)[] rows)
        {
            Rows = rows.Select(row => new PluginRow { Id = Guid.NewGuid().ToString("N"), Plugin = row.Plugin, Disabled = row.Disabled, Source = "test", Lifecycle = new() { TeardownTimeoutMs = timeout, ReconcileAttemptLimit = attempts } }).ToArray();
            foreach (var row in Rows) States[row.Id] = new();
            Host = new(Rows, PluginCatalog.ScanAssemblies([typeof(PluginTransitionTests).Assembly]), NullLoggerFactory.Instance);
        }
        internal Observation this[int index] => States[Rows[index].Id];
        internal PluginState Status(int index) => Host.Instances.Single(info => info.RowId == Rows[index].Id).State;
        internal IPluginTransition Request(int index, bool enabled, bool restart = false) => Host.RequestState(Rows[index].Id, enabled, restart);
        public async ValueTask DisposeAsync()
        {
            try { await Host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
            finally { foreach (var row in Rows) States.TryRemove(row.Id, out _); }
        }
    }
}
