using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Exercises entry parentage and inherited service namespaces through actual hosted activations.</summary>
public sealed class PluginEntryTreeHostTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly string ValueContract = typeof(IValue).FullName!;
    private static readonly string SecondaryContract = typeof(ISecondary).FullName!;
    private static readonly IReadOnlyDictionary<string, string> Versions = new Dictionary<string, string>
    { ["BubbleFighter.Fantasy.Abstractions"] = typeof(IPlugin).Assembly.GetName().Version!.ToString() };

    /// <summary>Exposes a value published independently in each isolated parent group.</summary>
    public interface IValue
    {
        /// <summary>Gets the configured value of this namespace's provider.</summary>
        int Number { get; }
    }

    /// <summary>Supplies a second known contract for testing isolation-set order and remapping.</summary>
    public interface ISecondary { }

    /// <summary>Starts parents before children supplied earlier in the candidate and keeps identical contracts isolated.</summary>
    [Fact]
    public async Task ChildFirstRowsStartUnderCapturedIndependentParentNamespaces()
    {
        await using var harness = TwoGroups();
        var leftInput = Assert.IsType<List<string>>(harness.Rows.Single(row => row.Id == "left").IsolatedServices);
        leftInput.Clear();
        leftInput.Add("Unknown.EntryTree.Contract");

        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);

        Assert.All(harness.Host.Instances, instance => Assert.Equal(PluginState.Active, instance.State));
        Assert.Equal(11, Assert.Single(harness["left-reader"].Seen)!.Number);
        Assert.Equal(22, Assert.Single(harness["right-reader"].Seen)!.Number);
        Assert.NotSame(harness["left-reader"].Seen.Single(), harness["right-reader"].Seen.Single());
        Assert.Same(harness["left-provider"].Published.Single(), harness["left-reader"].Seen.Single());
        Assert.Same(harness["right-provider"].Published.Single(), harness["right-reader"].Seen.Single());
        var order = harness.ActivationOrder.ToArray();
        AssertBefore(order, "left", "left-provider");
        AssertBefore(order, "left", "left-reader");
        AssertBefore(order, "right", "right-provider");
        AssertBefore(order, "right", "right-reader");
        AssertBefore(order, "left-provider", "left-reader");
        AssertBefore(order, "right-provider", "right-reader");
        var accepted = harness["left"].Rows.Single();
        Assert.Equal(2, accepted.IsolatedServices.Count);
        Assert.Contains(ValueContract, accepted.IsolatedServices);
        Assert.Contains(SecondaryContract, accepted.IsolatedServices);
        if (accepted.IsolatedServices is IList<string> mutable)
        {
            Assert.True(mutable.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => mutable[0] = "changed");
            Assert.Throws<NotSupportedException>(() => mutable.Clear());
        }
        Assert.Equal(["shared-profile"], harness["left-reader"].Rows.Single().Lineage);
        Assert.Equal(["shared-profile"], harness["right-reader"].Rows.Single().Lineage);
    }

    /// <summary>Limits provider reloads and configuration changes to consumers in the provider's actual namespace.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALeftProviderChangeDoesNotRestartTheRightBranch(bool reload)
    {
        await using var harness = TwoGroups();
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var rightValue = harness["right-reader"].Seen.Single();

        if (reload) await harness.Host.ReloadAsync("left-provider", default).AsTask().WaitAsync(Limit);
        else
        {
            var candidate = harness.Rows.Select(row => row.Id == "left-provider" ? WithValue(row, 17) : row).ToArray();
            var result = await harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
            Assert.Equal(["left-provider", "left-reader"], result.ChangedRowIds.OrderBy(id => id, StringComparer.Ordinal));
            Assert.All(harness.Probes.SelectMany(probe => probe.ReloadFlags), flag => Assert.False(flag));
        }

        Assert.Equal(2, harness["left-provider"].Starts);
        Assert.Equal(2, harness["left-reader"].Starts);
        Assert.Equal(reload ? 11 : 17, harness["left-reader"].Seen.Last()!.Number);
        Assert.Equal(1, harness["right-provider"].Starts);
        Assert.Equal(1, harness["right-reader"].Starts);
        Assert.Equal(1, harness["left"].Starts);
        Assert.Equal(1, harness["right"].Starts);
        Assert.Equal(1, harness["independent"].Starts);
        Assert.Equal(0, harness["right-provider"].Stops);
        Assert.Equal(0, harness["right-reader"].Stops);
        Assert.Same(rightValue, harness["right-reader"].Seen.Single());
    }

    /// <summary>Suspends desired descendants when their parent stops even when no service dependency connects them.</summary>
    [Fact]
    public async Task ParentTargetsSuspendAndResumeDesiredDescendantsWithoutServiceEdges()
    {
        var probes = new[] { new Probe("parent"), new Probe("child"), new Probe("leaf"), new Probe("independent") };
        await using var harness = new Harness([Row("leaf", parent: "child"), Row("child", parent: "parent"), Row("parent"), Row("independent")], probes);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);

        var stopped = await harness.Host.RequestState("parent", false).WaitAsync().AsTask().WaitAsync(Limit);

        Assert.Equal(PluginTransitionOutcome.Converged, stopped.Outcome);
        Assert.Equal(PluginState.Unloaded, harness.State("parent"));
        Assert.Equal(PluginState.Pending, harness.State("child"));
        Assert.Equal(PluginState.Pending, harness.State("leaf"));
        var stillWaiting = await harness.Host.RequestState("leaf", true).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.WaitingForDependencies, stillWaiting.Outcome);
        Assert.Equal(1, harness["child"].Starts);
        Assert.Equal(1, harness["leaf"].Starts);
        var resumed = await harness.Host.RequestState("parent", true).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.Converged, resumed.Outcome);
        Assert.Equal(2, harness["parent"].Starts);
        Assert.Equal(2, harness["child"].Starts);
        Assert.Equal(2, harness["leaf"].Starts);
        Assert.Equal(1, harness["independent"].Starts);
        Assert.All(harness.Host.Instances, instance => Assert.Equal(PluginState.Active, instance.State));
    }

    /// <summary>Retains parent execution and effects until a child's asynchronous resource has really drained.</summary>
    [Fact]
    public async Task ParentCleanupWaitsForChildOwnedResourceWithoutAServiceEdge()
    {
        await using var harness = new Harness([Row("child", parent: "parent"), Row("parent")], new Probe("parent"), new Probe("child"));
        var gate = harness.BlockResourceCleanup("child");
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);

        var stopped = harness.Host.RequestState("parent", false).WaitAsync().AsTask();
        await gate.Entered.Task.WaitAsync(Limit);

        Assert.False(stopped.IsCompleted);
        Assert.Equal(0, Volatile.Read(ref harness["parent"].Stops));
        Assert.Equal(0, Volatile.Read(ref harness["parent"].Closes));
        Assert.Equal(0, Volatile.Read(ref harness["child"].Closes));
        gate.Release.TrySetResult();
        var result = await stopped.WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
        Assert.Equal(1, Volatile.Read(ref harness["parent"].Stops));
        Assert.Equal(1, Volatile.Read(ref harness["parent"].Closes));
        Assert.Equal(1, Volatile.Read(ref harness["child"].Closes));
        AssertBefore(harness.DisposalOrder.ToArray(), "child:resource", "parent:execution");
    }

    /// <summary>Reserves a retained provider's original namespace while retrying cleanup for its accepted move.</summary>
    [Fact]
    public async Task RetriedMoveReservesTheRetainedProvidersOriginalNamespaceBeforeAcquisition()
    {
        var provider = new Probe("provider", provides: true);
        var replacement = new Probe("replacement", provides: true);
        await using var harness = new Harness(
        [
            Row("left") with { IsolatedServices = [ValueContract] },
            Row("right") with { IsolatedServices = [ValueContract] },
            Row("provider", 11, "left") with { Lifecycle = new() { EffectCapacity = 16, TeardownTimeoutMs = 100 } },
            Row("replacement", 22, "left") with { Disabled = true }
        ], new Probe("left"), new Probe("right"), provider, replacement);
        var gate = harness.BlockResourceCleanup("provider");
        try
        {
            await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
            Assert.Empty(replacement.ReloadFlags);
            var candidate = harness.Rows.Select(row => row.Id == "provider" ? row with { ParentId = "right" } : row).ToArray();
            var first = harness.Host.ReconcileConfigurationAsync(candidate).AsTask();
            await gate.Entered.Task.WaitAsync(Limit);
            await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(Limit));
            Assert.Equal(PluginState.CleanupIncomplete, harness.State("provider"));
            Assert.Equal("left", provider.Rows.Single().ParentId);
            Assert.Equal(1, provider.Starts);
            Assert.Equal(0, Volatile.Read(ref provider.Closes));

            var retry = harness.Host.ReconcileConfigurationAsync(candidate).AsTask();
            Assert.False(retry.IsCompleted);
            async Task ObserveRetriedCleanupAsync()
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                while (harness.State("provider") != PluginState.Unloading)
                {
                    if (retry.IsCompleted)
                    {
                        await retry;
                        throw new InvalidOperationException("The retry settled before re-entering its retained cleanup.");
                    }
                    if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= Limit)
                        throw new TimeoutException("The retry did not enter retained cleanup before the observation deadline.");
                    await Task.Yield();
                }
            }
            await ObserveRetriedCleanupAsync().WaitAsync(Limit);
            Assert.False(retry.IsCompleted);
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    harness.Host.LoadAsync("replacement", default).AsTask().WaitAsync(Limit));
                Assert.Empty(replacement.ReloadFlags);
                Assert.Equal(0, replacement.Starts);
                Assert.Empty(replacement.Published);
            }
            finally { gate.Release.TrySetResult(); }

            PluginConfigurationResult settled;
            try { settled = await retry.WaitAsync(Limit); }
            catch (InvalidOperationException)
            {
                Assert.Equal(PluginState.CleanupIncomplete, harness.State("provider"));
                settled = await harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit);
            }
            Assert.Equal(1, settled.Revision);
            Assert.Equal(PluginTransitionOutcome.Converged, settled.Outcome);
            Assert.Equal(PluginState.Active, harness.State("provider"));
            Assert.Equal("right", provider.Rows.Last().ParentId);
            Assert.Equal(2, provider.Starts);
            Assert.Equal(1, Volatile.Read(ref provider.Closes));
            Assert.Empty(replacement.ReloadFlags);
        }
        finally { gate.Release.TrySetResult(); }
    }

    /// <summary>Reuses unchanged namespaces after equivalent candidates reorder rows, mappings, and isolation sets.</summary>
    [Fact]
    public async Task EquivalentTreeCandidatesKeepActivationsAndNamespacesIntact()
    {
        await using var harness = TwoGroups();
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var leftValue = harness["left-provider"].Published.Single();
        var candidate = harness.Rows.Reverse().Select(row => row with
        {
            IsolatedServices = row.IsolatedServices.Reverse().ToArray(),
            Config = new Dictionary<string, object?> { ["label"] = row.Id, ["value"] = row.Config!["value"] }
        }).ToArray();

        var result = await harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit);

        Assert.Equal(0, result.Revision);
        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
        Assert.Empty(result.ChangedRowIds);
        Assert.All(harness.Probes, probe => { Assert.Equal(1, probe.Starts); Assert.Equal(0, probe.Stops); });
        await harness.Host.ReloadAsync("left-reader", default).AsTask().WaitAsync(Limit);
        Assert.Equal(2, harness["left-reader"].Starts);
        Assert.Equal(1, harness["left-provider"].Starts);
        Assert.Equal(1, harness["right-reader"].Starts);
        Assert.Same(leftValue, harness["left-reader"].Seen.Last());
    }

    /// <summary>Moves an admitted subtree into another parent's namespace while retaining unaffected services and rows.</summary>
    [Fact]
    public async Task MovingASubtreeCapturesItsCandidateAndChangesOnlyItsInheritedNamespace()
    {
        var probes = new[]
        {
            new Probe("left"), new Probe("right"), new Probe("left-provider", provides: true),
            new Probe("right-provider", provides: true), new Probe("branch", observesSecondary: true),
            new Probe("child", requires: true), new Probe("leaf", requires: true), new Probe("independent")
        };
        await using var harness = new Harness(
        [
            Row("leaf", parent: "child"), Row("child", parent: "branch"), Row("branch", parent: "left"),
            Row("left-provider", 11, "left"), Row("right-provider", 22, "right"),
            Row("left") with { IsolatedServices = [ValueContract] }, Row("right") with { IsolatedServices = [ValueContract] },
            Row("independent")
        ], probes);
        var gate = harness.BlockResourceCleanup("child");
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        Assert.Equal(11, harness["child"].Seen.Single()!.Number);
        Assert.Equal(11, harness["leaf"].Seen.Single()!.Number);
        var isolation = new List<string> { SecondaryContract };
        var config = new Dictionary<string, object?> { ["value"] = 7, ["label"] = "moved" };
        var candidate = harness.Rows.Select(row => row.Id == "branch"
            ? row with { ParentId = "right", IsolatedServices = isolation, Config = config } : row).ToArray();
        var applying = harness.Host.ReconcileConfigurationAsync(candidate).AsTask();
        await gate.Entered.Task.WaitAsync(Limit);
        isolation[0] = "Unknown.EntryTree.Contract";
        config["value"] = 999;
        var branchIndex = Array.FindIndex(candidate, row => row.Id == "branch");
        candidate[branchIndex] = candidate[branchIndex] with { ParentId = "missing" };
        gate.Release.TrySetResult();

        var result = await applying.WaitAsync(Limit);

        Assert.Equal(1, result.Revision);
        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
        Assert.Equal(["branch", "child", "leaf"], result.ChangedRowIds.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(2, harness["branch"].Starts);
        Assert.Equal(2, harness["child"].Starts);
        Assert.Equal(2, harness["leaf"].Starts);
        Assert.Equal(1, harness["independent"].Starts);
        Assert.Equal(0, harness["independent"].Stops);
        Assert.Equal(1, harness["left-provider"].Starts);
        Assert.Equal(1, harness["right-provider"].Starts);
        Assert.Same(harness["right-provider"].Published.Single(), harness["child"].Seen.Last());
        Assert.Same(harness["right-provider"].Published.Single(), harness["leaf"].Seen.Last());
        var accepted = harness["branch"].Rows.Last();
        Assert.Equal("right", accepted.ParentId);
        Assert.Equal([SecondaryContract], accepted.IsolatedServices);
        Assert.Equal(7, accepted.Config!["value"]);
        if (accepted.IsolatedServices is IList<string> mutable)
            Assert.Throws<NotSupportedException>(() => mutable.Add(ValueContract));
        Assert.All(harness.Probes.SelectMany(probe => probe.ReloadFlags), flag => Assert.False(flag));
    }

    /// <summary>Rebinds descendants to an inherited provider when their parent removes an isolation boundary.</summary>
    [Fact]
    public async Task ChangingIsolationRebindsDescendantsWithoutRestartingTheInheritedProvider()
    {
        await using var harness = new Harness(
        [
            Row("child", parent: "group"), Row("group") with { IsolatedServices = [ValueContract] },
            Row("provider", 23), Row("independent")
        ], new Probe("group"), new Probe("child", optional: true), new Probe("provider", provides: true), new Probe("independent"));
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        Assert.Null(harness["child"].Seen.Single());
        var candidate = harness.Rows.Select(row => row.Id == "group" ? row with { IsolatedServices = [] } : row).ToArray();

        var result = await harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit);

        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
        Assert.Equal(["child", "group"], result.ChangedRowIds.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(2, harness["group"].Starts);
        Assert.Equal(2, harness["child"].Starts);
        Assert.Equal(1, harness["provider"].Starts);
        Assert.Equal(1, harness["independent"].Starts);
        Assert.Same(harness["provider"].Published.Single(), harness["child"].Seen.Last());
        var replay = await harness.Host.ReconcileConfigurationAsync(candidate.Reverse().ToArray()).AsTask().WaitAsync(Limit);
        Assert.Equal(result.Revision, replay.Revision);
        Assert.Empty(replay.ChangedRowIds);
        Assert.Equal(2, harness["child"].Starts);
    }

    /// <summary>Rejects broken parent graphs and unknown isolated contracts before stopping any admitted activation.</summary>
    [Theory]
    [InlineData("missing-parent")]
    [InlineData("self-parent")]
    [InlineData("cycle")]
    [InlineData("unknown-isolation")]
    public async Task InvalidTreeCandidatesFailBeforeTeardownOrAdmission(string change)
    {
        await using var harness = new Harness([Row("parent"), Row("child", parent: "parent")],
            new Probe("parent"), new Probe("child", optional: true));
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var candidate = new[] { WithValue(harness.Rows[0], 99), harness.Rows[1] };
        if (change == "cycle") candidate[0] = candidate[0] with { ParentId = "child" };
        else candidate[1] = change switch
        {
            "missing-parent" => candidate[1] with { ParentId = "missing" },
            "self-parent" => candidate[1] with { ParentId = "child" },
            _ => candidate[1] with { IsolatedServices = ["Unknown.EntryTree.Contract"] }
        };

        await Assert.ThrowsAsync<ConfigException>(() => harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit));

        Assert.All(harness.Probes, probe => { Assert.Equal(1, probe.Starts); Assert.Equal(0, probe.Stops); });
        Assert.All(harness.Host.Instances, instance => Assert.Equal(PluginState.Active, instance.State));
        Assert.Equal("parent", harness["child"].Rows.Single().ParentId);
        Assert.Equal(0, harness["parent"].Rows.Single().Config!["value"]);
        var unchanged = await harness.Host.ReconcileConfigurationAsync(harness.Rows).AsTask().WaitAsync(Limit);
        Assert.Equal(0, unchanged.Revision);
        Assert.Empty(unchanged.ChangedRowIds);
    }

    private static void AssertBefore(string[] order, string first, string second)
    {
        var firstIndex = Array.IndexOf(order, first);
        var secondIndex = Array.IndexOf(order, second);
        Assert.True(firstIndex >= 0, $"Missing event '{first}'.");
        Assert.True(secondIndex >= 0, $"Missing event '{second}'.");
        Assert.True(firstIndex < secondIndex, $"Expected '{first}' before '{second}'.");
    }

    private static Harness TwoGroups()
    {
        var probes = new[]
        {
            new Probe("left", observesSecondary: true), new Probe("right", observesSecondary: true),
            new Probe("left-provider", provides: true), new Probe("right-provider", provides: true),
            new Probe("left-reader", requires: true), new Probe("right-reader", requires: true), new Probe("independent")
        };
        return new Harness(
        [
            Row("left-reader", parent: "left"), Row("right-reader", parent: "right"),
            Row("left-provider", 11, "left"), Row("right-provider", 22, "right"),
            Row("left") with { IsolatedServices = new List<string> { ValueContract, SecondaryContract } },
            Row("right") with { IsolatedServices = new List<string> { ValueContract, SecondaryContract } },
            Row("independent")
        ], probes);
    }

    private static PluginRow Row(string id, int value = 0, string? parent = null) => new()
    {
        Id = id, Plugin = id, ParentId = parent, Source = "entry-tree-test", Lineage = ["shared-profile"],
        Config = new Dictionary<string, object?> { ["value"] = value, ["label"] = id },
        Lifecycle = new() { EffectCapacity = 16 }
    };

    private static PluginRow WithValue(PluginRow row, int value) =>
        row with { Config = new Dictionary<string, object?> { ["value"] = value, ["label"] = row.Id } };

    private sealed record Value(int Number) : IValue;

    private sealed class Probe(string id, bool provides = false, bool requires = false, bool optional = false, bool observesSecondary = false)
    {
        internal string Id { get; } = id;
        internal PluginAttribute Declaration { get; } = new(id)
        {
            Provides = provides ? [typeof(IValue)] : [], Requires = requires ? [typeof(IValue)] : [],
            Optional = optional ? [typeof(IValue)] : [], Observes = observesSecondary ? [typeof(ISecondary)] : []
        };
        internal ConcurrentQueue<PluginRow> Rows { get; } = new();
        internal ConcurrentQueue<IValue> Published { get; } = new();
        internal ConcurrentQueue<IValue?> Seen { get; } = new();
        internal ConcurrentQueue<bool> ReloadFlags { get; } = new();
        internal int Starts => Rows.Count;
        internal int Stops;
        internal int Closes;
        internal Func<IPluginContext, CancellationToken, ValueTask>? Activate;
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
        internal Probe[] Probes { get; }
        internal ConcurrentQueue<string> ActivationOrder { get; } = new();
        internal ConcurrentQueue<string> DisposalOrder { get; } = new();
        internal Probe this[string id] => Probes.Single(probe => probe.Id == id);
        internal Harness(PluginRow[] rows, params Probe[] probes)
        {
            Rows = rows;
            Probes = probes;
            var definitions = probes.Select(probe => new PluginDescriptor(probe.Declaration, new Factory(probe, this), "entry-tree-test", "1.0.0", Versions));
            Host = new PluginHost(rows, PluginCatalog.FromDefinitions(definitions), NullLoggerFactory.Instance);
        }
        internal PluginState State(string id) => Host.Instances.Single(instance => instance.RowId == id).State;
        internal Gate BlockResourceCleanup(string id)
        {
            var gate = new Gate();
            _gates.Add(gate);
            this[id].Activate = async (context, _) =>
            {
                await context.AcquireEffectAsync(cancellationToken => ValueTask.FromResult<IAsyncDisposable>(new Resource(async () =>
                {
                    gate.Entered.TrySetResult();
                    await gate.Release.Task;
                    DisposalOrder.Enqueue($"{id}:resource");
                })));
            };
            return gate;
        }
        /// <summary>Releases cleanup gates before joining host teardown when the test completes or fails.</summary>
        public async ValueTask DisposeAsync()
        {
            foreach (var gate in _gates) gate.Release.TrySetResult();
            await Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        }
    }

    private sealed class Options
    {
        /// <summary>Gets or sets the value published by a provider in its inherited namespace.</summary>
        public int Value { get; set; }
        /// <summary>Gets or sets a second mapping value used to verify key-order equivalence.</summary>
        public string Label { get; set; } = "";
    }

    private sealed class Resource(Func<ValueTask> release) : IAsyncDisposable
    {
        /// <summary>Waits for the test-controlled resource drain to complete.</summary>
        public ValueTask DisposeAsync() => release();
    }

    private sealed class Factory(Probe probe, Harness harness) : IPluginExecutionFactory
    {
        /// <summary>Keeps unrelated fixture definitions independent during acquisition.</summary>
        public string? CoordinationKey => null;
        /// <summary>Records module-refresh requests and creates an execution for the requested entry.</summary>
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken)
        {
            probe.ReloadFlags.Enqueue(reload);
            return ValueTask.FromResult<IPluginExecution>(new Execution(probe, harness));
        }
    }

    private sealed class Execution(Probe probe, Harness harness) : IPluginExecution
    {
        /// <summary>Exposes the interface declarations used by dependency and isolation resolution.</summary>
        public PluginAttribute Declaration => probe.Declaration;
        /// <summary>Reports the version pinned by the fixture's static descriptor.</summary>
        public string Version => "1.0.0";
        /// <summary>Uses the abstraction version loaded in this test process.</summary>
        public IReadOnlyDictionary<string, string> ContractVersions => Versions;
        /// <summary>Identifies the fixture as a backend execution without a native class.</summary>
        public Type? ImplementationType => null;
        /// <summary>Exercises actual scoped registrations and reads, then acquires any test-controlled resources.</summary>
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            var options = context.Config.Bind<Options>();
            probe.Rows.Enqueue(context.Row);
            harness.ActivationOrder.Enqueue(probe.Id);
            context.Effect(() => { Interlocked.Increment(ref probe.Stops); harness.DisposalOrder.Enqueue($"{probe.Id}:effect"); });
            if (Declaration.Provides.Contains(typeof(IValue)))
            {
                IValue value = new Value(options.Value);
                context.Effect(context.Services.Register(value));
                probe.Published.Enqueue(value);
            }
            if (Declaration.Requires.Contains(typeof(IValue))) probe.Seen.Enqueue(context.Services.Get<IValue>());
            if (Declaration.Optional.Contains(typeof(IValue)))
            {
                context.Services.TryGet<IValue>(out var value);
                probe.Seen.Enqueue(value);
            }
            if (probe.Activate is { } activate) await activate(context, cancellationToken);
        }
        /// <summary>Records backend cleanup after the activation's owned effects have drained.</summary>
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref probe.Closes);
            harness.DisposalOrder.Enqueue($"{probe.Id}:execution");
            return ValueTask.CompletedTask;
        }
    }
}
