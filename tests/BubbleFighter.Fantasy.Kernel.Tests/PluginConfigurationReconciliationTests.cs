using System.Collections;
using System.Collections.Concurrent;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Exercises configuration admission and settlement through real host lifecycle operations.</summary>
public sealed class PluginConfigurationReconciliationTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, string> Versions = new Dictionary<string, string>
    { ["BubbleFighter.Fantasy.Abstractions"] = typeof(IPlugin).Assembly.GetName().Version!.ToString() };

    /// <summary>Deferred profile values bind during real activation and are refreshed by configuration reconciliation.</summary>
    [Fact]
    public async Task EnablingAComposedRowAndReloadingBindsDeferredIntegerConfiguration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-compose-reconcile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "bundles"));
        Directory.CreateDirectory(Path.Combine(directory, "demo"));
        try
        {
            File.WriteAllText(Path.Combine(directory, "bundles", "a.yaml"),
                "- id: a\n  plugin: a\n  disabled: true\n  config:\n    value: '${TEST_LIFETIME:-600}'\n");
            File.WriteAllText(Path.Combine(directory, "demo", "profile.yaml"), "id: demo\nbundles: [a]\n");
            var patch = Path.Combine(directory, "enable.yaml");
            File.WriteAllText(patch, "- replace:\n    id: a\n    disabled: false\n");
            string? lifetime = null;
            var composer = new ProfileComposer(directory, key => key == "TEST_LIFETIME" ? lifetime : null);
            var probe = new Probe("a", 0);
            await using var harness = new Harness(composer.Compose("demo", []).ToArray(), probe);
            await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
            Assert.Equal(0, probe.Starts);
            var enabled = await harness.Host.ReconcileConfigurationAsync(composer.Compose("demo", [patch])).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginTransitionOutcome.Converged, enabled.Outcome);
            Assert.Equal(600, Assert.Single(probe.Observations).Value);
            lifetime = "480";
            var reloaded = await harness.Host.ReconcileConfigurationAsync(composer.Compose("demo", [patch])).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginTransitionOutcome.Converged, reloaded.Outcome);
            Assert.Equal(new[] { 600, 480 }, probe.Observations.Select(observation => observation.Value));
            Assert.Equal(1, probe.Stops);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>Provides the first independently configurable dependency consumed by the shared fixture.</summary>
    public interface ILeft
    {
        /// <summary>Gets the configuration value published by the first provider.</summary>
        int Value { get; }
    }

    /// <summary>Provides the second independently configurable dependency consumed by the shared fixture.</summary>
    public interface IRight
    {
        /// <summary>Gets the configuration value published by the second provider.</summary>
        int Value { get; }
    }

    /// <summary>Treats mapping and row order as immaterial and ignores candidate source labels.</summary>
    [Fact]
    public async Task EquivalentCandidatesDoNotAllocateARevisionOrRestartRows()
    {
        var a = new Probe("a", 1);
        var b = new Probe("b", 2);
        var rows = new[]
        {
            Row("a", 1) with { Config = new Dictionary<string, object?>
            { ["value"] = 1, ["nested"] = new Dictionary<string, object?> { ["left"] = 3, ["right"] = 4 }, ["values"] = new ArrayList { 5, 6 } } },
            Row("b", 2)
        };
        await using var harness = new Harness(rows, a, b);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        PluginRow[] reordered =
        [
            rows[1] with { Source = "candidate-source" },
            rows[0] with { Source = "candidate-source", Config = new Dictionary<string, object?>
            { ["values"] = new List<object?> { 5, 6 }, ["nested"] = new Dictionary<string, object?> { ["right"] = 4, ["left"] = 3 }, ["value"] = 1 } }
        ];

        var result = await ((IPluginHost)harness.Host).ReconcileConfigurationAsync(reordered).AsTask().WaitAsync(Limit);

        Assert.Equal(0, result.Revision);
        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
        Assert.Empty(result.ChangedRowIds);
        Assert.Equal(1, a.Starts);
        Assert.Equal(1, b.Starts);
        Assert.Equal(0, a.Stops);
        Assert.Equal(0, b.Stops);
        var changed = reordered.Select(row => row.Id == "a" ? WithValue(row, 10) : row).ToArray();
        var applied = await harness.Host.ReconcileConfigurationAsync(changed).AsTask().WaitAsync(Limit);
        Assert.Equal(1, applied.Revision);
        Assert.Equal("initial-test", a.Observations.Last().Row.Source);
        Assert.Equal(10, a.Observations.Last().Value);
    }

    /// <summary>Restarts the union of two provider scopes once while retaining an unrelated activation.</summary>
    [Fact]
    public async Task TwoChangedProvidersRestartTheirSharedConsumerOnlyOnce()
    {
        var a = new Probe("a", 1, provides: [typeof(ILeft)]);
        var b = new Probe("b", 2, provides: [typeof(IRight)]);
        var c = new Probe("c", 0, requires: [typeof(ILeft), typeof(IRight)]);
        var d = new Probe("d", 4);
        await using var harness = new Harness(a, b, c, d);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);

        var candidate = harness.Candidate(("a", 10), ("b", 20));
        var result = await harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit);

        Assert.Equal(1, result.Revision);
        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
        Assert.Equal(["a", "b", "c"], result.ChangedRowIds.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(2, a.Starts);
        Assert.Equal(2, b.Starts);
        Assert.Equal(2, c.Starts);
        Assert.Equal(1, d.Starts);
        Assert.Equal(1, a.Stops);
        Assert.Equal(1, b.Stops);
        Assert.Equal(1, c.Stops);
        Assert.Equal(0, d.Stops);
        Assert.Equal([(1, 2), (10, 20)], c.Dependencies.ToArray());
        Assert.All(harness.Probes.SelectMany(probe => probe.ReloadFlags), reload => Assert.False(reload));
        var replay = await harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit);
        Assert.Equal(result.Revision, replay.Revision);
        Assert.Empty(replay.ChangedRowIds);
        Assert.Equal(2, c.Starts);
    }

    /// <summary>Accepts configuration for a manually stopped row without changing its desired state.</summary>
    [Fact]
    public async Task ManuallyUnloadedRowsUseAcceptedConfigurationOnlyOnExplicitLoad()
    {
        var probe = new Probe("a", 1);
        await using var harness = new Harness(probe);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        await harness.Host.UnloadAsync("a", default).AsTask().WaitAsync(Limit);

        var result = await harness.Host.ReconcileConfigurationAsync(harness.Candidate(("a", 9))).AsTask().WaitAsync(Limit);

        Assert.Equal(1, result.Revision);
        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
        Assert.Empty(result.ChangedRowIds);
        Assert.Equal(1, probe.Starts);
        Assert.Equal(PluginState.Unloaded, Assert.Single(harness.Host.Instances).State);
        Assert.Equal([false], probe.ReloadFlags.ToArray());
        await harness.Host.LoadAsync("a", default).AsTask().WaitAsync(Limit);
        Assert.Equal([1, 9], probe.Observations.Select(item => item.Value));
        Assert.Equal([false, true], probe.ReloadFlags.ToArray());
    }

    /// <summary>Rejects malformed graphs and fixed-topology changes before admitting any part of the candidate.</summary>
    [Theory]
    [InlineData("plugin")]
    [InlineData("version")]
    [InlineData("entry-grant")]
    [InlineData("lifecycle")]
    [InlineData("lineage")]
    [InlineData("metadata-grant")]
    [InlineData("identity")]
    [InlineData("unknown-plugin")]
    [InlineData("duplicate")]
    [InlineData("unsupported")]
    [InlineData("cycle")]
    public async Task InvalidCandidatesLeaveEveryAdmittedEntryUntouched(string change)
    {
        var a = new Probe("a", 1);
        var b = new Probe("b", 2);
        await using var harness = new Harness(a, b);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var candidate = harness.Candidate(("a", 99));
        candidate[1] = change switch
        {
            "plugin" => candidate[1] with { Plugin = "other-plugin" },
            "version" => candidate[1] with { Version = ">=99.0.0" },
            "entry-grant" => candidate[1] with { Lifecycle = candidate[1].Lifecycle with { EntryReserve = 1 } },
            "lifecycle" => candidate[1] with { Lifecycle = candidate[1].Lifecycle with { EffectCapacity = 0 } },
            "lineage" => candidate[1] with { Lineage = [null!] },
            "metadata-grant" => candidate[1] with { Lifecycle = candidate[1].Lifecycle with { ServiceMetadataReserve = 1 } },
            "identity" => candidate[1] with { Id = "another-row" },
            "unknown-plugin" => candidate[1] with { Plugin = "missing-definition" },
            "duplicate" => candidate[0],
            "unsupported" => candidate[1] with { Config = new Dictionary<string, object?> { ["value"] = new StringBuilder("private-value") } },
            _ => candidate[1]
        };
        if (change == "cycle")
        {
            var cycle = new Dictionary<string, object?>();
            cycle["nested"] = cycle;
            candidate[1] = candidate[1] with { Config = cycle };
        }

        await Assert.ThrowsAsync<ConfigException>(() => harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit));

        Assert.Equal(1, a.Starts);
        Assert.Equal(1, b.Starts);
        Assert.Equal(0, a.Stops);
        Assert.Equal(0, b.Stops);
        Assert.All(harness.Host.Instances, instance => Assert.Equal(PluginState.Active, instance.State));
        var unchanged = await harness.Host.ReconcileConfigurationAsync(harness.Rows).AsTask().WaitAsync(Limit);
        Assert.Equal(0, unchanged.Revision);
        Assert.Empty(unchanged.ChangedRowIds);
        Assert.Equal(1, a.Observations.Last().Value);
        Assert.Equal(2, b.Observations.Last().Value);
    }

    /// <summary>Joins equivalent pending submissions, cancels only observers, and permits unrelated lifecycle work.</summary>
    [Fact]
    public async Task EquivalentPendingCandidatesShareSettlementWithoutSharingObserverCancellation()
    {
        var a = new Probe("a", 1);
        var d = new Probe("d", 4);
        await using var harness = new Harness(a, d);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var gate = harness.BlockActivation(a, 2);
        var candidate = harness.Candidate(("a", 2));
        var first = harness.Host.ReconcileConfigurationAsync(candidate).AsTask();
        await gate.Entered.Task.WaitAsync(Limit);
        var equivalent = candidate.Reverse().Select(row => row with { Source = "equivalent-source" }).ToArray();
        var joined = harness.Host.ReconcileConfigurationAsync(equivalent).AsTask();
        using var cancellation = new CancellationTokenSource();
        var observer = harness.Host.ReconcileConfigurationAsync(equivalent, cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer.WaitAsync(Limit));
        Assert.False(first.IsCompleted);
        Assert.False(joined.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Host.ReconcileConfigurationAsync(harness.Candidate(("a", 3))).AsTask().WaitAsync(Limit));
        await harness.Host.UnloadAsync("d", default).AsTask().WaitAsync(Limit);
        await harness.Host.LoadAsync("d", default).AsTask().WaitAsync(Limit);
        Assert.Equal(2, d.Starts);
        Assert.False(first.IsCompleted);
        gate.Release.TrySetResult();
        var settled = await first.WaitAsync(Limit);
        var replay = await joined.WaitAsync(Limit);

        Assert.Equal(1, settled.Revision);
        Assert.Equal(settled.Revision, replay.Revision);
        Assert.Equal(PluginTransitionOutcome.Converged, settled.Outcome);
        Assert.Equal(settled.Outcome, replay.Outcome);
        Assert.Equal(settled.ChangedRowIds, replay.ChangedRowIds);
        Assert.Equal([1, 2], a.Observations.Select(item => item.Value));
        Assert.All(a.ReloadFlags, reload => Assert.False(reload));
    }

    /// <summary>Rejects configuration admission while an already-running immediate operation owns the same scope.</summary>
    [Fact]
    public async Task PreexistingConflictingLifecycleWorkRejectsConfigurationBeforeAdmission()
    {
        var a = new Probe("a", 1);
        await using var harness = new Harness(a);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var gate = harness.BlockActivation(a, 1);
        var reloading = harness.Host.ReloadAsync("a", default).AsTask();
        await gate.Entered.Task.WaitAsync(Limit);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Host.ReconcileConfigurationAsync(harness.Candidate(("a", 2))).AsTask().WaitAsync(Limit));

        gate.Release.TrySetResult();
        await reloading.WaitAsync(Limit);
        var unchanged = await harness.Host.ReconcileConfigurationAsync(harness.Rows).AsTask().WaitAsync(Limit);
        Assert.Equal(0, unchanged.Revision);
        Assert.Equal([1, 1], a.Observations.Select(item => item.Value));
    }

    /// <summary>Host shutdown waits for the real blocked activation to return and closes its acquired execution.</summary>
    [Fact]
    public async Task ShutdownJoinsAnAcceptedConfigurationActivation()
    {
        var a = new Probe("a", 1);
        await using var harness = new Harness(a);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var gate = harness.BlockActivation(a, 2);
        var applying = harness.Host.ReconcileConfigurationAsync(harness.Candidate(("a", 2))).AsTask();
        await gate.Entered.Task.WaitAsync(Limit);

        var shutdown = harness.Host.ShutdownAsync(default).AsTask();
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref a.Closes));
        gate.Release.TrySetResult();
        var result = await applying.WaitAsync(Limit);
        await shutdown.WaitAsync(Limit);

        Assert.Equal(PluginTransitionOutcome.Superseded, result.Outcome);
        Assert.Equal(2, a.Starts);
        Assert.Equal(2, a.Stops);
        Assert.Equal(2, Volatile.Read(ref a.Closes));
    }

    /// <summary>A newer target on a later changed root supersedes the batch and fences the blocked provider's publication.</summary>
    [Fact]
    public async Task TargetingANonFirstRootSupersedesTheWholeConfigurationBatch()
    {
        var a = new Probe("a", 1, provides: [typeof(ILeft)]);
        var b = new Probe("b", 2, provides: [typeof(IRight)]);
        var c = new Probe("c", 0, requires: [typeof(ILeft), typeof(IRight)]);
        await using var harness = new Harness(a, b, c);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var gate = harness.BlockActivation(a, 10);
        var applying = harness.Host.ReconcileConfigurationAsync(harness.Candidate(("a", 10), ("b", 20))).AsTask();
        await gate.Entered.Task.WaitAsync(Limit);

        var stopped = harness.Host.RequestState("b", false);
        gate.Release.TrySetResult();
        var result = await applying.WaitAsync(Limit);
        var stopResult = await stopped.WaitAsync().AsTask().WaitAsync(Limit);

        Assert.Equal(PluginTransitionOutcome.Superseded, result.Outcome);
        Assert.Equal(PluginTransitionOutcome.Converged, stopResult.Outcome);
        Assert.Equal(PluginState.Unloaded, harness.Host.Instances.Single(instance => instance.RowId == "b").State);
        Assert.NotEmpty(a.ServiceErrors);
        Assert.All(a.ServiceErrors, error => Assert.IsAssignableFrom<InvalidOperationException>(error));
        Assert.DoesNotContain((10, 2), c.Dependencies);
        await harness.Host.WaitForBindingChangesAsync().WaitAsync(Limit);
        Assert.Equal(PluginState.Active, harness.Host.Instances.Single(instance => instance.RowId == "a").State);
        Assert.Equal(10, a.Observations.Last().Value);
        Assert.All(harness.Probes.SelectMany(probe => probe.ReloadFlags), reload => Assert.False(reload));
        var loadB = await harness.Host.RequestState("b", true).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.Converged, loadB.Outcome);
        Assert.Equal(10, a.Observations.Last().Value);
        Assert.Equal(20, b.Observations.Last().Value);
        Assert.Equal((10, 20), c.Dependencies.Last());
        Assert.DoesNotContain((10, 2), c.Dependencies);
        Assert.Equal([false, true], b.ReloadFlags.ToArray());
        Assert.Equal([false, true], c.ReloadFlags.ToArray());
    }

    /// <summary>Equivalent submissions after a partial failure must retry unapplied work rather than report a successful no-op.</summary>
    [Fact]
    public async Task EqualRetryAfterPartialFailureReexecutesUnappliedConfiguration()
    {
        var a = new Probe("a", 1);
        var b = new Probe("b", 2);
        await using var harness = new Harness(a, b);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var fail = true;
        b.BeforeActivate = (context, _) =>
        {
            if (context.Config.Bind<Options>().Value == 20 && Volatile.Read(ref fail))
                throw new InvalidOperationException("configured activation failure");
            return ValueTask.CompletedTask;
        };
        var candidate = harness.Candidate(("a", 10), ("b", 20));

        var first = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit));
        var repeated = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit));

        Assert.Contains("configured activation failure", first.ToString());
        Assert.Contains("configured activation failure", repeated.ToString());
        var failedStarts = b.Starts;
        Volatile.Write(ref fail, false);
        var settled = await harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit);
        Assert.Equal(1, settled.Revision);
        Assert.Equal(PluginTransitionOutcome.Converged, settled.Outcome);
        Assert.True(b.Starts > failedStarts);
        Assert.Equal(10, a.Observations.Last().Value);
        Assert.Equal(20, b.Observations.Last().Value);
        Assert.All(harness.Host.Instances, instance => Assert.Equal(PluginState.Active, instance.State));
        Assert.All(harness.Probes.SelectMany(probe => probe.ReloadFlags), reload => Assert.False(reload));
    }

    /// <summary>A candidate that removes an entry a surviving declaration overrides is rejected whole.</summary>
    [Fact]
    public async Task CandidateCannotRemoveAnEntryThatASurvivorOverrides()
    {
        var a = new Probe("a", 1);
        var b = new Probe("b", 0, overrides: ["a:Test.Namespace.Message"]);
        var rows = new[] { Row("a", 1), Row("b", 0) with { Disabled = true } };
        await using var harness = new Harness(rows, a, b);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);

        var error = await Assert.ThrowsAsync<ConfigException>(() =>
            harness.Host.ReconcileConfigurationAsync([rows[1]]).AsTask().WaitAsync(Limit));

        Assert.Contains("b", error.Message);
        Assert.Contains("a", error.Message);
        Assert.Equal(PluginState.Active, harness.Host.Instances.Single(instance => instance.RowId == "a").State);
        Assert.Equal(1, a.Starts);
        Assert.Equal(0, a.Stops);
        var kept = await harness.Host.ReconcileConfigurationAsync(harness.Rows).AsTask().WaitAsync(Limit);
        Assert.Equal(0, kept.Revision);
    }

    /// <summary>Enabling a resident row whose plugin is absent fails loud without poisoning convergence.</summary>
    [Fact]
    public async Task EnablingAnUnknownPluginRowIsRejectedWithoutPoisoningConvergence()
    {
        var a = new Probe("a", 1);
        var rows = new[]
        {
            Row("a", 1),
            Row("ghost", 0) with { Plugin = "missing-plugin", Disabled = true }
        };
        await using var harness = new Harness(rows, a);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);

        Assert.Throws<ConfigException>(() => harness.Host.RequestState("ghost", true));

        Assert.Equal(1, a.Starts);
        var applied = await harness.Host.ReconcileConfigurationAsync(harness.Candidate(("a", 9))).AsTask().WaitAsync(Limit);
        Assert.Equal(1, applied.Revision);
        Assert.Equal(PluginTransitionOutcome.Converged, applied.Outcome);
        Assert.Equal(2, a.Starts);
    }

    private static PluginRow Row(string id, int value) => new()
    { Id = id, Plugin = id, Source = "initial-test", Config = new Dictionary<string, object?> { ["value"] = value }, Lifecycle = new() { EffectCapacity = 16 } };

    private static PluginRow WithValue(PluginRow row, int value) =>
        row with { Config = new Dictionary<string, object?> { ["value"] = value } };

    private sealed record Left(int Value) : ILeft;
    private sealed record Right(int Value) : IRight;
    private sealed record Observation(PluginRow Row, int Value);

    private sealed class Gate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Retained teardown prevents configuration replacement even when the same accepted revision is replayed.</summary>
    [Fact]
    public async Task ConfigurationRetryCannotBypassIncompleteCleanup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = new Probe("a", 1);
        a.BeforeActivate = async (context, _) =>
        {
            if (context.Config.Bind<Options>().Value != 1) return;
            await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new AsyncCleanup(async () =>
            {
                entered.TrySetResult();
                await release.Task;
            })));
        };
        var row = Row("a", 1) with { Lifecycle = new() { TeardownTimeoutMs = 100 } };
        await using var harness = new Harness([row], a);
        try
        {
            await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
            var candidate = harness.Candidate(("a", 2));
            var applying = harness.Host.ReconcileConfigurationAsync(candidate).AsTask();
            await entered.Task.WaitAsync(Limit);
            await Assert.ThrowsAsync<InvalidOperationException>(() => applying.WaitAsync(Limit));
            Assert.Equal(PluginState.CleanupIncomplete, Assert.Single(harness.Host.Instances).State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit));
            Assert.Equal(1, a.Starts);
            Assert.Equal(0, Volatile.Read(ref a.Closes));
            release.TrySetResult();

            var recovered = await harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit);
            Assert.Equal(1, recovered.Revision);
            Assert.Equal(PluginTransitionOutcome.Converged, recovered.Outcome);
            Assert.Equal([1, 2], a.Observations.Select(item => item.Value));
            Assert.Equal(1, Volatile.Read(ref a.Closes));
            Assert.Equal(PluginState.Active, Assert.Single(harness.Host.Instances).State);
        }
        finally { release.TrySetResult(); }
    }

    private sealed class AsyncCleanup(Func<ValueTask> close) : IAsyncDisposable
    {
        /// <summary>Waits for the controlled fixture cleanup before the backend execution may close.</summary>
        public ValueTask DisposeAsync() => close();
    }

    private sealed class Probe(string id, int initialValue, Type[]? provides = null, Type[]? requires = null, string[]? overrides = null)
    {
        internal string Id { get; } = id;
        internal int InitialValue { get; } = initialValue;
        internal PluginAttribute Declaration { get; } = new(id)
        { Provides = provides ?? [], Requires = requires ?? [], Overrides = overrides ?? [] };
        internal ConcurrentQueue<Observation> Observations { get; } = new();
        internal ConcurrentQueue<(int Left, int Right)> Dependencies { get; } = new();
        internal ConcurrentQueue<bool> ReloadFlags { get; } = new();
        internal ConcurrentQueue<Exception> ServiceErrors { get; } = new();
        internal int Starts => Observations.Count;
        internal int Stops;
        internal int Closes;
        internal Func<IPluginContext, CancellationToken, ValueTask>? BeforeActivate;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly List<Gate> _gates = [];
        internal PluginHost Host { get; }
        internal PluginRow[] Rows { get; }
        internal Probe[] Probes { get; }
        internal Harness(params Probe[] probes) : this(probes.Select(probe => Row(probe.Id, probe.InitialValue)).ToArray(), probes) { }
        internal Harness(PluginRow[] rows, params Probe[] probes)
        {
            Rows = rows;
            Probes = probes;
            var definitions = probes.Select(probe => new PluginDescriptor(probe.Declaration, new Factory(probe), "configuration-test", "1.0.0", Versions));
            Host = new PluginHost(rows, PluginCatalog.FromDefinitions(definitions), NullLoggerFactory.Instance);
        }
        internal PluginRow[] Candidate(params (string Id, int Value)[] changes)
        {
            var values = changes.ToDictionary(change => change.Id, change => change.Value, StringComparer.Ordinal);
            return Rows.Select(row => values.TryGetValue(row.Id, out var value) ? WithValue(row, value) : row).ToArray();
        }
        internal Gate BlockActivation(Probe probe, int value)
        {
            var gate = new Gate();
            _gates.Add(gate);
            probe.BeforeActivate = async (context, _) =>
            {
                if (context.Config.Bind<Options>().Value != value) return;
                gate.Entered.TrySetResult();
                await gate.Release.Task;
            };
            return gate;
        }
        /// <summary>Releases fixture gates before joining host teardown, including when a test assertion fails.</summary>
        public async ValueTask DisposeAsync()
        {
            foreach (var gate in _gates) gate.Release.TrySetResult();
            await Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        }
    }

    private sealed class Options
    {
        /// <summary>Gets or sets the per-entry value observed by the execution fixture.</summary>
        public int Value { get; set; }
        /// <summary>Gets or sets nested values used to test mapping-order equivalence.</summary>
        public NestedOptions Nested { get; set; } = new();
        /// <summary>Gets or sets sequence values used to test equivalent copied collection representations.</summary>
        public IReadOnlyList<int> Values { get; set; } = [];
    }

    private sealed class NestedOptions
    {
        /// <summary>Gets or sets the first nested mapping value.</summary>
        public int Left { get; set; }
        /// <summary>Gets or sets the second nested mapping value.</summary>
        public int Right { get; set; }
    }

    private sealed class Factory(Probe probe) : IPluginExecutionFactory
    {
        /// <summary>Allows independent fixture definitions to acquire their executions concurrently.</summary>
        public string? CoordinationKey => null;
        /// <summary>Records module-refresh requests and creates a fresh owned execution.</summary>
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken)
        {
            probe.ReloadFlags.Enqueue(reload);
            return ValueTask.FromResult<IPluginExecution>(new Execution(probe));
        }
    }

    private sealed class Execution(Probe probe) : IPluginExecution
    {
        /// <summary>Returns the provider and consumer contracts declared by this fixture definition.</summary>
        public PluginAttribute Declaration => probe.Declaration;
        /// <summary>Reports the fixture version used when constructing its descriptor.</summary>
        public string Version => "1.0.0";
        /// <summary>Uses the abstraction version loaded by this test process.</summary>
        public IReadOnlyDictionary<string, string> ContractVersions => Versions;
        /// <summary>Identifies the fixture as a backend execution without a native plugin class.</summary>
        public Type? ImplementationType => null;
        /// <summary>Records configuration and exercises actual declared service access after any activation gate.</summary>
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            var value = context.Config.Bind<Options>().Value;
            probe.Observations.Enqueue(new(context.Row, value));
            context.Effect(() => Interlocked.Increment(ref probe.Stops));
            if (probe.BeforeActivate is { } before) await before(context, cancellationToken);
            try
            {
                if (Declaration.Provides.Contains(typeof(ILeft))) context.Effect(context.Services.Register<ILeft>(new Left(value)));
                if (Declaration.Provides.Contains(typeof(IRight))) context.Effect(context.Services.Register<IRight>(new Right(value)));
                if (Declaration.Requires.Contains(typeof(ILeft)) && Declaration.Requires.Contains(typeof(IRight)))
                    probe.Dependencies.Enqueue((context.Services.Get<ILeft>().Value, context.Services.Get<IRight>().Value));
            }
            catch (Exception error) { probe.ServiceErrors.Enqueue(error); throw; }
        }
        /// <summary>Counts execution cleanup so blocked activation tests distinguish settlement from real release.</summary>
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref probe.Closes);
            return ValueTask.CompletedTask;
        }
    }
}
