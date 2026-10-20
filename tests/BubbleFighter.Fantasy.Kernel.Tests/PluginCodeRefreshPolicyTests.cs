using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Checks code-refresh admission independently of activation targets and actual code lifetime capability.</summary>
public sealed class PluginCodeRefreshPolicyTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, string> Versions = new Dictionary<string, string>
    { ["BubbleFighter.Fantasy.Abstractions"] = typeof(IPlugin).Assembly.GetName().Version!.ToString() };

    /// <summary>Provides the real dependency used to verify that rejected refreshes leave consumer references usable.</summary>
    public interface IValue
    {
        /// <summary>Gets the value published by the current provider activation.</summary>
        int Number { get; }
    }

    /// <summary>Provides the independently published prerequisite that changes queued refresh reachability.</summary>
    public interface IExtra
    {
        /// <summary>Gets the value supplied by the live conditional provider.</summary>
        int Number { get; }
    }

    /// <summary>A blocked root refresh cannot tear down its execution, its cache or any real dependent.</summary>
    [Fact]
    public async Task BlockedTargetReloadLeavesContextsGenerationsCachesAndValuesIntact()
    {
        var provider = new Probe("provider", provides: true);
        var reader = new Probe("reader", requires: true);
        await using var harness = new Harness(
            [Row(provider.Id, 11, PluginCodeRefreshPolicy.RestartRequired), Row(reader.Id)], provider, reader);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var providerContext = provider.Last;
        var readerContext = reader.Last;
        var generation = providerContext.NativeServices!.Activation.Generation;
        var seen = reader.Seen!;
        var ownership = harness.Host.ModuleOwnership.Snapshot().ToArray();
        var providerGate = harness.GateAcquisition(provider);
        var readerGate = harness.GateAcquisition(reader);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Host.ReloadAsync(provider.Id, default).AsTask().WaitAsync(Limit));

        Assert.Contains(provider.Id, error.Message);
        Assert.Same(providerContext, provider.Last);
        Assert.Same(readerContext, reader.Last);
        Assert.Equal(generation, provider.Last.NativeServices!.Activation.Generation);
        Assert.Same(seen, reader.Seen);
        Assert.Equal(11, reader.Seen!.Number);
        Assert.Equal(ownership, harness.Host.ModuleOwnership.Snapshot().ToArray());
        AssertUnchanged(provider);
        AssertUnchanged(reader);
        Assert.False(providerGate.Entered.Task.IsCompleted);
        Assert.False(readerGate.Entered.Task.IsCompleted);
        Assert.All(harness.Host.Instances, item => Assert.Equal(PluginState.Active, item.State));
        Assert.Equal(0, harness.Host.PendingIntentCount);
    }

    /// <summary>An allowed provider cannot refresh a restart-required dependent through strict cascade reload.</summary>
    [Fact]
    public async Task AllowedRootWithRestartRequiredDependentRejectsTheWholeReloadBeforeTeardown()
    {
        var provider = new Probe("provider", provides: true);
        var reader = new Probe("reader", requires: true);
        await using var harness = new Harness(
            [Row(provider.Id, 17), Row(reader.Id, policy: PluginCodeRefreshPolicy.RestartRequired)], provider, reader);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var originalProvider = provider.Last;
        var originalReader = reader.Last;
        var originalValue = reader.Seen;
        var ownership = harness.Host.ModuleOwnership.Snapshot().ToArray();
        harness.GateAcquisition(provider);
        harness.GateAcquisition(reader);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Host.ReloadAsync(provider.Id, default).AsTask().WaitAsync(Limit));

        Assert.Contains(reader.Id, error.Message);
        Assert.Same(originalProvider, provider.Last);
        Assert.Same(originalReader, reader.Last);
        Assert.Same(originalValue, reader.Seen);
        Assert.Equal(17, reader.Seen!.Number);
        Assert.Equal(ownership, harness.Host.ModuleOwnership.Snapshot().ToArray());
        AssertUnchanged(provider);
        AssertUnchanged(reader);
        Assert.All(harness.Host.Instances, item => Assert.Equal(PluginState.Active, item.State));
    }

    /// <summary>Rejected inactive load or enable requests neither acquire code nor leave an enabled desired target behind.</summary>
    [Theory]
    [InlineData("load")]
    [InlineData("enable")]
    [InlineData("restart")]
    public async Task ForbiddenInactiveRefreshLeavesNoDesiredSideEffectAndIndependentRequestsStillWork(string operation)
    {
        var blocked = new Probe("blocked");
        var independent = new Probe("independent");
        await using var harness = new Harness(
            [Row(blocked.Id, policy: PluginCodeRefreshPolicy.RestartRequired, disabled: true),
             Row(independent.Id, disabled: true)], blocked, independent);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var ownership = harness.Host.ModuleOwnership.Snapshot().ToArray();
        var gate = harness.GateAcquisition(blocked);

        if (operation == "load")
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Host.LoadAsync(blocked.Id, default).AsTask().WaitAsync(Limit));
        else
            await AssertRejectedRequest(harness.Host, blocked.Id, restart: operation == "restart");

        Assert.Empty(blocked.ReloadFlags);
        Assert.Empty(blocked.Contexts);
        Assert.Equal(0, Volatile.Read(ref blocked.Closes));
        Assert.False(gate.Entered.Task.IsCompleted);
        Assert.Equal(ownership, harness.Host.ModuleOwnership.Snapshot().ToArray());
        var replay = await harness.Host.ReconcileConfigurationAsync(harness.Rows).AsTask().WaitAsync(Limit);
        Assert.Equal(0, replay.Revision);
        Assert.Equal(PluginTransitionOutcome.Converged, replay.Outcome);
        Assert.Empty(replay.ChangedRowIds);
        Assert.Empty(blocked.ReloadFlags);
        Assert.Equal(PluginState.Unloaded, harness.Info(blocked.Id).State);
        var admitted = await harness.Host.RequestState(independent.Id, true).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.Converged, admitted.Outcome);
        Assert.Equal(PluginState.Active, harness.Info(independent.Id).State);
        Assert.Equal([true], independent.ReloadFlags.ToArray());
        Assert.Empty(blocked.ReloadFlags);
        Assert.Equal(0, harness.Host.PendingIntentCount);
    }

    /// <summary>A failed restart request keeps the current desired activation while later independent targets can still converge.</summary>
    [Fact]
    public async Task ForbiddenActiveRestartRequestDoesNotDisturbTheAcceptedLiveTarget()
    {
        var blocked = new Probe("blocked");
        var independent = new Probe("independent");
        await using var harness = new Harness(
            [Row(blocked.Id, 21, PluginCodeRefreshPolicy.RestartRequired), Row(independent.Id, disabled: true)], blocked, independent);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var context = blocked.Last;
        var ownership = harness.Host.ModuleOwnership.Snapshot().ToArray();
        harness.GateAcquisition(blocked);

        await AssertRejectedRequest(harness.Host, blocked.Id, restart: true);

        var replay = await harness.Host.ReconcileConfigurationAsync(harness.Rows).AsTask().WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.Converged, replay.Outcome);
        Assert.Empty(replay.ChangedRowIds);
        Assert.Same(context, blocked.Last);
        Assert.False(context.IsClosing);
        Assert.False(context.IsSuperseded);
        Assert.Equal(ownership, harness.Host.ModuleOwnership.Snapshot().ToArray());
        AssertUnchanged(blocked);
        await harness.Host.RequestState(independent.Id, true).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(PluginState.Active, harness.Info(independent.Id).State);
        Assert.Equal(PluginState.Active, harness.Info(blocked.Id).State);
    }

    /// <summary>Dispatch rechecks changed service reachability and restores the pre-request desired target after rejection.</summary>
    [Fact]
    public async Task QueuedGraphChangeRejectionRestoresTheOriginalDisabledTarget() =>
        await AssertQueuedGraphChangeAsync(submitNewerDisable: false);

    /// <summary>A newer queued disable wins even when the superseded enable would now violate a dependent's policy.</summary>
    [Fact]
    public async Task NewerQueuedDisableIsNotOverwrittenByTheSupersededEnableTarget() =>
        await AssertQueuedGraphChangeAsync(submitNewerDisable: true);

    /// <summary>A forbidden newer enable must not supersede a safe disable already waiting for its module reservation.</summary>
    [Fact]
    public async Task RejectedNewerEnablePreservesAnExistingSafeQueuedDisable()
    {
        var blocked = new Probe("blocked", coordinationKey: "queued-policy-key");
        var blocker = new Probe("blocker", coordinationKey: "queued-policy-key");
        await using var harness = new Harness(
            [Row(blocked.Id, policy: PluginCodeRefreshPolicy.RestartRequired, disabled: true), Row(blocker.Id, disabled: true)],
            blocked, blocker);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var gate = harness.GateAcquisition(blocker);
        var running = harness.Host.RequestState(blocker.Id, true);
        await gate.Entered.Task.WaitAsync(Limit);
        var safeDisable = harness.Host.RequestState(blocked.Id, false);
        var safeCompletion = safeDisable.WaitAsync().AsTask();
        Assert.False(safeCompletion.IsCompleted);

        await AssertRejectedRequest(harness.Host, blocked.Id, restart: false);

        Assert.False(safeCompletion.IsCompleted);
        Assert.Empty(blocked.ReloadFlags);
        gate.Release.TrySetResult();
        await running.WaitAsync().AsTask().WaitAsync(Limit);
        var result = await safeCompletion.WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
        await harness.Host.ReconcileConfigurationAsync(harness.Rows).AsTask().WaitAsync(Limit);
        Assert.Empty(blocked.ReloadFlags);
        Assert.Equal(PluginState.Unloaded, harness.Info(blocked.Id).State);
        Assert.Equal(0, harness.Host.PendingIntentCount);
    }

    /// <summary>Healthy enable-only requests and explicit teardown remain legal when in-process code refresh is forbidden.</summary>
    [Fact]
    public async Task HealthyActiveEnableIsNoOpAndUnloadAndShutdownRemainAllowed()
    {
        var probe = new Probe("protected");
        await using var harness = new Harness([Row(probe.Id, policy: PluginCodeRefreshPolicy.RestartRequired)], probe);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var context = probe.Last;
        harness.GateAcquisition(probe);

        var enabled = await harness.Host.RequestState(probe.Id, true, restart: false).WaitAsync().AsTask().WaitAsync(Limit);

        Assert.Equal(PluginTransitionOutcome.Converged, enabled.Outcome);
        Assert.Same(context, probe.Last);
        AssertUnchanged(probe);
        await harness.Host.UnloadAsync(probe.Id, default).AsTask().WaitAsync(Limit);
        Assert.Equal(1, Volatile.Read(ref probe.Stops));
        Assert.Equal(1, Volatile.Read(ref probe.Closes));
        Assert.True(context.ModuleOwnership!.IsReleased);
        await harness.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        Assert.Equal(0, harness.Host.ModuleOwnership.Snapshot().Sum(entry => entry.Activations));
    }

    /// <summary>Configuration restarts install the current refresh policy while using the already selected backend code.</summary>
    [Fact]
    public async Task ConfigurationCanInstallAndRemoveThePolicyWithoutRequestingCodeRefresh()
    {
        var probe = new Probe("configurable");
        var initial = Row(probe.Id, 1);
        await using var harness = new Harness([initial], probe);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var protectedRow = initial with
        {
            Config = new Dictionary<string, object?> { ["value"] = 2 },
            Lifecycle = initial.Lifecycle with { CodeRefreshPolicy = PluginCodeRefreshPolicy.RestartRequired }
        };

        await harness.Host.ReconcileConfigurationAsync([protectedRow]).AsTask().WaitAsync(Limit);

        Assert.Equal([false, false], probe.ReloadFlags.ToArray());
        Assert.Equal(2, probe.Last.Config.Bind<Options>().Value);
        Assert.Equal(PluginCodeRefreshPolicy.RestartRequired, harness.Info(probe.Id).CodeRefreshPolicy);
        Assert.Equal(PluginCodeRefreshPolicy.RestartRequired, probe.Last.Row.Lifecycle.CodeRefreshPolicy);
        var protectedContext = probe.Last;
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Host.ReloadAsync(probe.Id, default).AsTask().WaitAsync(Limit));
        Assert.Same(protectedContext, probe.Last);
        Assert.Equal([false, false], probe.ReloadFlags.ToArray());
        var nextConfig = protectedRow with { Config = new Dictionary<string, object?> { ["value"] = 3 } };
        await harness.Host.ReconcileConfigurationAsync([nextConfig]).AsTask().WaitAsync(Limit);
        Assert.Equal(3, probe.Last.Config.Bind<Options>().Value);
        Assert.Equal([false, false, false], probe.ReloadFlags.ToArray());
        await harness.Host.ReconcileConfigurationAsync([nextConfig with
        { Lifecycle = nextConfig.Lifecycle with { CodeRefreshPolicy = PluginCodeRefreshPolicy.Allow } }]).AsTask().WaitAsync(Limit);
        Assert.Equal(PluginCodeRefreshPolicy.Allow, harness.Info(probe.Id).CodeRefreshPolicy);
        await harness.Host.ReloadAsync(probe.Id, default).AsTask().WaitAsync(Limit);
        Assert.Equal([false, false, false, false, true], probe.ReloadFlags.ToArray());
    }

    /// <summary>A real provider withdrawal and return automatically restore protected consumers without refreshing their code.</summary>
    [Fact]
    public async Task AutomaticBindingRecoveryReusesCodeForRestartRequiredConsumers()
    {
        var provider = new Probe("provider", provides: true);
        var reader = new Probe("reader", requires: true);
        await using var harness = new Harness(
            [Row(provider.Id, 31), Row(reader.Id, policy: PluginCodeRefreshPolicy.RestartRequired)], provider, reader);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var providerContext = provider.Last;
        var readerContext = reader.Last;

        provider.Registration!.Dispose();
        await harness.Host.WaitForBindingChangesAsync().WaitAsync(Limit);
        Assert.Equal(PluginState.Pending, harness.Info(reader.Id).State);
        var replacement = new Value(32);
        provider.Registration = provider.Last.Effect(provider.Last.Services.Register<IValue>(replacement));
        await harness.Host.WaitForBindingChangesAsync().WaitAsync(Limit);

        Assert.Equal(PluginState.Active, harness.Info(reader.Id).State);
        Assert.NotSame(readerContext, reader.Last);
        Assert.Same(providerContext, provider.Last);
        Assert.Same(replacement, reader.Seen);
        Assert.Equal(32, reader.Seen!.Number);
        Assert.Equal([false], provider.ReloadFlags.ToArray());
        Assert.Equal([false, false], reader.ReloadFlags.ToArray());
        Assert.Equal(PluginCodeRefreshPolicy.RestartRequired, harness.Info(reader.Id).CodeRefreshPolicy);
    }

    /// <summary>The default allow policy preserves explicit refresh rather than silently changing it into a cached restart.</summary>
    [Fact]
    public async Task DefaultAllowPreservesTheExistingExplicitRefreshBehavior()
    {
        Assert.Equal(PluginCodeRefreshPolicy.Allow, new PluginLifecycleOptions().CodeRefreshPolicy);
        var probe = new Probe("default");
        await using var harness = new Harness([Row(probe.Id)], probe);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var original = probe.Last;

        await harness.Host.ReloadAsync(probe.Id, default).AsTask().WaitAsync(Limit);

        Assert.Equal([false, true], probe.ReloadFlags.ToArray());
        Assert.NotSame(original, probe.Last);
        Assert.Equal(1, Volatile.Read(ref probe.Closes));
        Assert.Equal(PluginState.Active, harness.Info(probe.Id).State);
    }

    /// <summary>Native lifetime capability reflects the actual load context and remains a capability after activation shutdown.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeLifetimeFactsRemainIndependentOfRefreshPolicyAndReclamation(bool collectible)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "module-ownership", "ModuleOwnershipFixture.dll");
        Assert.True(File.Exists(path), $"Build-only native fixture is missing: {path}");
        PluginDescriptor descriptor;
        if (collectible)
        {
            var context = new PluginLoadContext(path, "policy-facts-" + Guid.NewGuid().ToString("N"), isCollectible: true);
            var assembly = context.LoadFromAssemblyPath(path);
            var type = assembly.GetType("BubbleFighter.Fantasy.TestFixtures.ModuleOwnership.NativeOwnershipPlugin", throwOnError: true)!;
            var declaration = type.GetCustomAttribute<PluginAttribute>()!;
            descriptor = new PluginDescriptor(declaration.Id, declaration.Description, type, Path.GetDirectoryName(path)!, path);
        }
        else descriptor = PluginCatalog.LoadDescriptor(path, Path.GetDirectoryName(path)!);
        var row = Row(descriptor.Id, policy: PluginCodeRefreshPolicy.RestartRequired) with { Config = new Dictionary<string, object?>() };
        var host = new PluginHost([row], PluginCatalog.FromDefinitions([descriptor]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(default).AsTask().WaitAsync(Limit);
            var expected = collectible ? PluginCodeLifetime.Collectible : PluginCodeLifetime.ProcessLifetime;
            var active = Assert.Single(host.Instances);
            Assert.Equal(PluginState.Active, active.State);
            Assert.Equal(PluginCodeRefreshPolicy.RestartRequired, active.CodeRefreshPolicy);
            Assert.Equal(expected, active.CodeLifetime);
            Assert.Equal(!collectible, active.RestartRequiredForReclamation);
            Assert.Equal(expected, Assert.Single(host.ModuleOwnership.Snapshot()).Lifetime);
            await host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
            Assert.Equal(expected, Assert.Single(host.Instances).CodeLifetime);
            Assert.Equal(PluginState.Unloaded, Assert.Single(host.Instances).State);
            Assert.Equal(1, Assert.Single(host.ModuleOwnership.Snapshot()).CatalogPins);
            Assert.Equal(1, Assert.Single(host.ModuleOwnership.Snapshot()).DefinitionCaches);
            Assert.Equal(collectible, AssemblyLoadContext.GetLoadContext(descriptor.Type!.Assembly)!.IsCollectible);
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
    }

    /// <summary>A native-looking scheduling key and a restrictive policy cannot invent lifetime capabilities for an opaque backend.</summary>
    [Fact]
    public async Task OpaqueLifetimeRemainsUnknownEvenWithANativeLookingKeyAndRestartPolicy()
    {
        var probe = new Probe("opaque", coordinationKey: "native:opaque-not-a-code-identity");
        await using var harness = new Harness([Row(probe.Id, policy: PluginCodeRefreshPolicy.RestartRequired)], probe);

        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);

        var info = harness.Info(probe.Id);
        Assert.Equal(PluginState.Active, info.State);
        Assert.Equal(PluginCodeRefreshPolicy.RestartRequired, info.CodeRefreshPolicy);
        Assert.Equal(PluginCodeLifetime.Unknown, info.CodeLifetime);
        Assert.False(info.RestartRequiredForReclamation);
        Assert.All(harness.Host.ModuleOwnership.Snapshot(), entry => Assert.Equal(PluginCodeLifetime.Unknown, entry.Lifetime));
    }

    private static async Task AssertQueuedGraphChangeAsync(bool submitNewerDisable)
    {
        var root = new Probe("root", provides: true, coordinationKey: "graph-change-key");
        var reader = new Probe("reader", requires: true, requiresExtra: true);
        var extra = new Probe("extra", conditionalExtra: true);
        var blocker = new Probe("blocker", coordinationKey: "graph-change-key");
        await using var harness = new Harness(
            [Row(root.Id, 41, disabled: true), Row(reader.Id, policy: PluginCodeRefreshPolicy.RestartRequired, disabled: true),
             Row(extra.Id), Row(blocker.Id, disabled: true)], root, reader, extra, blocker);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var extraContext = extra.Last;
        var waiting = await harness.Host.RequestState(reader.Id, true).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.WaitingForDependencies, waiting.Outcome);
        Assert.Empty(reader.ReloadFlags);
        var gate = harness.GateAcquisition(blocker);
        var running = harness.Host.RequestState(blocker.Id, true);
        await gate.Entered.Task.WaitAsync(Limit);

        var original = harness.Host.RequestState(root.Id, true);
        var originalCompletion = original.WaitAsync().AsTask();
        Assert.False(originalCompletion.IsCompleted);
        Assert.Empty(root.ReloadFlags);
        extra.Registration = extraContext.Effect(extraContext.Services.Register<IExtra>(new ExtraValue(42)));
        Assert.False(originalCompletion.IsCompleted);
        Task<PluginTransitionResult>? newerCompletion = null;
        if (submitNewerDisable)
        {
            newerCompletion = harness.Host.RequestState(root.Id, false).WaitAsync().AsTask();
            var superseded = await originalCompletion.WaitAsync(Limit);
            Assert.Equal(PluginTransitionOutcome.Superseded, superseded.Outcome);
            Assert.False(newerCompletion.IsCompleted);
        }
        gate.Release.TrySetResult();
        await running.WaitAsync().AsTask().WaitAsync(Limit);
        if (newerCompletion is not null)
        {
            var newer = await newerCompletion.WaitAsync(Limit);
            Assert.Equal(PluginTransitionOutcome.Converged, newer.Outcome);
        }
        else
        {
            var rejection = await Assert.ThrowsAsync<InvalidOperationException>(() => originalCompletion.WaitAsync(Limit));
            Assert.Contains(reader.Id, rejection.Message);
        }
        await harness.Host.WaitForBindingChangesAsync().WaitAsync(Limit);

        Assert.Empty(root.ReloadFlags);
        Assert.Empty(reader.ReloadFlags);
        Assert.Empty(root.Contexts);
        Assert.Empty(reader.Contexts);
        Assert.Equal(0, Volatile.Read(ref root.Closes));
        Assert.Equal(0, Volatile.Read(ref reader.Closes));
        Assert.Same(extraContext, extra.Last);
        Assert.Equal(PluginState.Unloaded, harness.Info(root.Id).State);
        var replay = await harness.Host.ReconcileConfigurationAsync(harness.Rows).AsTask().WaitAsync(Limit);
        Assert.Equal(0, replay.Revision);
        Assert.Equal(PluginTransitionOutcome.WaitingForDependencies, replay.Outcome);
        Assert.Empty(replay.ChangedRowIds);
        Assert.Empty(root.ReloadFlags);
        Assert.Empty(reader.ReloadFlags);
        Assert.Equal(PluginState.Unloaded, harness.Info(root.Id).State);
        Assert.Equal(PluginState.Pending, harness.Info(reader.Id).State);
        Assert.Equal(0, harness.Host.PendingIntentCount);
    }

    private static async Task AssertRejectedRequest(PluginHost host, string row, bool restart)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var receipt = host.RequestState(row, true, restart);
            await receipt.WaitAsync().AsTask().WaitAsync(Limit);
        });
    }

    private static void AssertUnchanged(Probe probe)
    {
        Assert.Single(probe.ReloadFlags);
        Assert.Single(probe.Contexts);
        Assert.Equal(0, Volatile.Read(ref probe.Closes));
        Assert.Equal(0, Volatile.Read(ref probe.Stops));
        Assert.False(probe.Last.IsClosing);
        Assert.False(probe.Last.IsSuperseded);
    }

    private static PluginRow Row(string id, int value = 0, PluginCodeRefreshPolicy policy = PluginCodeRefreshPolicy.Allow, bool disabled = false) => new()
    {
        Id = id, Plugin = id, Source = "code-refresh-policy-test", Disabled = disabled,
        Config = new Dictionary<string, object?> { ["value"] = value },
        Lifecycle = new() { EffectCapacity = 16, TeardownTimeoutMs = 10000, CodeRefreshPolicy = policy }
    };

    private sealed record Value(int Number) : IValue;
    private sealed record ExtraValue(int Number) : IExtra;
    private sealed class Options
    {
        /// <summary>Gets or sets the actual service value observed by dependent activations.</summary>
        public int Value { get; set; }
    }

    private sealed class Gate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly List<Gate> _gates = [];
        internal PluginHost Host { get; }
        internal PluginRow[] Rows { get; }
        internal PluginInstanceInfo Info(string id) => Host.Instances.Single(item => item.RowId == id);
        internal Harness(PluginRow[] rows, params Probe[] probes)
        {
            Rows = rows;
            var definitions = probes.Select(probe => new PluginDescriptor(probe.Declaration, probe, "refresh-policy-test", "1.0.0", Versions));
            Host = new PluginHost(rows, PluginCatalog.FromDefinitions(definitions), NullLoggerFactory.Instance);
        }
        internal Gate GateAcquisition(Probe probe)
        {
            var gate = new Gate();
            _gates.Add(gate);
            probe.AcquireGate = gate;
            return gate;
        }
        /// <summary>Releases test gates before requiring all real host ownership to shut down cleanly.</summary>
        public async ValueTask DisposeAsync()
        {
            foreach (var gate in _gates) gate.Release.TrySetResult();
            await Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        }
    }

    private sealed class Probe(string id, bool provides = false, bool requires = false, string? coordinationKey = null,
        bool requiresExtra = false, bool conditionalExtra = false) : IPluginExecutionFactory
    {
        internal string Id { get; } = id;
        internal PluginAttribute Declaration { get; } = new(id)
        {
            Provides = provides ? [typeof(IValue)] : [],
            Requires = requiresExtra
                ? requires ? [typeof(IValue), typeof(IExtra)] : [typeof(IExtra)]
                : requires ? [typeof(IValue)] : [],
            ConditionalProvides = conditionalExtra ? [typeof(IExtra)] : []
        };
        internal ConcurrentQueue<bool> ReloadFlags { get; } = new();
        internal ConcurrentQueue<PluginContext> Contexts { get; } = new();
        internal PluginContext Last => Contexts.Last();
        internal IValue? Seen;
        internal IExtra? SeenExtra;
        internal IDisposable? Registration;
        internal Gate? AcquireGate;
        internal int Closes;
        internal int Stops;
        /// <summary>Serializes each fixture backend without asserting a physical code identity.</summary>
        public string? CoordinationKey => coordinationKey ?? "policy:" + Id;
        /// <summary>Records every real acquisition and exposes a gate that forbidden operations must never enter.</summary>
        public async ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken)
        {
            ReloadFlags.Enqueue(reload);
            if (AcquireGate is { } gate)
            {
                gate.Entered.TrySetResult();
                await gate.Release.Task;
            }
            return new Execution(this);
        }
    }

    private sealed class Execution(Probe probe) : IPluginExecution
    {
        /// <summary>Declares the fixture's real provider or consumer dependency.</summary>
        public PluginAttribute Declaration => probe.Declaration;
        /// <summary>Reports a stable version so a policy rejection cannot be confused with version incompatibility.</summary>
        public string Version => "1.0.0";
        /// <summary>References the shared abstraction loaded by the test host.</summary>
        public IReadOnlyDictionary<string, string> ContractVersions => Versions;
        /// <summary>Leaves lifetime capability unknown for this opaque backend.</summary>
        public Type? ImplementationType => null;
        /// <summary>Owns a real service registration or captures the actual provider selected by the host.</summary>
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            var actual = (PluginContext)context;
            probe.Contexts.Enqueue(actual);
            var value = context.Config.Bind<Options>().Value;
            context.Effect(() => Interlocked.Increment(ref probe.Stops));
            if (Declaration.Provides.Contains(typeof(IValue)))
                probe.Registration = context.Effect(context.Services.Register<IValue>(new Value(value)));
            if (Declaration.Requires.Contains(typeof(IValue))) probe.Seen = context.Services.Get<IValue>();
            if (Declaration.Requires.Contains(typeof(IExtra))) probe.SeenExtra = context.Services.Get<IExtra>();
            return ValueTask.CompletedTask;
        }
        /// <summary>Counts backend disposal independently of effect unwinding.</summary>
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref probe.Closes);
            return ValueTask.CompletedTask;
        }
    }
}
