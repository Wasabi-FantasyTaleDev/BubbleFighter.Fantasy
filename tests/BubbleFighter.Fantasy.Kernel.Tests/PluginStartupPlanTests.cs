using System.Collections.Concurrent;
using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Exercises fixed startup grants and deferred game rows through actual hosted definition sources.</summary>
public sealed class PluginStartupPlanTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, string> Versions = new Dictionary<string, string>
    { ["BubbleFighter.Fantasy.Abstractions"] = typeof(IPlugin).Assembly.GetName().Version!.ToString() };

    /// <summary>Identifies a service that exists only in definitions published by the bootstrap owner.</summary>
    public interface IValue
    {
        /// <summary>Gets a deterministic value supplied by a deferred game activation.</summary>
        int Number { get; }
    }

    private sealed record Value(int Number) : IValue;

    /// <summary>Deferred unknown definitions and isolation names are resolved only after the initial owner publishes its source.</summary>
    [Fact]
    public async Task DeferredDefinitionAndIsolationDoNotNeedToExistInSeedCatalog()
    {
        var game = GameRow() with { IsolatedServices = [typeof(IValue).FullName!] };
        var scene = new Scene(OwnerRow(), [game]);
        try
        {
            Assert.Equal("owner", Assert.Single(scene.Host.Instances).RowId);
            Assert.DoesNotContain(scene.Host.Definitions, definition => definition.Id == "game.logic");
            Assert.Equal(0, scene.Game.Acquisitions);
            await scene.Host.StartAsync(default).AsTask().WaitAsync(Limit);
            Assert.Equal(0, scene.Game.Acquisitions);
            Assert.Contains(scene.Host.Definitions, definition => definition.Id == "game.logic");
            await scene.AdmitDeferredAsync();
            Assert.Equal(PluginState.Active, scene.Host.Instances.Single(row => row.RowId == "game").State);
            Assert.Equal([typeof(IValue).FullName!], Assert.Single(scene.Game.Contexts).Row.IsolatedServices);
            Assert.Equal(1, scene.Game.Starts);
        }
        finally
        {
            await scene.Host.ReconcileConfigurationAsync([]).AsTask().WaitAsync(Limit);
            await scene.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        }
    }

    /// <summary>All four deferred startup grants count before the owner runs and survive admission without being reissued.</summary>
    [Fact]
    public async Task DeferredStartupGrantsFundInitialSourceAndRemainFixedAfterAdmission()
    {
        var owner = OwnerRow() with { Lifecycle = new() { EffectCapacity = 2 } };
        var game = GameRow() with
        {
            Lifecycle = new()
            {
                EffectCapacity = 2, EntryReserve = 1, ServiceMetadataReserve = 3,
                DefinitionSourceReserve = 1, DefinitionReserve = 1,
            },
        };
        var scene = new Scene(owner, [game]);
        try
        {
            var budget = Budget(scene.Host);
            Assert.Equal(3, budget.EntryCapacity);
            Assert.Equal(15, budget.ServiceMetadataCapacity);
            Assert.Equal(1, budget.DefinitionSourceCapacity);
            Assert.Equal(2, budget.DefinitionCapacity);
            await scene.Host.StartAsync(default).AsTask().WaitAsync(Limit);
            Assert.Single(scene.Host.DefinitionSources);
            await scene.AdmitDeferredAsync();
            Assert.Same(budget, Budget(scene.Host));
            Assert.Equal(3, budget.EntryCapacity);
            Assert.Equal(15, budget.ServiceMetadataCapacity);
            Assert.Equal(1, budget.DefinitionSourceCapacity);
            Assert.Equal(2, budget.DefinitionCapacity);
            var extra = GameRow() with { Id = "extra", Disabled = true };
            await scene.Host.ReconcileConfigurationAsync([.. scene.AcceptedRows, extra]).AsTask().WaitAsync(Limit);
            Assert.Equal(3, scene.Host.Instances.Count);
            Assert.Equal(1, scene.Game.Starts);
            Assert.Same(budget, Budget(scene.Host));
        }
        finally { await scene.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
    }

    /// <summary>A new identity cannot manufacture any category of startup grant even when entry and metadata capacity remain.</summary>
    [Theory]
    [InlineData("entry")]
    [InlineData("metadata")]
    [InlineData("source")]
    [InlineData("definition")]
    public async Task NewlyIntroducedIdentityCannotMintStartupGrants(string grant)
    {
        var scene = new Scene(OwnerRow() with { Lifecycle = OwnerRow().Lifecycle with { EntryReserve = 1, ServiceMetadataReserve = 3 } }, [GameRow()]);
        try
        {
            await scene.Host.StartAsync(default).AsTask().WaitAsync(Limit);
            await scene.AdmitDeferredAsync();
            var limits = new PluginLifecycleOptions { EffectCapacity = 2 };
            limits = grant switch
            {
                "entry" => limits with { EntryReserve = 1 },
                "metadata" => limits with { ServiceMetadataReserve = 1 },
                "source" => limits with { DefinitionSourceReserve = 1 },
                _ => limits with { DefinitionReserve = 1 },
            };
            var forged = GameRow() with { Id = "forged", Disabled = true, Lifecycle = limits };
            var failure = await Assert.ThrowsAsync<ConfigException>(() => scene.Host.ReconcileConfigurationAsync(
                [.. scene.AcceptedRows, forged]).AsTask().WaitAsync(Limit));
            Assert.Contains("startup grants", failure.Message);
            Assert.DoesNotContain(scene.Host.Instances, row => row.RowId == "forged");
            Assert.Equal(1, scene.Game.Starts);
            Assert.Equal(0, scene.Batch.Disposals);
        }
        finally { await scene.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
    }

    /// <summary>Initial and deferred rows fund exactly one identity and declared effect capacity each, with no second admission charge.</summary>
    [Fact]
    public async Task ExactStartupCapacityAdmitsDeferredRowsOnceAndRejectsExcessMetadata()
    {
        var scene = new Scene(OwnerRow(), [GameRow()]);
        try
        {
            var budget = Budget(scene.Host);
            Assert.Equal(2, budget.EntryCapacity);
            Assert.Equal(12, budget.ServiceMetadataCapacity);
            await scene.Host.StartAsync(default).AsTask().WaitAsync(Limit);
            await scene.AdmitDeferredAsync();
            Assert.Equal(2, scene.Host.Instances.Count);
            Assert.All(scene.Host.Instances, row => Assert.Equal(PluginState.Active, row.State));
            var oversized = scene.Plan.DeferredRows[0] with { Lifecycle = scene.Plan.DeferredRows[0].Lifecycle with { EffectCapacity = 3 } };
            var failure = await Assert.ThrowsAsync<ConfigException>(() => scene.Host.ReconcileConfigurationAsync(
                [.. scene.Plan.InitialRows, oversized]).AsTask().WaitAsync(Limit));
            Assert.Contains("service metadata capacity", failure.Message);
            Assert.Equal(1, scene.Game.Starts);
            Assert.Equal(0, scene.Game.Disposals);
            Assert.Same(budget, Budget(scene.Host));
        }
        finally { await scene.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
    }

    /// <summary>Source-owned deferred activations finish their asynchronous effects and execution before their batch can close.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownOrJointRemovalDrainsGameBeforeSourceBatch(bool removeAll)
    {
        var scene = new Scene(OwnerRow(), [GameRow()]);
        var entered = Signal();
        var release = Signal();
        scene.Game.EffectClosing = entered;
        scene.Game.EffectRelease = release;
        Task? cleanup = null;
        try
        {
            await scene.Host.StartAsync(default).AsTask().WaitAsync(Limit);
            await scene.AdmitDeferredAsync();
            scene.Batch.BeforeDispose = () =>
            {
                Assert.Equal(1, scene.Game.EffectStops);
                Assert.Equal(1, scene.Game.Disposals);
            };
            cleanup = removeAll
                ? scene.Host.ReconcileConfigurationAsync([]).AsTask()
                : scene.Host.ShutdownAsync(default).AsTask();
            await entered.Task.WaitAsync(Limit);
            Assert.False(cleanup.IsCompleted);
            Assert.Equal(0, scene.Batch.Disposals);
            Assert.Equal(0, scene.Game.Disposals);
            release.TrySetResult();
            await cleanup.WaitAsync(Limit);
            Assert.Equal(1, scene.Batch.Disposals);
            Assert.Equal(1, scene.Game.Disposals);
            Assert.Equal(1, scene.Game.EffectStops);
            if (removeAll) Assert.Empty(scene.Host.Instances);
        }
        finally
        {
            release.TrySetResult();
            if (cleanup is not null) await cleanup.WaitAsync(Limit);
            await scene.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        }
    }

    /// <summary>Removing only the bootstrap parent is atomically rejected while a deferred child still names it.</summary>
    [Fact]
    public async Task LoaderOnlyRemovalRejectsRetainedDeferredChild()
    {
        var scene = new Scene(OwnerRow(), [GameRow() with { ParentId = "owner" }]);
        try
        {
            await scene.Host.StartAsync(default).AsTask().WaitAsync(Limit);
            await scene.AdmitDeferredAsync();
            var failure = await Assert.ThrowsAsync<ConfigException>(() => scene.Host.ReconcileConfigurationAsync(
                scene.Plan.DeferredRows).AsTask().WaitAsync(Limit));
            Assert.Equal("Row 'game' requires entry row(s) the candidate does not contain: owner.", failure.Message);
            Assert.Equal(2, scene.Host.Instances.Count);
            Assert.All(scene.Host.Instances, row => Assert.Equal(PluginState.Active, row.State));
            Assert.Equal(0, scene.Batch.Disposals);
            Assert.Equal(0, scene.Game.Disposals);
        }
        finally { await scene.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
    }

    /// <summary>Caller arrays, dictionaries and lineage mutations cannot rewrite a captured startup plan before deferred admission.</summary>
    [Fact]
    public async Task StartupPlanCapturesInitialAndDeferredInputsForLaterAdmission()
    {
        var ownerConfig = new Dictionary<string, object?> { ["tag"] = "initial" };
        var gameConfig = new Dictionary<string, object?> { ["tag"] = "deferred" };
        var lineage = new[] { "profile" };
        var initial = new[] { OwnerRow() with { Config = ownerConfig } };
        var deferred = new[] { GameRow() with { Config = gameConfig, Lineage = lineage } };
        var scene = new Scene(initial, deferred);
        try
        {
            ownerConfig["tag"] = "changed";
            gameConfig["tag"] = "changed";
            lineage[0] = "changed";
            initial[0] = initial[0] with { Id = "changed-owner" };
            deferred[0] = deferred[0] with { Id = "changed-game", Plugin = "missing" };
            Assert.Throws<NotSupportedException>(() => ((IList<PluginRow>)scene.Plan.InitialRows).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<PluginRow>)scene.Plan.DeferredRows).Clear());
            await scene.Host.StartAsync(default).AsTask().WaitAsync(Limit);
            await scene.AdmitDeferredAsync();
            Assert.Equal("initial", Assert.Single(scene.Owner.Contexts).Row.Config!["tag"]);
            var game = Assert.Single(scene.Game.Contexts);
            Assert.Equal("deferred", game.Row.Config!["tag"]);
            Assert.Equal(["profile"], game.Row.Lineage);
            Assert.Equal("game", game.Row.Id);
            Assert.Equal("owner", scene.Plan.InitialRows[0].Id);
        }
        finally { await scene.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
    }

    /// <summary>Duplicate identities across the two startup phases are rejected before any factory can run.</summary>
    [Fact]
    public void DuplicateInitialAndDeferredIdentitiesAreRejected()
    {
        var factory = new Factory(new PluginAttribute("owner.logic"));
        var catalog = PluginCatalog.FromDefinitions([Descriptor(factory)]);
        Assert.Throws<ConfigException>(() => new PluginHost(
            new PluginStartupPlan([OwnerRow()], [GameRow() with { Id = "owner" }]), catalog, NullLoggerFactory.Instance));
        Assert.Equal(0, factory.Acquisitions);
    }

    /// <summary>Negative collection counts in either startup phase fail before collection enumeration.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativeReportedCountIsRejectedBeforeEnumeration(bool deferred)
    {
        var invalid = new ReportedRows([], () => -1);
        var failure = Assert.Throws<ConfigException>(() => deferred
            ? new PluginStartupPlan([], invalid)
            : new PluginStartupPlan(invalid, []));
        Assert.Contains("startup row count must not be negative", failure.Message);
        Assert.Equal(0, invalid.Enumerations);
    }

    /// <summary>Enumeration cannot silently add or omit rows relative to the count captured for either startup phase.</summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    public void EnumerationMustMatchReportedCount(bool deferred, int reported)
    {
        var invalid = new ReportedRows([GameRow()], () => reported);
        var failure = Assert.Throws<ConfigException>(() => deferred
            ? new PluginStartupPlan([], invalid)
            : new PluginStartupPlan(invalid, []));
        Assert.Contains(reported == 0 ? "enumeration exceeds its reported count" : "collection changed or differs from its reported count", failure.Message);
        Assert.Equal(1, invalid.Enumerations);
    }

    /// <summary>A collection cannot change its count after a successful enumeration and still enter the captured plan.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CountDriftAfterCaptureIsRejected(bool deferred)
    {
        var reads = 0;
        var invalid = new ReportedRows([GameRow()], () => ++reads == 1 ? 1 : 2);
        var failure = Assert.Throws<ConfigException>(() => deferred
            ? new PluginStartupPlan([], invalid)
            : new PluginStartupPlan(invalid, []));
        Assert.Contains("collection changed or differs from its reported count", failure.Message);
        Assert.Equal(1, invalid.Enumerations);
        Assert.Equal(2, reads);
    }

    /// <summary>Aggregate reported count overflow is rejected before either collection starts producing rows.</summary>
    [Fact]
    public void AggregateCountOverflowDoesNotEnumerateEitherCollection()
    {
        var initial = new ReportedRows([], () => int.MaxValue);
        var deferred = new ReportedRows([], () => 1);
        var failure = Assert.Throws<ConfigException>(() => new PluginStartupPlan(initial, deferred));
        Assert.Equal("The aggregate startup entry count exceeds the supported range.", failure.Message);
        Assert.Equal(0, initial.Enumerations);
        Assert.Equal(0, deferred.Enumerations);
    }

    /// <summary>Invalid deferred resource limits are refused during plan capture before any host or factory exists.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, -1)]
    public void MalformedDeferredLifecycleIsRejectedBeforeHostConstruction(int effects, int entryReserve)
    {
        var deferred = GameRow() with { Lifecycle = new() { EffectCapacity = effects, EntryReserve = entryReserve } };
        var failure = Assert.Throws<ConfigException>(() => new PluginStartupPlan([OwnerRow()], [deferred]));
        Assert.Contains("game", failure.Message);
        Assert.Contains(effects == 0 ? "EffectCapacity" : "EntryReserve", failure.Message);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static PluginRuntimeBudget Budget(PluginHost host) =>
        (PluginRuntimeBudget)typeof(PluginHost).GetField("_budget", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
    private static PluginRow OwnerRow() => new()
    {
        Id = "owner", Plugin = "owner.logic", Source = "startup-test",
        Lifecycle = new() { EffectCapacity = 2, DefinitionSourceReserve = 1, DefinitionReserve = 1 },
    };
    private static PluginRow GameRow() => new()
    {
        Id = "game", Plugin = "game.logic", Source = "startup-test", Lifecycle = new() { EffectCapacity = 2 },
    };
    private static PluginDescriptor Descriptor(Factory factory) => new(factory.Declaration, factory, "startup-test", "1.0.0", Versions);

    private sealed class ReportedRows(IReadOnlyList<PluginRow> rows, Func<int> reportCount) : IReadOnlyList<PluginRow>
    {
        internal int Enumerations;
        /// <summary>Reports the deliberately controlled count independently of enumeration.</summary>
        public int Count => reportCount();
        /// <summary>Returns fixture rows if the implementation elects to inspect indexed elements.</summary>
        public PluginRow this[int index] => rows[index];
        /// <summary>Records any enumeration so pre-enumeration rejection has an observable boundary.</summary>
        public IEnumerator<PluginRow> GetEnumerator()
        {
            Interlocked.Increment(ref Enumerations);
            return rows.GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Scene
    {
        internal Factory Owner { get; } = new(new PluginAttribute("owner.logic") { Requires = [typeof(IPluginHost)] });
        internal Factory Game { get; } = new(new PluginAttribute("game.logic") { Provides = [typeof(IValue)] });
        internal Batch Batch { get; }
        internal PluginStartupPlan Plan { get; }
        internal PluginHost Host { get; }
        internal PluginRow[] AcceptedRows => [.. Plan.InitialRows, .. Plan.DeferredRows];

        internal Scene(PluginRow initial, IReadOnlyList<PluginRow> deferred) : this([initial], deferred) { }
        internal Scene(IReadOnlyList<PluginRow> initial, IReadOnlyList<PluginRow> deferred)
        {
            Batch = new(new PluginDefinition(Game.Declaration, Game, "startup-source", "1.0.0", Versions));
            Owner.Activate = async context =>
            {
                await context.Services.Get<IPluginHost>().RegisterDefinitionSourceAsync(context, "game-source", 1, [],
                    _ => ValueTask.FromResult<IPluginDefinitionBatch>(Batch));
            };
            Plan = new(initial, deferred);
            Host = new(Plan, PluginCatalog.FromDefinitions([Descriptor(Owner)]), NullLoggerFactory.Instance);
        }
        internal Task AdmitDeferredAsync() => Host.ReconcileConfigurationAsync(AcceptedRows).AsTask().WaitAsync(Limit);
    }

    private sealed class Factory(PluginAttribute declaration) : IPluginExecutionFactory
    {
        internal PluginAttribute Declaration => declaration;
        internal int Acquisitions;
        internal int Starts;
        internal int Disposals;
        internal int EffectStops;
        internal ConcurrentQueue<IPluginContext> Contexts { get; } = new();
        internal Func<IPluginContext, ValueTask>? Activate { get; set; }
        internal TaskCompletionSource? EffectClosing { get; set; }
        internal TaskCompletionSource? EffectRelease { get; set; }
        /// <summary>Leaves neutral fixture executions independent of native module coordination.</summary>
        public string? CoordinationKey => null;
        /// <summary>Records genuine host acquisition before returning a separately owned executor.</summary>
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Acquisitions);
            return ValueTask.FromResult<IPluginExecution>(new Execution(this));
        }
    }

    private sealed class Execution(Factory owner) : IPluginExecution
    {
        /// <summary>Returns the definition admitted from either the seed or its dynamic source.</summary>
        public PluginAttribute Declaration => owner.Declaration;
        /// <summary>Supplies a stable fixture release for compatibility checks.</summary>
        public string Version => "1.0.0";
        /// <summary>Uses the currently built shared abstraction version.</summary>
        public IReadOnlyDictionary<string, string> ContractVersions => Versions;
        /// <summary>Ensures startup behavior does not depend on native implementation types.</summary>
        public Type? ImplementationType => null;
        /// <summary>Publishes its declared service and records an asynchronously owned cleanup effect.</summary>
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner.Starts);
            owner.Contexts.Enqueue(context);
            if (owner.Declaration.Provides.Contains(typeof(IValue))) context.Services.Register<IValue>(new Value(7));
            await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(new Effect(owner)));
            if (owner.Activate is { } activate) await activate(context);
        }
        /// <summary>Records backend release after its activation effects have actually finished.</summary>
        public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.Disposals); return ValueTask.CompletedTask; }
    }

    private sealed class Effect(Factory owner) : IAsyncDisposable
    {
        /// <summary>Can hold cleanup behind an explicit test gate without sleeping or relaxing host timeouts.</summary>
        public async ValueTask DisposeAsync()
        {
            owner.EffectClosing?.TrySetResult();
            if (owner.EffectRelease is { } release) await release.Task;
            Interlocked.Increment(ref owner.EffectStops);
        }
    }

    private sealed class Batch(PluginDefinition definition) : IPluginDefinitionBatch
    {
        internal int Disposals;
        internal Action? BeforeDispose { get; set; }
        /// <summary>Exposes the complete game definition batch that initial seed discovery could not see.</summary>
        public IReadOnlyList<PluginDefinition> Definitions { get; } = Array.AsReadOnly(new[] { definition });
        /// <summary>Verifies caller-provided ordering assertions before recording real source release.</summary>
        public ValueTask DisposeAsync()
        {
            BeforeDispose?.Invoke();
            Interlocked.Increment(ref Disposals);
            return ValueTask.CompletedTask;
        }
    }
}
