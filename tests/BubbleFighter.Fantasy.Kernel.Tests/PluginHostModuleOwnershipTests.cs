using System.Collections.Concurrent;
using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Checks physical module references across acquisition, native reload, cache ownership and retained cleanup.</summary>
public sealed class PluginHostModuleOwnershipTests
{
    private const string NativePluginId = "test.module-ownership-native";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, string> Versions = new Dictionary<string, string>
    { ["BubbleFighter.Fantasy.Abstractions"] = typeof(IPlugin).Assembly.GetName().Version!.ToString() };

    /// <summary>A sibling configuration restart retains the real old assembly revision after another entry refreshes the same native path.</summary>
    [Fact]
    public async Task NativeSiblingConfigurationRestartRetainsItsActualOldAssemblyRevision()
    {
        await using var harness = new NativeHarness();
        var initial = Assert.Single(harness.Host.ModuleOwnership.Snapshot());
        Assert.Equal(1, initial.CatalogPins);
        Assert.Equal("native-assembly", initial.Attribution);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var started = Assert.Single(harness.Host.ModuleOwnership.Snapshot());
        Assert.Equal(2, started.Activations);
        Assert.Equal(2, started.DefinitionCaches);
        Assert.Same(harness.InitialAssembly, harness.Logs.LastAssembly("first"));
        Assert.Same(harness.InitialAssembly, harness.Logs.LastAssembly("second"));

        await harness.Host.ReloadAsync("first", default).AsTask().WaitAsync(Limit);

        var refreshedAssembly = harness.Logs.LastAssembly("first");
        Assert.NotSame(harness.InitialAssembly, refreshedAssembly);
        var revisions = harness.Host.ModuleOwnership.Snapshot();
        Assert.Equal(2, revisions.Count);
        var oldRevision = revisions.Single(item => item.RevisionId == initial.RevisionId);
        var newRevision = revisions.Single(item => item.RevisionId != initial.RevisionId);
        Assert.Equal(oldRevision.ModuleId, newRevision.ModuleId);
        Assert.Equal(1, oldRevision.Activations);
        Assert.Equal(1, oldRevision.DefinitionCaches);
        Assert.Equal(1, newRevision.Activations);
        Assert.Equal(1, newRevision.DefinitionCaches);
        Assert.Equal(0, newRevision.CatalogPins);
        var candidate = harness.Rows.Select(row => row.Id == "second"
            ? row with { Config = new Dictionary<string, object?> { ["revision"] = 1 } } : row).ToArray();

        var changed = await harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit);

        Assert.Equal(PluginTransitionOutcome.Converged, changed.Outcome);
        Assert.Same(harness.InitialAssembly, harness.Logs.LastAssembly("second"));
        Assert.Same(refreshedAssembly, harness.Logs.LastAssembly("first"));
        var settled = harness.Host.ModuleOwnership.Snapshot();
        Assert.Equal(2, settled.Count);
        Assert.Equal(1, settled.Single(item => item.RevisionId == initial.RevisionId).Activations);
        Assert.Equal(1, settled.Single(item => item.RevisionId == initial.RevisionId).DefinitionCaches);
        Assert.Equal(1, settled.Single(item => item.RevisionId == newRevision.RevisionId).Activations);
        Assert.Equal(0, harness.Host.ModuleOwnership.PendingCount);
    }

    /// <summary>Repeated native refreshes prune unowned revisions while shutdown preserves caches until entries are removed.</summary>
    [Fact]
    public async Task NativeTurnoverStaysBoundedAndEntryDeletionReleasesOnlyItsCachePins()
    {
        await using var harness = new NativeHarness();
        var bootstrap = Assert.Single(harness.Host.ModuleOwnership.Snapshot()).RevisionId;
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var previous = bootstrap;
        for (var round = 0; round < 6; round++)
        {
            await harness.Host.ReloadAsync("first", default).AsTask().WaitAsync(Limit);
            var snapshots = harness.Host.ModuleOwnership.Snapshot();
            Assert.Equal(2, snapshots.Count);
            Assert.True(snapshots.Count <= harness.Host.ModuleOwnership.Capacity);
            var current = snapshots.Single(item => item.CatalogPins == 0).RevisionId;
            Assert.NotEqual(previous, current);
            if (previous != bootstrap) Assert.DoesNotContain(snapshots, item => item.RevisionId == previous);
            previous = current;
            Assert.Equal(2, snapshots.Sum(item => item.Activations));
            Assert.Equal(2, snapshots.Sum(item => item.DefinitionCaches));
        }

        await harness.Host.UnloadAsync("first", default).AsTask().WaitAsync(Limit);
        Assert.Equal(1, harness.Host.ModuleOwnership.Snapshot().Sum(item => item.Activations));
        Assert.Equal(2, harness.Host.ModuleOwnership.Snapshot().Sum(item => item.DefinitionCaches));
        await harness.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        var stopped = harness.Host.ModuleOwnership.Snapshot();
        Assert.Equal(0, stopped.Sum(item => item.Activations));
        Assert.Equal(2, stopped.Sum(item => item.DefinitionCaches));
        Assert.Equal(1, stopped.Sum(item => item.CatalogPins));

        var removed = await harness.Host.ReconcileConfigurationAsync([]).AsTask().WaitAsync(Limit);

        Assert.Equal(PluginTransitionOutcome.Converged, removed.Outcome);
        Assert.Empty(harness.Host.Instances);
        var remaining = Assert.Single(harness.Host.ModuleOwnership.Snapshot());
        Assert.Equal(bootstrap, remaining.RevisionId);
        Assert.Equal(1, remaining.CatalogPins);
        Assert.Equal(0, remaining.Activations);
        Assert.Equal(0, remaining.DefinitionCaches);
        Assert.Equal(0, remaining.RetainedFailures);
    }

    /// <summary>A shared or missing scheduling key cannot merge unrelated opaque backend code references.</summary>
    [Theory]
    [InlineData("shared-backend-key")]
    [InlineData(null)]
    public async Task OpaqueBackendsStayDistinctRegardlessOfCoordinationKey(string? key)
    {
        var first = new BackendProbe("opaque.first", key);
        var second = new BackendProbe("opaque.second", key);
        await using var harness = new BackendHarness([Row("first", first.Id), Row("second", second.Id)], first, second);

        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);

        var firstRevision = Assert.IsType<long>(first.Last.ModuleOwnership!.RevisionId);
        var secondRevision = Assert.IsType<long>(second.Last.ModuleOwnership!.RevisionId);
        Assert.NotEqual(firstRevision, secondRevision);
        var active = harness.Host.ModuleOwnership.Snapshot().Where(item => item.Activations != 0).ToArray();
        Assert.Equal(2, active.Length);
        Assert.All(active, item =>
        {
            Assert.Equal(1, item.Activations);
            Assert.Equal(1, item.DefinitionCaches);
            Assert.NotEqual("native-assembly", item.Attribution);
        });
        Assert.Contains(active, item => item.RevisionId == firstRevision);
        Assert.Contains(active, item => item.RevisionId == secondRevision);
        await harness.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        Assert.Equal(0, harness.Host.ModuleOwnership.Snapshot().Sum(item => item.Activations));
        Assert.Equal(2, harness.Host.ModuleOwnership.Snapshot().Sum(item => item.DefinitionCaches));
        Assert.Equal(2, harness.Host.ModuleOwnership.Snapshot().Sum(item => item.CatalogPins));
        Assert.True(first.Last.ModuleOwnership!.IsReleased);
        Assert.True(second.Last.ModuleOwnership!.IsReleased);
    }

    /// <summary>Shutdown retains a pending ticket until a late backend return and its actual disposal both settle.</summary>
    [Fact]
    public async Task PendingAcquisitionSurvivesSupersessionUntilLateExecutionCleanupCompletes()
    {
        var probe = new BackendProbe("opaque.pending", null);
        var acquire = new Gate();
        var close = new Gate();
        probe.BeforeAcquire = async _ => { acquire.Entered.TrySetResult(); await acquire.Release.Task; };
        probe.BeforeClose = async () => { close.Entered.TrySetResult(); await close.Release.Task; };
        await using var harness = new BackendHarness([Row("pending", probe.Id) with { Disabled = true }], probe);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var request = harness.Host.RequestState("pending", true);
        try
        {
            await acquire.Entered.Task.WaitAsync(Limit);
            Assert.Equal(1, harness.Host.ModuleOwnership.PendingCount);
            Assert.Equal(0, harness.Host.ModuleOwnership.Snapshot().Sum(item => item.Activations));
            var shutdown = harness.Host.ShutdownAsync(default).AsTask();
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(1, harness.Host.ModuleOwnership.PendingCount);
            acquire.Release.TrySetResult();
            await close.Entered.Task.WaitAsync(Limit);
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(1, harness.Host.ModuleOwnership.PendingCount);
            Assert.Equal(0, Volatile.Read(ref probe.Closed));
            close.Release.TrySetResult();

            await shutdown.WaitAsync(Limit);
            var result = await request.WaitAsync().AsTask().WaitAsync(Limit);

            Assert.Equal(PluginTransitionOutcome.Superseded, result.Outcome);
            Assert.Equal(0, harness.Host.ModuleOwnership.PendingCount);
            Assert.Equal(0, harness.Host.ModuleOwnership.PendingRetainedCount);
            Assert.Equal(0, harness.Host.ModuleOwnership.Snapshot().Sum(item => item.Activations));
            Assert.Equal(0, harness.Host.ModuleOwnership.Snapshot().Sum(item => item.DefinitionCaches));
            Assert.Equal(1, Volatile.Read(ref probe.Closed));
        }
        finally
        {
            acquire.Release.TrySetResult();
            close.Release.TrySetResult();
        }
    }

    /// <summary>Repeated cleanup failures retain one activation and one failure marker without duplicating the owner.</summary>
    [Fact]
    public async Task FailedCleanupRetainsOneActivationOwnerAcrossRepeatedUnloadAndShutdown()
    {
        var probe = new BackendProbe("opaque.retained", "retained-key");
        await using var harness = new BackendHarness([Row("retained", probe.Id)], probe);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var ticket = probe.Last.ModuleOwnership!;
        var revision = Assert.IsType<long>(ticket.RevisionId);
        probe.BeforeClose = () => throw new InvalidOperationException("backend cleanup remains unknown");
        harness.ExpectShutdownFailure = true;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Host.UnloadAsync("retained", default).AsTask().WaitAsync(Limit));
            var retained = Assert.Single(harness.Host.ModuleOwnership.Snapshot(), item => item.RevisionId == revision);
            Assert.Equal(1, retained.Activations);
            Assert.Equal(1, retained.RetainedFailures);
            Assert.Equal(1, retained.DefinitionCaches);
            Assert.False(ticket.IsReleased);
            Assert.Equal(PluginState.CleanupFailed, Assert.Single(harness.Host.Instances).State);
            Assert.Equal(0, harness.Host.ModuleOwnership.PendingCount);
        }
        await Assert.ThrowsAnyAsync<Exception>(() => harness.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit));
        var afterShutdown = harness.Host.ModuleOwnership.Snapshot().Single(item => item.RevisionId == revision);
        Assert.Equal(1, afterShutdown.Activations);
        Assert.Equal(1, afterShutdown.RetainedFailures);
        Assert.False(ticket.IsReleased);
    }

    /// <summary>A failed replacement with proven cleanup leaves the previous committed cache as the only entry definition pin.</summary>
    [Fact]
    public async Task CleanActivationRollbackPreservesThePreviouslyCommittedDefinitionCache()
    {
        var probe = new BackendProbe("opaque.rollback", null);
        var row = Row("rollback", probe.Id);
        await using var harness = new BackendHarness([row], probe);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var previous = probe.Last;
        var previousRevision = Assert.IsType<long>(previous.ModuleOwnership!.RevisionId);
        probe.BeforeActivate = (_, _) => throw new InvalidOperationException("replacement activation rejected");

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Host.ReconcileConfigurationAsync(
            [row with { Config = new Dictionary<string, object?> { ["revision"] = 1 } }]).AsTask().WaitAsync(Limit));

        var failed = probe.Last;
        Assert.NotSame(previous, failed);
        Assert.True(previous.ModuleOwnership!.IsReleased);
        Assert.True(failed.ModuleOwnership!.IsReleased);
        Assert.Equal(PluginState.Failed, Assert.Single(harness.Host.Instances).State);
        var snapshots = harness.Host.ModuleOwnership.Snapshot();
        Assert.Equal(0, snapshots.Sum(item => item.Activations));
        Assert.Equal(0, snapshots.Sum(item => item.RetainedFailures));
        Assert.Equal(1, snapshots.Sum(item => item.DefinitionCaches));
        Assert.Equal(1, snapshots.Single(item => item.RevisionId == previousRevision).DefinitionCaches);
        Assert.Equal(0, harness.Host.ModuleOwnership.PendingCount);
    }

    private static PluginRow Row(string id, string plugin) => new()
    {
        Id = id, Plugin = plugin, Source = "module-ownership-test",
        Config = new Dictionary<string, object?>(), Lifecycle = new() { EffectCapacity = 16, TeardownTimeoutMs = 10000 }
    };

    private sealed class NativeHarness : IAsyncDisposable
    {
        internal PluginRow[] Rows { get; } = [Row("first", NativePluginId), Row("second", NativePluginId)];
        internal AssemblyLoggerFactory Logs { get; } = new();
        internal Assembly InitialAssembly { get; }
        internal PluginHost Host { get; }
        internal NativeHarness()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "module-ownership", "ModuleOwnershipFixture.dll");
            Assert.True(File.Exists(path), $"Build-only native fixture is missing: {path}");
            var descriptor = PluginCatalog.LoadDescriptor(path, Path.GetDirectoryName(path)!);
            InitialAssembly = descriptor.Type!.Assembly;
            Host = new PluginHost(Rows, PluginCatalog.FromDefinitions([descriptor]), Logs);
        }
        /// <summary>Joins actual host cleanup without claiming native assembly reclamation.</summary>
        public async ValueTask DisposeAsync() => await Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
    }

    private sealed class BackendHarness : IAsyncDisposable
    {
        internal PluginHost Host { get; }
        internal bool ExpectShutdownFailure;
        internal BackendHarness(PluginRow[] rows, params BackendProbe[] probes)
        {
            var definitions = probes.Select(probe => new PluginDescriptor(probe.Declaration, probe, "ownership-test", "1.0.0", Versions));
            Host = new PluginHost(rows, PluginCatalog.FromDefinitions(definitions), NullLoggerFactory.Instance);
        }
        /// <summary>Requires clean shutdown unless the test deliberately retains a failed backend.</summary>
        public async ValueTask DisposeAsync()
        {
            if (ExpectShutdownFailure)
                await Assert.ThrowsAnyAsync<Exception>(() => Host.ShutdownAsync(default).AsTask().WaitAsync(Limit));
            else await Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        }
    }

    private sealed class Gate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BackendProbe(string id, string? key) : IPluginExecutionFactory
    {
        internal string Id { get; } = id;
        internal PluginAttribute Declaration { get; } = new(id);
        internal ConcurrentQueue<PluginContext> Contexts { get; } = new();
        internal PluginContext Last => Contexts.Last();
        internal Func<CancellationToken, ValueTask>? BeforeAcquire;
        internal Func<PluginContext, CancellationToken, ValueTask>? BeforeActivate;
        internal Func<ValueTask>? BeforeClose;
        internal int Closed;
        /// <summary>Supplies a scheduling key without asserting any native module identity.</summary>
        public string? CoordinationKey => key;
        /// <summary>Returns a controllable opaque execution, including late returns after cancellation.</summary>
        public async ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken)
        {
            if (BeforeAcquire is { } acquire) await acquire(cancellationToken);
            return new BackendExecution(this);
        }
    }

    private sealed class BackendExecution(BackendProbe probe) : IPluginExecution
    {
        /// <summary>Exposes the probe's contract-free backend declaration.</summary>
        public PluginAttribute Declaration => probe.Declaration;
        /// <summary>Reports a stable release while physical opaque executions remain distinguishable.</summary>
        public string Version => "1.0.0";
        /// <summary>References the shared abstraction currently loaded by the test host.</summary>
        public IReadOnlyDictionary<string, string> ContractVersions => Versions;
        /// <summary>Does not claim a native assembly for this external backend.</summary>
        public Type? ImplementationType => null;
        /// <summary>Captures the actual context ticket before an optional controlled activation failure.</summary>
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            var actual = (PluginContext)context;
            probe.Contexts.Enqueue(actual);
            if (probe.BeforeActivate is { } activate) await activate(actual, cancellationToken);
        }
        /// <summary>Reports disposal only after the controlled backend cleanup has completed.</summary>
        public async ValueTask DisposeAsync()
        {
            if (probe.BeforeClose is { } close) await close();
            Interlocked.Increment(ref probe.Closed);
        }
    }

    private sealed class AssemblyLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<(string Row, Assembly Assembly)> _observations = new();
        internal Assembly LastAssembly(string row) => _observations.Last(item => item.Row == row).Assembly;
        /// <summary>Captures only the fixture's structured actual-assembly observations.</summary>
        public ILogger CreateLogger(string categoryName) => new AssemblyLogger(_observations);
        /// <summary>Requires no providers because fixture observations stay local to this host.</summary>
        public void AddProvider(ILoggerProvider provider) { }
        /// <summary>Releases no external resources.</summary>
        public void Dispose() { }
    }

    private sealed class AssemblyLogger(ConcurrentQueue<(string Row, Assembly Assembly)> observations) : ILogger
    {
        /// <summary>Does not retain logger scopes for this fixture.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>Accepts fixture information logs so assembly identity is observable.</summary>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <summary>Preserves the assembly object itself rather than comparing a repeated assembly name.</summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            var entries = values.ToArray();
            if (entries.FirstOrDefault(item => item.Key == "FixtureRow").Value is string row &&
                entries.FirstOrDefault(item => item.Key == "FixtureAssembly").Value is Assembly assembly)
                observations.Enqueue((row, assembly));
        }
    }
}
