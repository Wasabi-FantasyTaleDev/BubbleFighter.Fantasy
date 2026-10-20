using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Exercises definition and operational metadata changes while preserving hosted entry identity.</summary>
public sealed class PluginDefinitionRebindingTests
{
    private const string A = "provider.a";
    private const string B = "provider.b";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, string> Versions = new Dictionary<string, string>
    { ["BubbleFighter.Fantasy.Abstractions"] = typeof(IPlugin).Assembly.GetName().Version!.ToString() };

    /// <summary>Provides the service contract belonging to the first loaded definition.</summary>
    public interface ILeft
    {
        /// <summary>Gets the value supplied by definition A.</summary>
        int Number { get; }
    }

    /// <summary>Provides the service contract belonging to the second loaded definition.</summary>
    public interface IRight
    {
        /// <summary>Gets the value supplied by definition B.</summary>
        int Number { get; }
    }

    /// <summary>Changes the published contract under one row identity and restores the original loaded definition on return.</summary>
    [Fact]
    public async Task RebindingChangesConsumersAndCanSelectTheOriginalDefinitionAgain()
    {
        await using var harness = ServiceScene();
        await StartServiceSceneAsync(harness);
        var original = harness.Entry("swap").Last;
        var candidate = harness.Rows.Select(row => row.Id == "swap" ? Rebind(row, B, 22) : row).ToArray();

        var result = await harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit);

        Assert.Equal(1, result.Revision);
        Assert.NotEqual(PluginTransitionOutcome.Superseded, result.Outcome);
        Assert.Equal(PluginState.Pending, harness.Info("left").State);
        Assert.Equal(PluginState.Active, harness.Info("right").State);
        Assert.Equal(22, harness.Entry("right").Last.Right!.Number);
        Assert.Equal(B, harness.Entry("swap").Last.DefinitionId);
        Assert.Equal("swap", harness.Entry("swap").Last.Context.Row.Id);
        Assert.NotSame(original.Context, harness.Entry("swap").Last.Context);
        Assert.Single(harness.Definition(A).Acquisitions);
        Assert.Single(harness.Definition(B).Acquisitions);
        Assert.Equal(1, harness.Entry("independent").Starts);
        Assert.Equal(0, harness.Entry("independent").Stops);
        var returned = await harness.Host.ReconcileConfigurationAsync(harness.Rows).AsTask().WaitAsync(Limit);
        Assert.Equal(2, returned.Revision);
        Assert.Equal(PluginState.Active, harness.Info("left").State);
        Assert.Equal(PluginState.Pending, harness.Info("right").State);
        Assert.Equal(11, harness.Entry("left").Last.Left!.Number);
        Assert.Equal([A, B, A], harness.Entry("swap").Activations.Select(activation => activation.DefinitionId));
        Assert.Equal(2, harness.Definition(A).Acquisitions.Count);
        Assert.Single(harness.Definition(B).Acquisitions);
        Assert.Equal(1, harness.Entry("independent").Starts);
        Assert.All(harness.Definitions.SelectMany(definition => definition.Acquisitions), reload => Assert.False(reload));
    }

    /// <summary>Retries the proposed definition after its failed activation instead of falling back to the last successful descriptor.</summary>
    [Fact]
    public async Task FailedRebindingRetriesBWithoutReacquiringCachedA()
    {
        await using var harness = ServiceScene();
        await StartServiceSceneAsync(harness);
        var attempts = 0;
        harness.Definition(B).Activate = (activation, cancellationToken) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("definition B activation failed");
            return ValueTask.CompletedTask;
        };
        var candidate = harness.Rows.Select(row => row.Id == "swap" ? Rebind(row, B, 23) : row).ToArray();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit));

        Assert.Contains("definition B activation failed", failure.ToString());
        Assert.Equal(PluginState.Failed, harness.Info("swap").State);
        Assert.Single(harness.Definition(A).Acquisitions);
        Assert.Single(harness.Definition(B).Acquisitions);
        Assert.Equal(0, harness.Entry("right").Starts);
        var retried = await harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit);
        Assert.Equal(1, retried.Revision);
        Assert.Equal(PluginState.Active, harness.Info("swap").State);
        Assert.Equal(23, harness.Entry("right").Last.Right!.Number);
        Assert.Equal([A, B, B], harness.Entry("swap").Activations.Select(activation => activation.DefinitionId));
        Assert.Single(harness.Definition(A).Acquisitions);
        Assert.Equal(2, harness.Definition(B).Acquisitions.Count);
        Assert.All(harness.Definition(B).Acquisitions, reload => Assert.False(reload));
    }

    /// <summary>Validates the selected loaded definition and immutable bootstrap grants before stopping the previous activation.</summary>
    [Theory]
    [InlineData("unknown-plugin")]
    [InlineData("old-version-range")]
    [InlineData("version-syntax")]
    [InlineData("unavailable-release")]
    [InlineData("unsupported-build")]
    [InlineData("startup-grant")]
    public async Task InvalidDefinitionMetadataIsRejectedBeforeTeardown(string change)
    {
        var initial = Row("swap", A, 11);
        await using var harness = new Harness([initial]);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var candidate = change switch
        {
            "unknown-plugin" => initial with { Plugin = "not-loaded" },
            "old-version-range" => Rebind(initial, B, 22) with { Version = ">=1 <2" },
            "version-syntax" => Rebind(initial, B, 22) with { Version = "^2" },
            "unavailable-release" => initial with { Version = "=1.3.0" },
            "unsupported-build" => Rebind(initial, B, 22) with { ClientBuild = new ClientBuild("kr", 1100, 0) },
            _ => initial with { Lifecycle = initial.Lifecycle with { EntryReserve = 1 } }
        };

        await Assert.ThrowsAsync<ConfigException>(() => harness.Host.ReconcileConfigurationAsync([candidate]).AsTask().WaitAsync(Limit));

        Assert.Single(harness.Definition(A).Acquisitions);
        Assert.Empty(harness.Definition(B).Acquisitions);
        Assert.Equal(0, harness.Entry("swap").Stops);
        Assert.Equal(0, harness.Entry("swap").Closes);
        Assert.Equal(PluginState.Active, harness.Info("swap").State);
        Assert.Equal(A, harness.Entry("swap").Last.DefinitionId);
        var unchanged = await harness.Host.ReconcileConfigurationAsync([initial]).AsTask().WaitAsync(Limit);
        Assert.Equal(0, unchanged.Revision);
    }

    /// <summary>A matching constraint update restarts against the already loaded release without requesting a backend refresh.</summary>
    [Fact]
    public async Task ValidVersionConstraintChangesUseTheSelectedLoadedRelease()
    {
        var initial = Row("swap", A, 11);
        await using var harness = new Harness([initial]);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var previous = harness.Entry("swap").Last.Context;

        var result = await harness.Host.ReconcileConfigurationAsync(
            [initial with { Version = ">=1.0.0 <2" }]).AsTask().WaitAsync(Limit);

        Assert.Equal(1, result.Revision);
        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
        Assert.Equal("1.2.0", harness.Info("swap").Version);
        Assert.Equal(">=1.0.0 <2", harness.Entry("swap").Last.Context.Row.Version);
        Assert.NotSame(previous, harness.Entry("swap").Last.Context);
        Assert.Equal(2, harness.Definition(A).Acquisitions.Count);
        Assert.All(harness.Definition(A).Acquisitions, reload => Assert.False(reload));
    }

    /// <summary>Retains the current activation when a complete candidate copies equivalent definition metadata.</summary>
    [Fact]
    public async Task EquivalentDefinitionMetadataPreservesActivationAndFactoryCounts()
    {
        await using var harness = ServiceScene();
        await StartServiceSceneAsync(harness);
        var previous = harness.Entry("swap").Last.Context;
        var counts = harness.Definitions.ToDictionary(definition => definition.Id, definition => definition.Acquisitions.Count, StringComparer.Ordinal);
        var candidate = harness.Rows.Reverse().Select(row => row with
        {
            Lineage = row.Lineage.ToArray(), Lifecycle = row.Lifecycle with { },
            Config = new Dictionary<string, object?> { ["tag"] = row.Id, ["value"] = row.Config!["value"] }
        }).ToArray();

        var result = await harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit);

        Assert.Equal(0, result.Revision);
        Assert.Empty(result.ChangedRowIds);
        Assert.Same(previous, harness.Entry("swap").Last.Context);
        Assert.All(harness.Definitions, definition => Assert.Equal(counts[definition.Id], definition.Acquisitions.Count));
        Assert.Equal(0, harness.Entry("swap").Stops);
    }

    /// <summary>Reports the accepted definition for an initially disabled entry without acquiring either execution.</summary>
    [Fact]
    public async Task InitiallyDisabledRebindingReportsTheAcceptedDefinitionWithoutAcquisition()
    {
        var initial = Row("swap", A, 11, disabled: true);
        await using var harness = new Harness([initial]);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        Assert.Equal(A, harness.Info("swap").Plugin);

        var result = await harness.Host.ReconcileConfigurationAsync([Rebind(initial, B, 22)])
            .AsTask().WaitAsync(Limit);

        Assert.Equal(1, result.Revision);
        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
        Assert.Equal(PluginState.Unloaded, harness.Info("swap").State);
        Assert.Equal(B, harness.Info("swap").Plugin);
        Assert.Equal("2.3.0", harness.Info("swap").Version);
        Assert.Equal(["kr:1256-1304"], harness.Info("swap").SupportedBuilds);
        Assert.Null(harness.Info("swap").Error);
        Assert.False(harness.Info("swap").IsRetiring);
        Assert.Empty(harness.Definition(A).Acquisitions);
        Assert.Empty(harness.Definition(B).Acquisitions);
        Assert.Equal(0, harness.Entry("swap").Starts);
    }

    /// <summary>Keeps old diagnostics while the backend is retained, then reports and enables the accepted disabled definition.</summary>
    [Fact]
    public async Task DisabledRebindingRetainsOldDiagnosticsUntilDrainAndThenEnablesTheNewDefinition()
    {
        var initial = Row("swap", A, 11);
        await using var harness = new Harness([initial]);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var oldContext = harness.Entry("swap").Last.Context;
        Assert.Equal(A, harness.Info("swap").Plugin);
        Assert.Equal(PluginState.Active, harness.Info("swap").State);
        var gate = harness.BlockBackendClose(A);
        var candidate = Rebind(initial, B, 22) with { Disabled = true };

        var changing = harness.Host.ReconcileConfigurationAsync([candidate]).AsTask();
        try
        {
            await gate.Entered.Task.WaitAsync(Limit);
            Assert.False(changing.IsCompleted);
            Assert.Equal(PluginState.Unloading, harness.Info("swap").State);
            Assert.Equal(A, harness.Info("swap").Plugin);
            Assert.Equal("1.2.0", harness.Info("swap").Version);
            Assert.Equal(["kr:1100-1256"], harness.Info("swap").SupportedBuilds);
            Assert.Equal(A, oldContext.Row.Plugin);
            Assert.False(oldContext.Row.Disabled);
            Assert.Equal(0, Volatile.Read(ref harness.Definition(A).Closed));
            Assert.Empty(harness.Definition(B).Acquisitions);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
        var result = await changing.WaitAsync(Limit);

        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
        Assert.Equal(1, Volatile.Read(ref harness.Definition(A).Closed));
        Assert.Equal(PluginState.Unloaded, harness.Info("swap").State);
        Assert.Equal(B, harness.Info("swap").Plugin);
        Assert.Equal("2.3.0", harness.Info("swap").Version);
        Assert.Equal(["kr:1256-1304"], harness.Info("swap").SupportedBuilds);
        Assert.Empty(harness.Definition(B).Acquisitions);

        var enabled = await harness.Host.RequestState("swap", true).WaitAsync().AsTask().WaitAsync(Limit);

        Assert.Equal(PluginTransitionOutcome.Converged, enabled.Outcome);
        Assert.Equal(PluginState.Active, harness.Info("swap").State);
        Assert.Equal(B, harness.Entry("swap").Last.DefinitionId);
        Assert.Equal(22, harness.Entry("swap").Last.Context.Config.Bind<Options>().Value);
        Assert.NotSame(oldContext, harness.Entry("swap").Last.Context);
        Assert.Single(harness.Definition(A).Acquisitions);
        Assert.Single(harness.Definition(B).Acquisitions);
    }

    /// <summary>Preserves an explicit stopped target across configuration and definition changes until a new enable request.</summary>
    [Fact]
    public async Task ManuallyStoppedEntryStaysOffThroughConfigurationAndDefinitionChanges()
    {
        var initial = Row("swap", A, 11);
        await using var harness = new Harness([initial]);
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var stopped = await harness.Host.RequestState("swap", false).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.Converged, stopped.Outcome);
        Assert.Equal(PluginState.Unloaded, harness.Info("swap").State);
        var configured = Rebind(initial, A, 12);

        var configuredResult = await harness.Host.ReconcileConfigurationAsync([configured]).AsTask().WaitAsync(Limit);

        Assert.Equal(PluginTransitionOutcome.Converged, configuredResult.Outcome);
        Assert.Equal(1, configuredResult.Revision);
        Assert.Equal(PluginState.Unloaded, harness.Info("swap").State);
        Assert.Equal(A, harness.Info("swap").Plugin);
        Assert.Single(harness.Definition(A).Acquisitions);
        var rebound = Rebind(configured, B, 22);
        Assert.False(rebound.Disabled);

        var reboundResult = await harness.Host.ReconcileConfigurationAsync([rebound]).AsTask().WaitAsync(Limit);

        Assert.Equal(PluginTransitionOutcome.Converged, reboundResult.Outcome);
        Assert.Equal(2, reboundResult.Revision);
        Assert.Equal(PluginState.Unloaded, harness.Info("swap").State);
        Assert.Equal(B, harness.Info("swap").Plugin);
        Assert.Equal("2.3.0", harness.Info("swap").Version);
        Assert.Equal(1, harness.Entry("swap").Starts);
        Assert.Equal(1, Volatile.Read(ref harness.Entry("swap").Closes));
        Assert.Single(harness.Definition(A).Acquisitions);
        Assert.Empty(harness.Definition(B).Acquisitions);

        var enabled = await harness.Host.RequestState("swap", true).WaitAsync().AsTask().WaitAsync(Limit);

        Assert.Equal(PluginTransitionOutcome.Converged, enabled.Outcome);
        Assert.Equal(PluginState.Active, harness.Info("swap").State);
        Assert.Equal(B, harness.Entry("swap").Last.DefinitionId);
        Assert.Equal(22, harness.Entry("swap").Last.Context.Config.Bind<Options>().Value);
        Assert.Single(harness.Definition(A).Acquisitions);
        Assert.Single(harness.Definition(B).Acquisitions);
    }

    private static Harness ServiceScene() => new(
    [
        Row("swap", A, 11), Row("left", "left-reader"),
        Row("right", "right-reader", disabled: true), Row("independent", "node")
    ]);

    private static async Task StartServiceSceneAsync(Harness harness)
    {
        await harness.Host.StartAsync(default).AsTask().WaitAsync(Limit);
        var waiting = await harness.Host.RequestState("right", true).WaitAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.WaitingForDependencies, waiting.Outcome);
        Assert.Equal(PluginState.Pending, harness.Info("right").State);
    }

    private static PluginRow Row(string id, string plugin, int value = 0, string? parent = null, bool disabled = false, int effects = 16, int timeout = 10000) => new()
    {
        Id = id, Plugin = plugin, ParentId = parent, Disabled = disabled, Source = "definition-rebinding-test",
        Version = plugin == A ? "=1.2.0" : plugin == B ? "=2.3.0" : null,
        ClientBuild = new ClientBuild("kr", 1256, 0), Lineage = ["initial-profile"],
        Config = new Dictionary<string, object?> { ["value"] = value, ["tag"] = id },
        Lifecycle = new() { EffectCapacity = effects, TeardownTimeoutMs = timeout }
    };

    private static PluginRow Rebind(PluginRow row, string plugin, int value) => row with
    {
        Plugin = plugin, Version = plugin == A ? "=1.2.0" : "=2.3.0",
        Config = new Dictionary<string, object?> { ["value"] = value, ["tag"] = row.Id }
    };

    private sealed record Left(int Number) : ILeft;
    private sealed record Right(int Number) : IRight;
    private sealed record Activation(IPluginContext Context, string DefinitionId)
    {
        internal ILeft? Left;
        internal IRight? Right;
    }

    private sealed class EntryProbe
    {
        internal ConcurrentQueue<Activation> Activations { get; } = new();
        internal Activation Last => Activations.Last();
        internal int Starts => Activations.Count;
        internal int Stops;
        internal int Closes;
        internal Func<Activation, CancellationToken, ValueTask>? Activate;
    }

    private sealed class DefinitionProbe(string id, string version = "1.0.0", Type[]? provides = null, Type[]? requires = null, string[]? builds = null, string? key = null)
    {
        internal string Id { get; } = id;
        internal string Version { get; } = version;
        internal string? CoordinationKey { get; } = key;
        internal PluginAttribute Declaration { get; } = new(id)
        { Provides = provides ?? [], Requires = requires ?? [], SupportedBuilds = builds ?? [] };
        internal ConcurrentQueue<bool> Acquisitions { get; } = new();
        internal int Closing;
        internal int Closed;
        internal Func<Activation, CancellationToken, ValueTask>? Activate;
        internal Func<Activation?, ValueTask>? Close;
    }

    private sealed class Gate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, EntryProbe> _entries = new(StringComparer.Ordinal);
        private readonly List<Gate> _gates = [];
        internal PluginHost Host { get; }
        internal PluginRow[] Rows { get; }
        internal DefinitionProbe[] Definitions { get; } =
        [
            new(A, "1.2.0", provides: [typeof(ILeft)], builds: ["kr:1100-1256"], key: "module-a"),
            new(B, "2.3.0", provides: [typeof(IRight)], builds: ["kr:1256-1304"], key: "module-b"),
            new("left-reader", requires: [typeof(ILeft)]), new("right-reader", requires: [typeof(IRight)]),
            new("node"), new("same-a-module", key: "module-a"), new("same-b-module", key: "module-b")
        ];
        internal EntryProbe Entry(string id) => _entries.GetOrAdd(id, _ => new());
        internal DefinitionProbe Definition(string id) => Definitions.Single(definition => definition.Id == id);
        internal PluginInstanceInfo Info(string id) => Host.Instances.Single(instance => instance.RowId == id);
        internal Harness(PluginRow[] rows)
        {
            Rows = rows;
            var definitions = Definitions.Select(definition => new PluginDescriptor(definition.Declaration,
                new ExecutionFactory(definition, this), "definition-rebinding-test", definition.Version, Versions));
            Host = new PluginHost(rows, PluginCatalog.FromDefinitions(definitions), NullLoggerFactory.Instance);
        }
        internal Gate BlockBackendClose(string definitionId)
        {
            var gate = new Gate();
            _gates.Add(gate);
            Definition(definitionId).Close = async activation =>
            { gate.Entered.TrySetResult(); await gate.Release.Task; };
            return gate;
        }
        internal Gate BlockActivation(string entryId)
        {
            var gate = new Gate();
            _gates.Add(gate);
            Entry(entryId).Activate = async (activation, cancellationToken) =>
            { gate.Entered.TrySetResult(); await gate.Release.Task; };
            return gate;
        }
        /// <summary>Releases every controlled backend or activation before joining host shutdown.</summary>
        public async ValueTask DisposeAsync()
        {
            foreach (var gate in _gates) gate.Release.TrySetResult();
            await Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        }
    }

    private sealed class Options
    {
        /// <summary>Gets or sets the service value emitted by the loaded definition.</summary>
        public int Value { get; set; }
        /// <summary>Gets or sets a second key used to check mapping-order equivalence.</summary>
        public string Tag { get; set; } = "";
    }

    private sealed class ExecutionFactory(DefinitionProbe definition, Harness harness) : IPluginExecutionFactory
    {
        /// <summary>Reports the loaded definition's stable backend coordination identity.</summary>
        public string? CoordinationKey => definition.CoordinationKey;
        /// <summary>Records selection of the actual loaded definition before returning a new execution.</summary>
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken)
        {
            definition.Acquisitions.Enqueue(reload);
            return ValueTask.FromResult<IPluginExecution>(new Execution(definition, harness));
        }
    }

    private sealed class Execution(DefinitionProbe definition, Harness harness) : IPluginExecution
    {
        private EntryProbe? _entry;
        private Activation? _activation;
        /// <summary>Returns the loaded definition's service and build declarations.</summary>
        public PluginAttribute Declaration => definition.Declaration;
        /// <summary>Reports the release currently selected from the catalog.</summary>
        public string Version => definition.Version;
        /// <summary>References the shared abstraction version loaded by this test process.</summary>
        public IReadOnlyDictionary<string, string> ContractVersions => Versions;
        /// <summary>Identifies the fixture as a backend execution without a native implementing class.</summary>
        public Type? ImplementationType => null;
        /// <summary>Captures row metadata and exercises the contracts belonging to the selected definition.</summary>
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            var value = context.Config.Bind<Options>().Value;
            var entry = harness.Entry(context.Row.Id);
            var activation = new Activation(context, definition.Id);
            _entry = entry;
            _activation = activation;
            entry.Activations.Enqueue(activation);
            context.Effect(() => Interlocked.Increment(ref entry.Stops));
            if (Declaration.Provides.Contains(typeof(ILeft))) context.Effect(context.Services.Register<ILeft>(new Left(value)));
            if (Declaration.Provides.Contains(typeof(IRight))) context.Effect(context.Services.Register<IRight>(new Right(value)));
            if (Declaration.Requires.Contains(typeof(ILeft))) activation.Left = context.Services.Get<ILeft>();
            if (Declaration.Requires.Contains(typeof(IRight))) activation.Right = context.Services.Get<IRight>();
            if (definition.Activate is { } definitionActivate) await definitionActivate(activation, cancellationToken);
            if (entry.Activate is { } entryActivate) await entryActivate(activation, cancellationToken);
        }
        /// <summary>Retains backend ownership until the controlled close callback has actually completed.</summary>
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref definition.Closing);
            if (definition.Close is { } close) await close(_activation);
            Interlocked.Increment(ref definition.Closed);
            if (_entry is { } entry) Interlocked.Increment(ref entry.Closes);
        }
    }
}
