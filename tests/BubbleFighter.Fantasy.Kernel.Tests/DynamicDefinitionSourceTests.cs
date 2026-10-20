using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Exercises definition-source ownership through hosted providers and real consumer lifecycle transitions.</summary>
public sealed class DynamicDefinitionSourceTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, string> Versions = new Dictionary<string, string>();

    /// <summary>An earlier idle period does not let source retirement bypass a later activation's blocked cleanup.</summary>
    [Fact]
    public async Task ReactivatedReaderMustDrainBeforeSourceBatchRelease()
    {
        var consumer = new ProbeFactory("dynamic.reactivated");
        var batch = new Batch(Definition(consumer));
        await using var harness = new Harness(batch);
        await harness.StartAsync();
        await harness.Host.ReconcileConfigurationAsync(harness.WithConsumer(consumer.Id)).AsTask().WaitAsync(Limit);
        await harness.Host.UnloadAsync("consumer", default).AsTask().WaitAsync(Limit);
        await harness.Host.LoadAsync("consumer", default).AsTask().WaitAsync(Limit);
        consumer.DisposeGate = Signal();
        consumer.DisposeEntered = Signal();
        try
        {
            var retirement = harness.Registration!.DisposeAsync().AsTask();
            await consumer.DisposeEntered.Task.WaitAsync(Limit);
            Assert.False(retirement.IsCompleted);
            Assert.Equal(0, batch.Disposals);
            Assert.Contains(harness.Api.DefinitionSources, source => source.SourceId == "dynamic-source");
            consumer.DisposeGate.TrySetResult();
            await retirement.WaitAsync(Limit);
            Assert.Equal(1, batch.Disposals);
            Assert.Equal(2, consumer.ExecutionDisposals);
        }
        finally { consumer.DisposeGate.TrySetResult(); }
    }

    /// <summary>Exhausting source revisions fails before allocating a coordination-key lease or invoking a producer.</summary>
    [Fact]
    public async Task RevisionOverflowLeavesNoKeyReservation()
    {
        await using var harness = new Harness();
        await harness.StartAsync();
        typeof(PluginHost).GetField("_definitionSourceRevision", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(harness.Host, long.MaxValue);
        var calls = 0;
        await Assert.ThrowsAsync<OverflowException>(async () =>
            await harness.Api.RegisterDefinitionSourceAsync(harness.Context!, "overflow", 1, ["overflow-key"], _ =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult<IPluginDefinitionBatch>(new Batch(Definition(new ProbeFactory("unused"))));
            }).AsTask().WaitAsync(Limit));
        Assert.Equal(0, calls);
        Assert.Empty(harness.Api.DefinitionSources);
        Assert.Empty(harness.Host.Modules.Snapshot());
    }

    /// <summary>A descriptor selected before complete source removal cannot bypass admission merely because its source lookup is now absent.</summary>
    [Fact]
    public async Task PreviouslySelectedDescriptorCannotAcquireAfterSourceRemoval()
    {
        var consumer = new ProbeFactory("dynamic.queued");
        await using var harness = new Harness(new Batch(Definition(consumer)));
        await harness.StartAsync();
        var candidate = harness.WithConsumer(consumer.Id);
        candidate[1] = candidate[1] with { Disabled = true };
        await harness.Host.ReconcileConfigurationAsync(candidate).AsTask().WaitAsync(Limit);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var catalog = (PluginCatalog)typeof(PluginHost).GetProperty("_catalog", flags)!.GetValue(harness.Host)!;
        var selected = catalog.Find(consumer.Id)!;
        var rows = (IReadOnlyList<PluginRow>)typeof(PluginHost).GetProperty("_rows", flags)!.GetValue(harness.Host)!;
        var row = rows.Single(item => item.Id == "consumer");
        await harness.Registration!.DisposeAsync().AsTask().WaitAsync(Limit);
        var activate = typeof(PluginHost).GetMethod("ActivateAsync", flags)!;
        var attempt = (ValueTask)activate.Invoke(harness.Host, [row, selected, CancellationToken.None, false])!;
        var error = await Record.ExceptionAsync(() => attempt.AsTask().WaitAsync(Limit));
        Assert.NotNull(error);
        Assert.Equal(0, consumer.Acquisitions);
        Assert.Equal(0, consumer.Starts);
    }

    /// <summary>A provider may publish during activation and its definitions subsequently activate in the same host.</summary>
    [Fact]
    public async Task HostedProviderPublishesAUsableDefinitionDuringActivation()
    {
        var consumer = new ProbeFactory("dynamic.consumer");
        var batch = new Batch(Definition(consumer));
        await using var harness = new Harness(batch);
        await harness.StartAsync();
        Assert.NotNull(harness.Registration);
        Assert.Contains(harness.Api.DefinitionSources, source => source.SourceId == "dynamic-source");
        await harness.Host.ReconcileConfigurationAsync(harness.WithConsumer(consumer.Id)).AsTask().WaitAsync(Limit);
        Assert.Equal(1, consumer.Starts);
        Assert.Equal(PluginState.Active, harness.Host.Instances.Single(row => row.RowId == "consumer").State);
        Assert.Equal(0, batch.Disposals);
    }

    /// <summary>Source revocation drains consumers and prevents a committed cache from authorizing another acquisition.</summary>
    [Fact]
    public async Task RevokedSourceCannotRestartThroughTheCommittedCache()
    {
        var consumer = new ProbeFactory("dynamic.consumer");
        var batch = new Batch(Definition(consumer));
        await using var harness = new Harness(batch);
        await harness.StartAsync();
        await harness.Host.ReconcileConfigurationAsync(harness.WithConsumer(consumer.Id)).AsTask().WaitAsync(Limit);
        await harness.Registration!.DisposeAsync().AsTask().WaitAsync(Limit);
        Assert.Equal(1, consumer.Stops);
        Assert.Equal(1, consumer.ExecutionDisposals);
        Assert.Equal(1, batch.Disposals);
        Assert.Contains(harness.Host.Instances, row => row.RowId == "consumer");
        var rejected = await Record.ExceptionAsync(() => harness.Host.LoadAsync("consumer", default).AsTask().WaitAsync(Limit));
        Assert.NotNull(rejected);
        Assert.Equal(1, consumer.Acquisitions);
        Assert.DoesNotContain(harness.Host.ModuleOwnership.Snapshot(), code => code.ModuleId == "code:dynamic.consumer");
    }

    /// <summary>Capacity is reserved before calling a producer that could allocate backend resources.</summary>
    [Fact]
    public async Task ExcessCapacityRejectsBeforeProducerInvocation()
    {
        await using var harness = new Harness();
        await harness.StartAsync();
        var calls = 0;
        var error = await Record.ExceptionAsync(async () =>
            await harness.Api.RegisterDefinitionSourceAsync(harness.Context!, "too-large", 100, [], _ =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult<IPluginDefinitionBatch>(new Batch(Definition(new ProbeFactory("extra"))));
            }).AsTask().WaitAsync(Limit));
        Assert.NotNull(error);
        Assert.Equal(0, calls);
        Assert.DoesNotContain(harness.Api.DefinitionSources, source => source.SourceId == "too-large");
    }

    /// <summary>A duplicate definition batch is wholly rejected and its produced resource remains owned until disposal.</summary>
    [Fact]
    public async Task DuplicateBatchHasNoVisiblePrefixAndDisposesItsResource()
    {
        await using var harness = new Harness();
        await harness.StartAsync();
        var batch = new Batch(Definition(new ProbeFactory("duplicate")), Definition(new ProbeFactory("duplicate")));
        var error = await Record.ExceptionAsync(() => harness.RegisterAsync("duplicate-source", batch, 2));
        Assert.NotNull(error);
        Assert.Equal(1, batch.Disposals);
        Assert.DoesNotContain(harness.Api.DefinitionSources, source => source.SourceId == "duplicate-source" && source.State == PluginDefinitionSourceState.Published);
        var configuration = await Record.ExceptionAsync(() => harness.Host.ReconcileConfigurationAsync(harness.WithConsumer("duplicate")).AsTask().WaitAsync(Limit));
        Assert.NotNull(configuration);
        Assert.DoesNotContain(harness.Host.Instances, row => row.RowId == "consumer");
    }

    /// <summary>A context from another host cannot donate its identity or lifetime to this host's source registry.</summary>
    [Fact]
    public async Task ForeignOwnerRejectsBeforeAcquiringResources()
    {
        await using var first = new Harness();
        await using var second = new Harness();
        await first.StartAsync();
        await second.StartAsync();
        var calls = 0;
        var error = await Record.ExceptionAsync(async () =>
            await first.Api.RegisterDefinitionSourceAsync(second.Context!, "foreign", 1, [], _ =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult<IPluginDefinitionBatch>(new Batch(Definition(new ProbeFactory("foreign"))));
            }).AsTask().WaitAsync(Limit));
        Assert.NotNull(error);
        Assert.Equal(0, calls);
    }

    /// <summary>A result acquired after source retirement is disposed without running its activation callback.</summary>
    [Fact]
    public async Task LateAcquisitionCannotPublishAfterSourceRevocation()
    {
        var consumer = new ProbeFactory("dynamic.late") { AcquireGate = Signal() };
        var batch = new Batch(Definition(consumer));
        await using var harness = new Harness(batch);
        await harness.StartAsync();
        try
        {
            var convergence = harness.Host.ReconcileConfigurationAsync(harness.WithConsumer(consumer.Id)).AsTask();
            await consumer.Entered.Task.WaitAsync(Limit);
            var retiring = harness.Registration!.DisposeAsync().AsTask();
            await UntilAsync(() => harness.Registration.Info.State is PluginDefinitionSourceState.Retiring or PluginDefinitionSourceState.Retired);
            Assert.False(retiring.IsCompleted);
            consumer.AcquireGate.TrySetResult();
            _ = await Record.ExceptionAsync(() => convergence.WaitAsync(Limit));
            await retiring.WaitAsync(Limit);
            Assert.Equal(0, consumer.Starts);
            Assert.Equal(1, consumer.ExecutionDisposals);
            Assert.Equal(1, batch.Disposals);
        }
        finally { consumer.AcquireGate.TrySetResult(); }
    }

    /// <summary>Owner shutdown drains dependent entries before releasing the source batch and does not wait on itself.</summary>
    [Fact]
    public async Task OwnerShutdownDrainsSourceConsumersBeforeBatchRelease()
    {
        var consumer = new ProbeFactory("dynamic.consumer");
        var batch = new Batch(Definition(consumer)) { BeforeDispose = () => Assert.Equal(1, consumer.Stops) };
        await using var harness = new Harness(batch);
        await harness.StartAsync();
        await harness.Host.ReconcileConfigurationAsync(harness.WithConsumer(consumer.Id)).AsTask().WaitAsync(Limit);
        await harness.Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        Assert.Equal(1, batch.Disposals);
        Assert.Equal(1, consumer.ExecutionDisposals);
    }

    /// <summary>A startup row bound to a plugin that a source row publishes during its activation waits Pending, then activates.</summary>
    [Fact]
    public async Task StartupRowActivatesOnceItsSourcePublishes()
    {
        var consumer = new ProbeFactory("dynamic.startup");
        await using var harness = new Harness(new Batch(Definition(consumer)), ConsumerRow(consumer.Id));
        await harness.StartAsync();
        await UntilAsync(() => harness.Host.Instances.Single(row => row.RowId == "consumer").State == PluginState.Active);
        Assert.Equal(1, consumer.Starts);
    }

    /// <summary>A native startup consumer can depend on a service whose implementation is published by a loader during boot.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupDependencyWaitsForADynamicallyPublishedProvider(bool sourceFirst)
    {
        var providerDeclaration = new PluginAttribute("dynamic.service") { Provides = [typeof(IStartupDependency)] };
        var provider = new PluginDefinition(providerDeclaration, new OwnerFactory(providerDeclaration, context =>
        {
            context.Effect(context.Services.Register<IStartupDependency>(new StartupDependency()));
            return ValueTask.CompletedTask;
        }), "dynamic-source-test", "1.0.0", Versions);
        var batch = new Batch(provider);
        var ownerDeclaration = new PluginAttribute("source.owner") { Requires = [typeof(IPluginHost)] };
        var owner = new PluginDescriptor(ownerDeclaration, new OwnerFactory(ownerDeclaration, async context =>
        {
            await context.Services.Get<IPluginHost>().RegisterDefinitionSourceAsync(context, "startup-source", 1, [],
                _ => ValueTask.FromResult<IPluginDefinitionBatch>(batch));
        }), "dynamic-source-test", "1.0.0", Versions);
        var observed = false;
        var consumerDeclaration = new PluginAttribute("native.consumer") { Requires = [typeof(IStartupDependency)] };
        var consumer = new PluginDescriptor(consumerDeclaration, new OwnerFactory(consumerDeclaration, context =>
        {
            observed = context.Services.Get<IStartupDependency>().Ready;
            return ValueTask.CompletedTask;
        }), "dynamic-source-test", "1.0.0", Versions);
        var ownerRow = new PluginRow
        {
            Id = "owner", Plugin = "source.owner", Source = "dynamic-source-test",
            Lifecycle = new() { DefinitionSourceReserve = 1, DefinitionReserve = 1, ServiceMetadataReserve = 16, EffectCapacity = 16 },
        };
        var providerRow = ConsumerRow(providerDeclaration.Id) with { Id = "provider" };
        var consumerRow = ConsumerRow(consumerDeclaration.Id);
        PluginRow[] rows = sourceFirst ? [ownerRow, consumerRow, providerRow] : [consumerRow, providerRow, ownerRow];
        var host = new PluginHost(rows, PluginCatalog.FromDefinitions([owner, consumer]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(default).AsTask().WaitAsync(Limit);
            Assert.True(observed);
            Assert.All(host.Instances, row => Assert.Equal(PluginState.Active, row.State));
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
        Assert.Equal(1, batch.Disposals);
    }

    private interface IStartupDependency { bool Ready { get; } }
    private sealed class StartupDependency : IStartupDependency { public bool Ready => true; }

    /// <summary>A configuration that adds a source row and a row for the plugin it publishes converges with both active.</summary>
    [Fact]
    public async Task ConfigurationAddingASourceAndItsConsumerConverges()
    {
        var consumer = new ProbeFactory("dynamic.configured");
        await using var harness = new Harness(new Batch(Definition(consumer)), ownerAtStartup: false);
        await harness.StartAsync();
        var result = await harness.Host.ReconcileConfigurationAsync(harness.WithConsumer(consumer.Id)).AsTask().WaitAsync(Limit);
        Assert.Equal(PluginTransitionOutcome.Converged, result.Outcome);
        Assert.All(harness.Host.Instances, row => Assert.Equal(PluginState.Active, row.State));
        Assert.Equal(1, consumer.Starts);
    }

    /// <summary>Without any row that reserves definition sources, an unknown plugin still fails startup.</summary>
    [Fact]
    public async Task UnknownPluginWithoutASourceRowFailsStartup()
    {
        var host = new PluginHost([ConsumerRow("dynamic.missing")], PluginCatalog.FromDefinitions([]), NullLoggerFactory.Instance);
        var error = await Assert.ThrowsAsync<ConfigException>(() => host.StartAsync(default).AsTask().WaitAsync(Limit));
        Assert.Contains("unknown plugin 'dynamic.missing'", error.Message);
    }

    private static PluginRow ConsumerRow(string plugin) => new()
    {
        Id = "consumer", Plugin = plugin, Source = "dynamic-source-test",
        Lifecycle = new() { EffectCapacity = 16, TeardownTimeoutMs = 10000 },
    };

    private static PluginDefinition Definition(ProbeFactory factory) =>
        new(new PluginAttribute(factory.Id), factory, "dynamic-source-test", "1.0.0", Versions);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(Limit);
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal PluginContext? Context;
        internal IPluginDefinitionRegistration? Registration;
        internal PluginRow OwnerRow { get; } = new()
        {
            Id = "owner", Plugin = "source.owner", Source = "dynamic-source-test",
            Lifecycle = new()
            {
                EntryReserve = 3, ServiceMetadataReserve = 64, EffectCapacity = 64,
                DefinitionSourceReserve = 2, DefinitionReserve = 4, TeardownTimeoutMs = 10000,
            },
        };
        internal PluginHost Host { get; }
        internal IPluginHost Api => Host;

        internal Harness(Batch? initial = null, params PluginRow[] startupRows) : this(initial, true, startupRows) { }

        internal Harness(Batch? initial, bool ownerAtStartup, params PluginRow[] startupRows)
        {
            var declaration = new PluginAttribute("source.owner") { Requires = [typeof(IPluginHost)] };
            var factory = new OwnerFactory(declaration, async context =>
            {
                Context = Assert.IsType<PluginContext>(context);
                if (initial is not null)
                    Registration = await context.Services.Get<IPluginHost>().RegisterDefinitionSourceAsync(
                        context, "dynamic-source", initial.Definitions.Count, [], _ => ValueTask.FromResult<IPluginDefinitionBatch>(initial));
            });
            // A deferred owner is admitted later by configuration, as the server admits profile rows after bootstrap.
            var plan = ownerAtStartup ? new PluginStartupPlan([OwnerRow, .. startupRows], []) : new PluginStartupPlan(startupRows, [OwnerRow]);
            Host = new PluginHost(plan, PluginCatalog.FromDefinitions(
                [new PluginDescriptor(declaration, factory, "dynamic-source-test", "1.0.0", Versions)]), NullLoggerFactory.Instance);
        }

        internal Task StartAsync() => Host.StartAsync(default).AsTask().WaitAsync(Limit);
        internal PluginRow[] WithConsumer(string plugin) =>
            [OwnerRow, ConsumerRow(plugin)];
        internal async Task<IPluginDefinitionRegistration> RegisterAsync(string id, Batch batch, int capacity) =>
            await Api.RegisterDefinitionSourceAsync(Context!, id, capacity, [], _ => ValueTask.FromResult<IPluginDefinitionBatch>(batch)).AsTask().WaitAsync(Limit);
        /// <summary>Joins real host teardown within the same bounded test deadline.</summary>
        public ValueTask DisposeAsync() => new(Host.ShutdownAsync(default).AsTask().WaitAsync(Limit));
    }

    private sealed class Batch(params PluginDefinition[] definitions) : IPluginDefinitionBatch
    {
        internal int Disposals;
        internal Action? BeforeDispose;
        /// <summary>Supplies the complete candidate batch to the real host.</summary>
        public IReadOnlyList<PluginDefinition> Definitions { get; } = Array.AsReadOnly(definitions);
        /// <summary>Records resource release once and checks the required consumer-first teardown order.</summary>
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref Disposals) != 1) throw new InvalidOperationException("The source batch was disposed more than once.");
            BeforeDispose?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ProbeFactory(string id) : IPluginExecutionFactory, IPluginModuleSource
    {
        internal string Id { get; } = id;
        internal int Acquisitions;
        internal int Starts;
        internal int Stops;
        internal int ExecutionDisposals;
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource? AcquireGate;
        internal TaskCompletionSource? DisposeGate;
        internal TaskCompletionSource? DisposeEntered;
        /// <summary>Allows independent source and row coordination without a shared backend key.</summary>
        public string? CoordinationKey => null;
        /// <summary>Attributes this test backend without claiming native code collection.</summary>
        public PluginModuleCode ModuleCode { get; } = new("code:" + id, new object(), "dynamic-source-test");
        /// <summary>Optionally returns late to exercise retirement fencing and owned cleanup.</summary>
        public async ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Acquisitions);
            Entered.TrySetResult();
            if (AcquireGate is { } gate) await gate.Task;
            return new ProbeExecution(this);
        }
    }

    private sealed class ProbeExecution(ProbeFactory owner) : IPluginExecution
    {
        /// <summary>Uses the backend-neutral execution path without a native plugin class.</summary>
        public Type? ImplementationType => null;
        /// <summary>Uses the same definition identity as the admitted source.</summary>
        public PluginAttribute Declaration => new(owner.Id);
        /// <summary>Supplies a stable backend release version.</summary>
        public string Version => "1.0.0";
        /// <summary>Requires no additional versioned contracts in this neutral fixture.</summary>
        public IReadOnlyDictionary<string, string> ContractVersions => Versions;
        /// <summary>Runs only if the source is still authorized and owns one observable effect.</summary>
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref owner.Starts);
            context.Effect(() => Interlocked.Increment(ref owner.Stops));
            return ValueTask.CompletedTask;
        }
        /// <summary>Records cleanup of successful and rejected executors.</summary>
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref owner.ExecutionDisposals);
            owner.DisposeEntered?.TrySetResult();
            if (owner.DisposeGate is { } gate) await gate.Task;
        }
    }

    private sealed class OwnerFactory(PluginAttribute declaration, Func<IPluginContext, ValueTask> activate) : IPluginExecutionFactory
    {
        /// <summary>Uses no shared backend coordination key for the source provider.</summary>
        public string? CoordinationKey => null;
        /// <summary>Creates a source-provider execution using the same host-public contract as ordinary plugins.</summary>
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IPluginExecution>(new OwnerExecution(declaration, activate));
    }

    private sealed class OwnerExecution(PluginAttribute declaration, Func<IPluginContext, ValueTask> activate) : IPluginExecution
    {
        /// <summary>Uses a backend-neutral source-provider execution.</summary>
        public Type? ImplementationType => null;
        /// <summary>Declares access to the host capability used for source publication.</summary>
        public PluginAttribute Declaration => declaration;
        /// <summary>Reports the fixture release version.</summary>
        public string Version => "1.0.0";
        /// <summary>Requires no extra versioned contracts.</summary>
        public IReadOnlyDictionary<string, string> ContractVersions => Versions;
        /// <summary>Publishes definitions from inside a genuine hosted activation when requested.</summary>
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken) => activate(context);
        /// <summary>Leaves source resource cleanup to the context's real ownership scopes.</summary>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
