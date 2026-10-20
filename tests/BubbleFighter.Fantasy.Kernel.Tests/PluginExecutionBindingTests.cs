using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

/// <summary>Exercises backend-neutral code revision binding and factory-free definition diagnostics through the real host.</summary>
public sealed class PluginExecutionBindingTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, string> Versions = new Dictionary<string, string>
    { ["BubbleFighter.Fantasy.Abstractions"] = typeof(IPlugin).Assembly.GetName().Version!.ToString() };

    /// <summary>Supplies a contract used to detect service declaration drift before an execution starts.</summary>
    public interface IValue
    {
        /// <summary>Gets a value that a valid provider could publish.</summary>
        int Number { get; }
    }

    /// <summary>A refreshed non-native revision remains the source for configuration recovery after a later activation fails.</summary>
    [Fact]
    public async Task RefreshedBindingSurvivesConfigurationFailureAndRetry()
    {
        var declaration = new PluginAttribute("bound");
        var oldFactory = new Factory("module");
        var newFactory = new Factory("module");
        var executions = new List<Execution>();
        var failNext = false;
        Execution Create(Factory factory, string version)
        {
            var execution = new Execution(declaration, () => factory, version)
            {
                Activate = _ =>
                {
                    if (failNext) { failNext = false; throw new InvalidOperationException("configuration activation failed"); }
                    return ValueTask.CompletedTask;
                },
            };
            executions.Add(execution);
            return execution;
        }
        oldFactory.Acquire = reload => Create(reload ? newFactory : oldFactory, reload ? "2.0.0" : "1.0.0");
        newFactory.Acquire = _ => Create(newFactory, "2.0.0");
        var row = Row(declaration.Id);
        var host = Host(row, declaration, oldFactory);
        try
        {
            await host.StartAsync(default).AsTask().WaitAsync(Limit);
            await host.ReloadAsync(row.Id, default).AsTask().WaitAsync(Limit);
            Assert.Equal("2.0.0", Assert.Single(host.Instances).Version);
            Assert.Equal([false, true], oldFactory.Acquisitions);
            Assert.Empty(newFactory.Acquisitions);
            failNext = true;
            var candidate = row with { Config = new Dictionary<string, object?> { ["value"] = 2 } };
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                host.ReconcileConfigurationAsync([candidate]).AsTask().WaitAsync(Limit));
            Assert.Contains("configuration activation failed", error.ToString());
            Assert.Equal(1, executions[2].Closes);
            await host.ReconcileConfigurationAsync([candidate]).AsTask().WaitAsync(Limit);
            Assert.Equal(PluginState.Active, Assert.Single(host.Instances).State);
            Assert.Equal("2.0.0", Assert.Single(host.Instances).Version);
            Assert.Equal([false, true], oldFactory.Acquisitions);
            Assert.Equal([false, false], newFactory.Acquisitions);
            Assert.All(executions, execution => Assert.Null(execution.ImplementationType));
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
        Assert.Equal(4, executions.Count);
        Assert.All(executions, execution => Assert.Equal(1, execution.Closes));
    }

    /// <summary>Invalid bindings are rejected before activation and their acquired executions are closed before a valid retry.</summary>
    [Theory]
    [InlineData("null-factory")]
    [InlineData("changed-key")]
    [InlineData("acquiring-key-drift")]
    [InlineData("blank-key")]
    [InlineData("changed-id")]
    [InlineData("changed-services")]
    [InlineData("incompatible-contract")]
    [InlineData("throwing-binding")]
    public async Task InvalidBindingRollsBackOwnedExecutionAndAllowsRetry(string failure)
    {
        var declaration = new PluginAttribute("invalid-binding");
        var original = new Factory("module");
        var bound = new Factory(failure == "changed-key" ? "different" : failure == "blank-key" ? " " : "module");
        var resolved = failure switch
        {
            "changed-id" => new PluginAttribute("renamed"),
            "changed-services" => new PluginAttribute(declaration.Id) { Provides = [typeof(IValue)] },
            _ => declaration,
        };
        var acquired = new Execution(resolved, () => failure switch
        {
            "null-factory" => null!,
            "throwing-binding" => throw new InvalidOperationException("binding getter failed"),
            _ => bound,
        });
        if (failure == "incompatible-contract")
        {
            var major = typeof(IPlugin).Assembly.GetName().Version!.Major;
            acquired.Contracts = new Dictionary<string, string>
            { ["BubbleFighter.Fantasy.Abstractions"] = $"{major + 1}.0.0.0" };
        }
        original.Acquire = _ =>
        {
            if (failure == "acquiring-key-drift") original.Key = "drifted";
            return acquired;
        };
        var host = Host(Row(declaration.Id), declaration, original);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                host.LoadAsync(declaration.Id, default).AsTask().WaitAsync(Limit));
            Assert.Contains(failure == "throwing-binding" ? "binding getter failed" : nameof(ConfigException), error.ToString());
            Assert.Equal(0, acquired.Starts);
            Assert.Equal(1, acquired.Closes);
            Assert.Equal(PluginState.Failed, Assert.Single(host.Instances).State);
            Assert.Empty(bound.Acquisitions);
            original.Key = "module";
            var valid = new Execution(declaration, () => original);
            original.Acquire = _ => valid;
            await host.LoadAsync(declaration.Id, default).AsTask().WaitAsync(Limit);
            Assert.Equal(2, original.Acquisitions.Count);
            Assert.Equal(1, valid.Starts);
            Assert.Equal(PluginState.Active, Assert.Single(host.Instances).State);
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
        Assert.Equal(1, acquired.Closes);
    }

    /// <summary>An opted-in factory must actually return binding metadata, and rejection closes its execution before retry.</summary>
    [Fact]
    public async Task OptedInFactoryReturningNoBindingIsRejectedAndCleanedUp()
    {
        var declaration = new PluginAttribute("missing-binding");
        var factory = new Factory("module");
        var execution = new Execution(declaration, () => factory);
        factory.Acquire = _ => new UnboundExecution(execution);
        var host = Host(Row(declaration.Id), declaration, factory);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                host.LoadAsync(declaration.Id, default).AsTask().WaitAsync(Limit));
            Assert.Contains("opted into execution binding but returned no binding", error.ToString());
            Assert.Equal(0, execution.Starts);
            Assert.Equal(1, execution.Closes);
            var valid = new Execution(declaration, () => factory);
            factory.Acquire = _ => valid;
            await host.LoadAsync(declaration.Id, default).AsTask().WaitAsync(Limit);
            Assert.Equal(1, valid.Starts);
            Assert.Equal(2, factory.Acquisitions.Count);
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
        Assert.Equal(1, execution.Closes);
    }

    /// <summary>Opting into binding does not allow an existing factory to change its accepted code identity in place.</summary>
    [Fact]
    public async Task ReturningTheSameFactoryStillRequiresImmutableCodeProvenance()
    {
        var declaration = new PluginAttribute("mutable-code");
        var factory = new Factory("module");
        var execution = new Execution(declaration, () => factory);
        factory.Acquire = _ =>
        {
            factory.Code = new PluginModuleCode("module", new object(), "binding-test");
            return execution;
        };
        var host = Host(Row(declaration.Id), declaration, factory);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                host.LoadAsync(declaration.Id, default).AsTask().WaitAsync(Limit));
            Assert.Contains("changed its factory code provenance", error.ToString());
            Assert.Equal(0, execution.Starts);
            Assert.Equal(1, execution.Closes);
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
    }

    /// <summary>Catalog diagnostics are read-only primitive snapshots and do not start any backend factory.</summary>
    [Fact]
    public async Task DefinitionDiagnosticsExposeCopiedMetadataWithoutExecutableReferences()
    {
        var declaration = new PluginAttribute("listed") { Description = "available definition", SupportedBuilds = ["kr:1256"] };
        var factory = new Factory(null);
        var host = new PluginHost([], PluginCatalog.FromDefinitions([Definition(declaration, factory)]), NullLoggerFactory.Instance);
        try
        {
            IPluginHost api = host;
            var snapshot = api.Definitions;
            var info = Assert.Single(snapshot);
            Assert.Equal(declaration.Id, info.Id);
            Assert.Equal(declaration.Description, info.Description);
            Assert.Equal("1.0.0", info.Version);
            Assert.Equal("binding-test", info.Origin);
            Assert.Equal(["kr:1256"], info.SupportedBuilds);
            Assert.Throws<NotSupportedException>(() => ((IList<PluginDefinitionInfo>)snapshot).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<string>)info.SupportedBuilds)[0] = "changed");
            declaration.SupportedBuilds[0] = "changed";
            Assert.Equal(["kr:1256"], Assert.Single(api.Definitions).SupportedBuilds);
            Assert.All(typeof(PluginDefinitionInfo).GetProperties(), property =>
                Assert.True(property.PropertyType == typeof(string) || property.PropertyType == typeof(IReadOnlyList<string>)));
            Assert.Empty(factory.Acquisitions);
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
    }

    /// <summary>A revoked source disappears from new definition diagnostics while previously captured metadata remains independent.</summary>
    [Fact]
    public async Task DefinitionDiagnosticsExcludeRevokedSourcesBeforeTheirBatchDrains()
    {
        var ownerDeclaration = new PluginAttribute("source.owner") { Requires = [typeof(IPluginHost)] };
        var ownerFactory = new Factory(null);
        var loadedFactory = new Factory(null);
        var loaded = new PluginAttribute("dynamic.definition");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batch = new Batch(new PluginDefinition(loaded, loadedFactory, "dynamic-origin", "1.0.0", Versions), closing, release);
        IPluginDefinitionRegistration? registration = null;
        var owner = new Execution(ownerDeclaration, () => ownerFactory)
        {
            Activate = async context =>
            {
                registration = await context.Services.Get<IPluginHost>().RegisterDefinitionSourceAsync(
                    context, "source", 1, [], _ => ValueTask.FromResult<IPluginDefinitionBatch>(batch));
            },
        };
        ownerFactory.Acquire = _ => owner;
        var row = Row(ownerDeclaration.Id) with { Lifecycle = new() { DefinitionReserve = 1, DefinitionSourceReserve = 1, EffectCapacity = 16 } };
        var host = Host(row, ownerDeclaration, ownerFactory);
        Task? retirement = null;
        try
        {
            await host.StartAsync(default).AsTask().WaitAsync(Limit);
            var accepted = host.Definitions;
            Assert.Contains(accepted, definition => definition.Id == loaded.Id);
            retirement = registration!.DisposeAsync().AsTask();
            await closing.Task.WaitAsync(Limit);
            Assert.False(retirement.IsCompleted);
            Assert.DoesNotContain(host.Definitions, definition => definition.Id == loaded.Id);
            Assert.Contains(accepted, definition => definition.Id == loaded.Id);
            Assert.Empty(loadedFactory.Acquisitions);
        }
        finally
        {
            release.TrySetResult();
            if (retirement is not null) await retirement.WaitAsync(Limit);
            await host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        }
    }

    private static PluginRow Row(string id) => new() { Id = id, Plugin = id, Source = "binding-test" };
    private static PluginDescriptor Definition(PluginAttribute declaration, IPluginExecutionFactory factory) =>
        new(declaration, factory, "binding-test", "1.0.0", Versions);
    private static PluginHost Host(PluginRow row, PluginAttribute declaration, IPluginExecutionFactory factory) =>
        new([row], PluginCatalog.FromDefinitions([Definition(declaration, factory)]), NullLoggerFactory.Instance);

    private sealed class Factory(string? key) : IPluginExecutionBindingFactory, IPluginModuleSource
    {
        internal Func<bool, IPluginExecution> Acquire { get; set; } = _ => throw new InvalidOperationException("No acquisition configured.");
        internal List<bool> Acquisitions { get; } = [];
        internal PluginModuleCode Code { get; set; } = new("binding-module", new object(), "binding-test");
        internal string? Key { get; set; } = key;
        /// <summary>Exposes the backend reservation identity checked on both sides of acquisition.</summary>
        public string? CoordinationKey => Key;
        /// <summary>Exposes the opaque code revision used by the host ownership ledger.</summary>
        public PluginModuleCode ModuleCode => Code;
        /// <summary>Records whether the host requested refresh before invoking the configured acquisition.</summary>
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken)
        {
            Acquisitions.Add(reload);
            return ValueTask.FromResult(Acquire(reload));
        }
    }

    private sealed class Execution(PluginAttribute declaration, Func<IPluginExecutionFactory> binding, string version = "1.0.0")
        : IPluginExecution, IPluginExecutionBinding
    {
        internal int Starts;
        internal int Closes;
        internal Func<IPluginContext, ValueTask> Activate { get; init; } = _ => ValueTask.CompletedTask;
        internal IReadOnlyDictionary<string, string> Contracts { get; set; } = Versions;
        /// <summary>Returns the immutable factory selected by this execution's resolved code revision.</summary>
        public IPluginExecutionFactory BoundFactory => binding();
        /// <summary>Returns the resolved declaration checked before any activation callback.</summary>
        public PluginAttribute Declaration => declaration;
        /// <summary>Reports the test execution's resolved release version.</summary>
        public string Version => version;
        /// <summary>Supplies the resolved shared ABI requirements for host validation.</summary>
        public IReadOnlyDictionary<string, string> ContractVersions => Contracts;
        /// <summary>Leaves native types absent so the tests exercise only backend-neutral contracts.</summary>
        public Type? ImplementationType => null;
        /// <summary>Counts entry into plugin code and applies the configured activation behavior.</summary>
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Starts);
            return Activate(context);
        }
        /// <summary>Counts real execution cleanup after activation or metadata validation failure.</summary>
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Closes); return ValueTask.CompletedTask; }
    }

    private sealed class UnboundExecution(Execution execution) : IPluginExecution
    {
        /// <summary>Preserves valid declaration metadata while intentionally omitting revision binding.</summary>
        public PluginAttribute Declaration => execution.Declaration;
        /// <summary>Preserves the wrapped execution's release version.</summary>
        public string Version => execution.Version;
        /// <summary>Preserves the wrapped execution's shared ABI requirements.</summary>
        public IReadOnlyDictionary<string, string> ContractVersions => execution.ContractVersions;
        /// <summary>Preserves the absent native implementation type.</summary>
        public Type? ImplementationType => execution.ImplementationType;
        /// <summary>Records activation if the host incorrectly accepts missing binding metadata.</summary>
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken) => execution.ActivateAsync(context, cancellationToken);
        /// <summary>Closes the real returned resource when binding validation rejects it.</summary>
        public ValueTask DisposeAsync() => execution.DisposeAsync();
    }

    private sealed class Batch(PluginDefinition definition, TaskCompletionSource closing, TaskCompletionSource release) : IPluginDefinitionBatch
    {
        /// <summary>Returns the single candidate definition owned by this source batch.</summary>
        public IReadOnlyList<PluginDefinition> Definitions { get; } = Array.AsReadOnly(new[] { definition });
        /// <summary>Retains the batch while a gate proves revoked definitions are already hidden.</summary>
        public async ValueTask DisposeAsync()
        {
            closing.TrySetResult();
            await release.Task;
        }
    }
}
