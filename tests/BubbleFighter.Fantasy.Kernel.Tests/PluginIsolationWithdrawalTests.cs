using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Exercises removal of source-owned contract metadata while unrelated builtin entries retain isolated service scopes.</summary>
public sealed class PluginIsolationWithdrawalTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, string> Versions = new Dictionary<string, string>();

    /// <summary>Represents a contract known to the catalog only through the dynamic metadata source.</summary>
    public interface IFoo { }

    /// <summary>Source retirement drains inherited builtin scopes before releasing a batch even when no source-backed executor exists.</summary>
    [Fact]
    public async Task UnrelatedContainerAndChildMustDrainBeforeSourceBatchRelease()
    {
        var scene = new Scene(typeof(IFoo));
        var childClosing = Signal(); var childRelease = Signal();
        var containerClosing = Signal(); var containerRelease = Signal();
        scene.Child.CloseEntered = childClosing; scene.Child.CloseGate = childRelease;
        scene.Container.CloseEntered = containerClosing; scene.Container.CloseGate = containerRelease;
        Task? retirement = null;
        try
        {
            await scene.StartAndAdmitAsync();
            Assert.Equal(0, SourceReaderCount(scene.Host));
            var oldContainer = Assert.Single(scene.Container.Contexts);
            var oldChild = Assert.Single(scene.Child.Contexts);
            retirement = scene.Registration!.DisposeAsync().AsTask();
            await childClosing.Task.WaitAsync(Limit);
            Assert.False(retirement.IsCompleted);
            Assert.Equal(0, scene.InitialBatch.Disposals);
            Assert.True(oldContainer.IsSuperseded); Assert.True(oldChild.IsSuperseded);
            Assert.Equal(0, scene.Container.ExecutionDisposals);
            childRelease.TrySetResult();
            await containerClosing.Task.WaitAsync(Limit);
            Assert.False(retirement.IsCompleted);
            Assert.Equal(0, scene.InitialBatch.Disposals);
            Assert.Equal(1, scene.Child.ExecutionDisposals);
            containerRelease.TrySetResult();
            await retirement.WaitAsync(Limit);
            await scene.ObservePendingAsync();
            Assert.Equal(1, scene.InitialBatch.Disposals);
            Assert.Equal(1, scene.Container.ExecutionDisposals);
            AssertUnavailable(scene, "container"); AssertUnavailable(scene, "child");
            Assert.Equal([scene.ContractName], Entry(scene.Host, "container").IsolatedServices);
            Assert.Equal(1, scene.Container.Acquisitions); Assert.Equal(1, scene.Child.Acquisitions);
            var restored = await scene.PublishAgainAsync(typeof(IFoo));
            await scene.Container.SecondStarted.Task.WaitAsync(Limit);
            await scene.Child.SecondStarted.Task.WaitAsync(Limit);
            await scene.Host.RequestState("child", true).WaitAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(PluginDefinitionSourceState.Published, restored.Info.State);
            Assert.Single(scene.Host.DefinitionSources);
            Assert.NotEqual(scene.Registration.Info.Revision, restored.Info.Revision);
            Assert.True(Entry(scene.Host, "container").IsNamespaceReady);
            Assert.True(Entry(scene.Host, "child").IsNamespaceReady);
            AssertIsolated(scene.Host, typeof(IFoo));
        }
        finally
        {
            childRelease.TrySetResult(); containerRelease.TrySetResult();
            if (retirement is not null) await ObserveCleanupAsync(retirement);
            await scene.ShutdownAsync();
        }
    }

    /// <summary>Unchanged unavailable names remain accepted while new unknown names fail, and explicitly clearing isolation restores builtin rows.</summary>
    [Fact]
    public async Task UnavailableConfigurationCanRemainOrBeRepairedWithoutOldCode()
    {
        var scene = new Scene(typeof(IFoo));
        try
        {
            await scene.StartAndAdmitAsync();
            await scene.Registration!.DisposeAsync().AsTask().WaitAsync(Limit);
            await scene.ObservePendingAsync();
            var beforeContainer = scene.Container.Acquisitions; var beforeChild = scene.Child.Acquisitions;
            var unchanged = await scene.Host.ReconcileConfigurationAsync(scene.Rows).AsTask().WaitAsync(Limit);
            Assert.Empty(unchanged.ChangedRowIds);
            AssertUnavailable(scene, "container"); AssertUnavailable(scene, "child");
            var invalid = scene.Rows.Select(row => row.Id == "container"
                ? row with { IsolatedServices = [scene.ContractName, "Unknown.Withdrawal.Contract"] } : row).ToArray();
            var failure = await Assert.ThrowsAsync<ConfigException>(() =>
                scene.Host.ReconcileConfigurationAsync(invalid).AsTask().WaitAsync(Limit));
            Assert.Contains("isolated_services", failure.Message);
            Assert.Equal([scene.ContractName], Entry(scene.Host, "container").IsolatedServices);
            await scene.ObservePendingAsync();
            Assert.Equal(beforeContainer, scene.Container.Acquisitions);
            Assert.Equal(beforeChild, scene.Child.Acquisitions);
            var repaired = scene.Rows.Select(row => row.Id == "container" ? row with { IsolatedServices = [] } : row).ToArray();
            await scene.Host.ReconcileConfigurationAsync(repaired).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginState.Active, scene.Info("container").State);
            Assert.Equal(PluginState.Active, scene.Info("child").State);
            Assert.True(Entry(scene.Host, "container").IsNamespaceReady);
            Assert.True(Entry(scene.Host, "child").IsNamespaceReady);
            Assert.Empty(Entry(scene.Host, "container").IsolatedServices);
            Assert.Empty(scene.Host.DefinitionSources);
            Assert.Equal(1, scene.InitialBatch.Disposals);
            Assert.Equal(2, scene.Container.Starts); Assert.Equal(2, scene.Child.Starts);
        }
        finally { await scene.ShutdownAsync(); }
    }

    /// <summary>Disabled rows preserve unavailable name metadata without retaining an uncreated activation or consuming a released source grant.</summary>
    [Fact]
    public async Task DisabledScopesDoNotBlockBatchCleanupOrBecomeEnabledOnRestoration()
    {
        var scene = new Scene(typeof(IFoo), disabled: true);
        try
        {
            await scene.StartAndAdmitAsync();
            Assert.Equal(0, scene.Container.Acquisitions); Assert.Equal(0, scene.Child.Acquisitions);
            await scene.Registration!.DisposeAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(1, scene.InitialBatch.Disposals);
            Assert.False(Entry(scene.Host, "container").IsNamespaceReady);
            Assert.False(Entry(scene.Host, "child").IsNamespaceReady);
            Assert.Equal([scene.ContractName], Entry(scene.Host, "container").IsolatedServices);
            Assert.Empty(Entry(scene.Host, "container").IsolatedContracts);
            Assert.Equal(PluginState.Unloaded, scene.Info("container").State);
            Assert.Equal(PluginState.Unloaded, scene.Info("child").State);
            await scene.PublishAgainAsync(typeof(IFoo));
            Assert.True(Entry(scene.Host, "container").IsNamespaceReady);
            Assert.True(Entry(scene.Host, "child").IsNamespaceReady);
            Assert.Equal(PluginState.Unloaded, scene.Info("container").State);
            Assert.Equal(PluginState.Unloaded, scene.Info("child").State);
            Assert.Equal(0, scene.Container.Acquisitions); Assert.Equal(0, scene.Child.Acquisitions);
            Assert.Single(scene.Host.DefinitionSources);
        }
        finally { await scene.ShutdownAsync(); }
    }

    /// <summary>Reappearing contract metadata must preserve an operator's disabled target rather than silently reenable its subtree.</summary>
    [Fact]
    public async Task ManualDisabledTargetSurvivesWithdrawalAndRestoration()
    {
        var scene = new Scene(typeof(IFoo));
        try
        {
            await scene.StartAndAdmitAsync();
            await scene.Host.RequestState("container", false).WaitAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(PluginState.Unloaded, scene.Info("container").State);
            await scene.Registration!.DisposeAsync().AsTask().WaitAsync(Limit);
            await scene.PublishAgainAsync(typeof(IFoo));
            var child = await scene.Host.RequestState("child", true).WaitAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(PluginTransitionOutcome.WaitingForDependencies, child.Outcome);
            Assert.Equal(PluginState.Unloaded, scene.Info("container").State);
            Assert.Equal(PluginState.Pending, scene.Info("child").State);
            Assert.Equal(1, scene.Container.Starts); Assert.Equal(1, scene.Child.Starts);
            Assert.Equal(1, scene.InitialBatch.Disposals);
        }
        finally { await scene.ShutdownAsync(); }
    }

    /// <summary>A restored contract with the same full name but a different type receives new isolation instead of retaining the old type or falling back to root.</summary>
    [Fact]
    public async Task DistinctTypeWithSameFullNameRestoresInheritedNamespace()
    {
        const string name = "Fixture.IsolationWithdrawal.IRevision";
        var original = ContractType(name); var replacement = ContractType(name);
        Assert.NotSame(original, replacement); Assert.Equal(original.FullName, replacement.FullName);
        var scene = new Scene(original);
        try
        {
            await scene.StartAndAdmitAsync();
            var before = Entry(scene.Host, "container").Scope.Resolve(original);
            AssertIsolated(scene.Host, original);
            await scene.Registration!.DisposeAsync().AsTask().WaitAsync(Limit);
            await scene.ObservePendingAsync();
            Assert.Empty(Entry(scene.Host, "container").IsolatedContracts);
            Assert.Contains(name, Entry(scene.Host, "child").UnavailableIsolatedServices);
            await scene.PublishAgainAsync(replacement);
            // SecondStarted fires inside the activation body; a new request then would legally supersede it.
            await scene.WaitActiveAsync("container", "child");
            var entry = Entry(scene.Host, "container");
            Assert.Same(replacement, Assert.Single(entry.IsolatedContracts));
            Assert.DoesNotContain(original, entry.IsolatedContracts);
            Assert.NotSame(before.Realm, entry.Scope.Resolve(replacement).Realm);
            AssertIsolated(scene.Host, replacement);
            Assert.True(Entry(scene.Host, "child").IsNamespaceReady);
            Assert.Empty(Entry(scene.Host, "child").UnavailableIsolatedServices);
            Assert.Equal(2, scene.Container.Starts); Assert.Equal(2, scene.Child.Starts);
        }
        finally { await scene.ShutdownAsync(); }
    }

    /// <summary>A source owner cannot isolate, directly or through an ancestor, a contract only its own source defines.</summary>
    [Fact]
    public async Task OwnerNamespaceCannotDependOnItsOwnSource()
    {
        var scene = new Scene(typeof(IFoo));
        try
        {
            await scene.StartAndAdmitAsync();
            var direct = scene.Rows.Select(row => row.Id == "owner" ? row with { IsolatedServices = [scene.ContractName] } : row).ToArray();
            var error = await Assert.ThrowsAsync<ConfigException>(() => scene.Host.ReconcileConfigurationAsync(direct).AsTask().WaitAsync(Limit));
            Assert.Contains("owns", error.Message);
            var inherited = scene.Rows.Select(row => row.Id == "owner" ? row with { ParentId = "container" } : row).ToArray();
            error = await Assert.ThrowsAsync<ConfigException>(() => scene.Host.ReconcileConfigurationAsync(inherited).AsTask().WaitAsync(Limit));
            Assert.Contains("owns", error.Message);
            Assert.Equal(PluginState.Active, scene.Info("owner").State);
            Assert.Equal(PluginDefinitionSourceState.Published, scene.Registration!.Info.State);
            Assert.Empty(Entry(scene.Host, "owner").IsolatedServices);
            await scene.Registration.DisposeAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(1, scene.InitialBatch.Disposals);
        }
        finally { await scene.ShutdownAsync(); }
    }

    /// <summary>Configuration published while a source retires is serialized with it instead of leaving the source in failed cleanup.</summary>
    [Fact]
    public async Task ConcurrentConfigurationDoesNotFailSourceRetirement()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var scene = new Scene(typeof(IFoo));
            try
            {
                await scene.StartAndAdmitAsync();
                var edited = scene.Rows.Select(row => row.Id == "child" ? row with { Disabled = true } : row).ToArray();
                var retirement = scene.Registration!.DisposeAsync().AsTask();
                var configuration = scene.Host.ReconcileConfigurationAsync(edited).AsTask();
                await retirement.WaitAsync(Limit);
                var configurationError = await Record.ExceptionAsync(() => configuration.WaitAsync(Limit));
                if (configurationError is not null) Assert.IsType<InvalidOperationException>(configurationError);
                Assert.Empty(scene.Host.DefinitionSources);
                Assert.Equal(1, scene.InitialBatch.Disposals);
                Assert.False(Entry(scene.Host, "container").IsNamespaceReady);
            }
            finally { await scene.ShutdownAsync(); }
        }
    }

    private static void AssertUnavailable(Scene scene, string id)
    {
        var entry = Entry(scene.Host, id); var info = scene.Info(id);
        Assert.False(entry.IsNamespaceReady);
        Assert.Contains(scene.ContractName, entry.UnavailableIsolatedServices);
        Assert.Equal(PluginState.Pending, info.State);
        Assert.NotNull(info.Error);
        Assert.Contains("isolat", info.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(scene.ContractName, info.Error);
    }
    private static void AssertIsolated(PluginHost host, Type contract)
    {
        var container = Entry(host, "container"); var child = Entry(host, "child");
        Assert.NotEqual(container.Scope.Runtime.RootScope.Resolve(contract), container.Scope.Resolve(contract));
        Assert.Equal(container.Scope.Resolve(contract), child.Scope.Resolve(contract));
    }
    private static PluginRuntimeEntry Entry(PluginHost host, string id)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var configuration = typeof(PluginHost).GetField("_configuration", flags)!.GetValue(host)!;
        return ((PluginEntryTree)configuration.GetType().GetProperty("Tree", flags)!.GetValue(configuration)!).Entries[id];
    }
    private static int SourceReaderCount(PluginHost host) =>
        ((IDictionary)typeof(PluginHost).GetField("_sourceReaders", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!).Count;
    private static Type ContractType(string fullName) => AssemblyBuilder.DefineDynamicAssembly(
        new AssemblyName("IsolationWithdrawal_" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run)
        .DefineDynamicModule("contracts").DefineType(fullName, TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract).CreateType()!;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task ObserveCleanupAsync(Task task)
    {
        var error = await Record.ExceptionAsync(() => task.WaitAsync(Limit));
        Assert.IsNotType<TimeoutException>(error);
    }
    private static PluginDescriptor Descriptor(Factory factory) => new(factory.Declaration, factory, "isolation-test", "1.0.0", Versions);
    private static PluginRow Row(string id, string plugin) => new()
    { Id = id, Plugin = plugin, Source = "isolation-test", Lifecycle = new() { EffectCapacity = 4 } };

    private sealed class Scene
    {
        internal Factory Container { get; } = new(new PluginAttribute("builtin.container"));
        internal Factory Child { get; } = new(new PluginAttribute("builtin.child"));
        internal string ContractName { get; }
        internal Batch InitialBatch { get; }
        internal PluginHost Host { get; }
        internal PluginRow[] Rows { get; }
        internal IPluginDefinitionRegistration? Registration;
        private IPluginContext? _ownerContext;

        internal Scene(Type contract, bool disabled = false)
        {
            ContractName = contract.FullName!; InitialBatch = ContractBatch(contract);
            var owner = new Factory(new PluginAttribute("source.owner") { Requires = [typeof(IPluginHost)] });
            owner.Activate = async context =>
            {
                _ownerContext = context;
                Registration = await context.Services.Get<IPluginHost>().RegisterDefinitionSourceAsync(
                    context, "contracts", 1, [], _ => ValueTask.FromResult<IPluginDefinitionBatch>(InitialBatch));
            };
            var ownerRow = Row("owner", owner.Declaration.Id) with
            { Lifecycle = new() { EffectCapacity = 8, EntryReserve = 2, ServiceMetadataReserve = 16, DefinitionSourceReserve = 1, DefinitionReserve = 1 } };
            Rows = [ownerRow, Row("container", Container.Declaration.Id) with { IsolatedServices = [ContractName], Disabled = disabled },
                Row("child", Child.Declaration.Id) with { ParentId = "container", Disabled = disabled }];
            Host = new([ownerRow], PluginCatalog.FromDefinitions([Descriptor(owner), Descriptor(Container), Descriptor(Child)]), NullLoggerFactory.Instance);
        }
        internal async Task StartAndAdmitAsync()
        {
            await Host.StartAsync(default).AsTask().WaitAsync(Limit);
            await Host.ReconcileConfigurationAsync(Rows).AsTask().WaitAsync(Limit);
        }
        internal async Task ObservePendingAsync()
        {
            var parent = await Host.RequestState("container", true).WaitAsync().AsTask().WaitAsync(Limit);
            var child = await Host.RequestState("child", true).WaitAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(PluginTransitionOutcome.WaitingForDependencies, parent.Outcome);
            Assert.Equal(PluginTransitionOutcome.WaitingForDependencies, child.Outcome);
        }
        internal Task<IPluginDefinitionRegistration> PublishAgainAsync(Type contract) => Host.RegisterDefinitionSourceAsync(
            _ownerContext!, "contracts", 1, [], _ => ValueTask.FromResult<IPluginDefinitionBatch>(ContractBatch(contract))).AsTask().WaitAsync(Limit);
        internal async Task WaitActiveAsync(params string[] ids)
        {
            var deadline = DateTime.UtcNow + Limit;
            while (!ids.All(id => Info(id).State == PluginState.Active))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"Rows did not become active: {string.Join(", ", ids)}.");
                await Task.Delay(5);
            }
        }
        internal PluginInstanceInfo Info(string id) => Host.Instances.Single(row => row.RowId == id);
        internal Task ShutdownAsync() => Host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        private static Batch ContractBatch(Type contract)
        {
            var declaration = new PluginAttribute("source.contract-metadata") { Provides = [contract] };
            var factory = new Factory(declaration) { RejectAcquisition = true };
            return new(new PluginDefinition(declaration, factory, "isolation-contract-source", "1.0.0", Versions));
        }
    }

    private sealed class Factory(PluginAttribute declaration) : IPluginExecutionFactory
    {
        internal PluginAttribute Declaration => declaration;
        internal int Acquisitions; internal int Starts; internal int EffectStops; internal int ExecutionDisposals;
        internal bool RejectAcquisition;
        internal Func<IPluginContext, ValueTask>? Activate;
        internal ConcurrentQueue<PluginContext> Contexts { get; } = new();
        internal TaskCompletionSource SecondStarted { get; } = Signal();
        internal TaskCompletionSource? CloseEntered; internal TaskCompletionSource? CloseGate;
        /// <summary>Leaves scope retention independent of backend coordination keys.</summary>
        public string? CoordinationKey => null;
        /// <summary>Counts real activations and refuses accidental creation of the metadata-only source definition.</summary>
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Acquisitions);
            if (RejectAcquisition) throw new InvalidOperationException("The metadata source definition must not be activated.");
            return ValueTask.FromResult<IPluginExecution>(new Execution(this));
        }
    }

    private sealed class Execution(Factory owner) : IPluginExecution
    {
        /// <summary>Preserves a builtin declaration that does not mention the withdrawn isolated interface.</summary>
        public PluginAttribute Declaration => owner.Declaration;
        /// <summary>Reports a stable neutral fixture release.</summary>
        public string Version => "1.0.0";
        /// <summary>Requires no additional shared ABI versions in this fixture.</summary>
        public IReadOnlyDictionary<string, string> ContractVersions => Versions;
        /// <summary>Separates scope ownership from CLR plugin implementation types.</summary>
        public Type? ImplementationType => null;
        /// <summary>Records the actual scoped activation and owns one observable cleanup effect.</summary>
        public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref owner.Starts);
            owner.Contexts.Enqueue(Assert.IsType<PluginContext>(context));
            context.Effect(() => Interlocked.Increment(ref owner.EffectStops));
            if (owner.Activate is { } activate) await activate(context);
            if (count == 2) owner.SecondStarted.TrySetResult();
        }
        /// <summary>Retains a scoped context behind an explicit gate until its real backend cleanup completes.</summary>
        public async ValueTask DisposeAsync()
        {
            owner.CloseEntered?.TrySetResult();
            if (owner.CloseGate is { } gate) await gate.Task;
            Interlocked.Increment(ref owner.ExecutionDisposals);
        }
    }

    private sealed class Batch(PluginDefinition definition) : IPluginDefinitionBatch
    {
        internal int Disposals;
        /// <summary>Introduces the isolated interface through exactly one dynamic source declaration.</summary>
        public IReadOnlyList<PluginDefinition> Definitions { get; } = Array.AsReadOnly(new[] { definition });
        /// <summary>Records when the real source batch becomes eligible for resource release.</summary>
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
    }
}
