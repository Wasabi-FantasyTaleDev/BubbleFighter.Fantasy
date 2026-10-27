using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Kernel.Tests;

public sealed class PluginExecutionTests
{
    public interface IValue { int Number { get; } }
    private sealed record Value(int Number) : IValue;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly IReadOnlyDictionary<string, string> Versions = new Dictionary<string, string>
    { ["BubbleFighter.Fantasy.Abstractions"] = typeof(IPlugin).Assembly.GetName().Version!.ToString() };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Factory(Func<bool, CancellationToken, ValueTask<IPluginExecution>> acquire, string? key = null,
        Func<string?>? keySource = null) : IPluginExecutionFactory
    {
        public string? CoordinationKey => keySource is null ? key : keySource();
        public ValueTask<IPluginExecution> CreateAsync(bool reload, CancellationToken cancellationToken) => acquire(reload, cancellationToken);
    }
    private sealed class Execution(PluginAttribute declaration, Func<IPluginContext, CancellationToken, ValueTask> activate, Func<ValueTask>? close = null,
        IReadOnlyDictionary<string, string>? contractVersions = null) : IPluginExecution
    {
        internal int Closes;
        public PluginAttribute Declaration => declaration;
        public string Version => "1.0.0";
        public IReadOnlyDictionary<string, string> ContractVersions => contractVersions ?? Versions;
        public Type? ImplementationType => null;
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken) => activate(context, cancellationToken);
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Closes); return close?.Invoke() ?? ValueTask.CompletedTask; }
    }
    private static PluginRow Row(string id, int capacity = 16) => new()
    { Id = id, Plugin = id, Source = "backend-test", Lifecycle = new() { EffectCapacity = capacity } };
    private static PluginDescriptor Definition(PluginAttribute declaration, IPluginExecutionFactory factory) =>
        new(declaration, factory, "backend-test", "1.0.0", Versions);
    private static Factory Returns(IPluginExecution execution) => new((_, _) => ValueTask.FromResult(execution));

    [Fact]
    public async Task TypeFreeExecutionsServeDependenciesAndCloseAfterTheirPluginEffects()
    {
        var order = new List<string>(); var providerAlive = true;
        PluginContext? providerContext = null; PluginContext? consumerContext = null;
        var providerDeclaration = new PluginAttribute("provider") { Provides = [typeof(IValue)] };
        var consumerDeclaration = new PluginAttribute("consumer") { Requires = [typeof(IValue)] };
        var provider = new Execution(providerDeclaration, (context, _) =>
        {
            providerContext = Assert.IsType<PluginContext>(context);
            context.Effect(() => order.Add("provider-effect"));
            context.Services.Register<IValue>(new Value(9));
            return ValueTask.CompletedTask;
        }, () => { providerAlive = false; order.Add("provider-backend"); return ValueTask.CompletedTask; });
        var consumer = new Execution(consumerDeclaration, (context, _) =>
        {
            consumerContext = Assert.IsType<PluginContext>(context);
            Assert.Equal(9, context.Services.Get<IValue>().Number);
            context.Effect(() => { Assert.True(providerAlive); order.Add("consumer-effect"); });
            return ValueTask.CompletedTask;
        }, () => { Assert.True(providerAlive); order.Add("consumer-backend"); return ValueTask.CompletedTask; });
        var definition = Definition(providerDeclaration, Returns(provider));
        Assert.Null(definition.Type);
        var host = new PluginHost([Row("consumer", 1), Row("provider", 2)],
            PluginCatalog.FromDefinitions([definition, Definition(consumerDeclaration, Returns(consumer))]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(default).AsTask().WaitAsync(Limit);
            Assert.All(host.Instances, instance => Assert.Equal(PluginState.Active, instance.State));
            Assert.Equal(1, providerContext!.RetainedExecutionCount); Assert.Equal(1, consumerContext!.RetainedExecutionCount);
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
        Assert.Equal(["consumer-effect", "consumer-backend", "provider-effect", "provider-backend"], order);
        Assert.Equal(0, providerContext!.RetainedExecutionCount); Assert.Equal(0, consumerContext!.RetainedExecutionCount);
        Assert.Equal(1, provider.Closes); Assert.Equal(1, consumer.Closes);
    }

    [Fact]
    public async Task SupersededAcquisitionReleasesItsLateExecutionWithoutActivating()
    {
        var declaration = new PluginAttribute("late");
        var entered = Signal(); var release = Signal(); var activations = 0;
        var execution = new Execution(declaration, (_, _) => { activations++; return ValueTask.CompletedTask; });
        var factory = new Factory(async (_, _) => { entered.TrySetResult(); await release.Task; return execution; });
        var host = new PluginHost([Row("late")], PluginCatalog.FromDefinitions([Definition(declaration, factory)]), NullLoggerFactory.Instance);
        var first = host.RequestState("late", true); IPluginTransition? stopped = null;
        try
        {
            await entered.Task.WaitAsync(Limit);
            stopped = host.RequestState("late", false);
            Assert.Equal(PluginTransitionOutcome.Superseded, (await first.WaitAsync()).Outcome);
            Assert.False(stopped.IsCompleted); Assert.Equal(0, execution.Closes);
        }
        finally
        {
            release.TrySetResult();
            if (stopped is not null) await stopped.WaitAsync().AsTask().WaitAsync(Limit);
            await host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        }
        Assert.Equal(0, activations); Assert.Equal(1, execution.Closes);
        Assert.Equal(PluginState.Unloaded, Assert.Single(host.Instances).State);
    }

    [Fact]
    public async Task FailedActivationClosesItsEffectsBeforeItsBackend()
    {
        var order = new List<string>(); var declaration = new PluginAttribute("failed");
        PluginContext? saved = null;
        var execution = new Execution(declaration, (context, _) =>
        {
            saved = Assert.IsType<PluginContext>(context);
            context.Effect(() => order.Add("effect"));
            throw new InvalidOperationException("activation failed");
        }, () => { order.Add("backend"); return ValueTask.CompletedTask; });
        var host = new PluginHost([Row("failed", 1)], PluginCatalog.FromDefinitions([Definition(declaration, Returns(execution))]), NullLoggerFactory.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync("failed", default).AsTask().WaitAsync(Limit));
        await host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        Assert.Equal(["effect", "backend"], order); Assert.Equal(1, execution.Closes);
        Assert.Equal(0, saved!.RetainedEffectCount); Assert.Equal(0, saved.RetainedExecutionCount);
    }

    [Fact]
    public async Task UnknownPluginCleanupFailureRetainsItsBackendAndProviderLease()
    {
        var providerDeclaration = new PluginAttribute("provider") { Provides = [typeof(IValue)] };
        var consumerDeclaration = new PluginAttribute("consumer") { Requires = [typeof(IValue)] };
        PluginContext? providerContext = null; PluginContext? consumerContext = null;
        var provider = new Execution(providerDeclaration, (context, _) =>
        {
            providerContext = Assert.IsType<PluginContext>(context);
            context.Services.Register<IValue>(new Value(7)); return ValueTask.CompletedTask;
        });
        var consumer = new Execution(consumerDeclaration, (context, _) =>
        {
            consumerContext = Assert.IsType<PluginContext>(context);
            context.Effect(() => throw new InvalidOperationException("unknown cleanup")); return ValueTask.CompletedTask;
        });
        var host = new PluginHost([Row("provider"), Row("consumer")], PluginCatalog.FromDefinitions([
            Definition(providerDeclaration, Returns(provider)), Definition(consumerDeclaration, Returns(consumer))]), NullLoggerFactory.Instance);
        await host.StartAsync(default).AsTask().WaitAsync(Limit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.UnloadAsync("consumer", default).AsTask().WaitAsync(Limit));
        await Assert.ThrowsAsync<AggregateException>(() => host.ShutdownAsync(default).AsTask().WaitAsync(Limit));
        Assert.Equal(0, consumer.Closes); Assert.Equal(0, provider.Closes);
        Assert.Equal(1, consumerContext!.RetainedExecutionCount);
        Assert.Equal(1, providerContext!.NativeServices!.Activation.Publication.Dependents);
        Assert.Equal(7, consumerContext.Services.Get<IValue>().Number);
        Assert.Equal(PluginState.CleanupFailed, host.Instances.Single(instance => instance.RowId == "consumer").State);
    }

    [Fact]
    public async Task ABackendCannotChangeItsDefinitionContractsDuringAcquisition()
    {
        var declared = new PluginAttribute("drift") { Provides = [typeof(IValue)] };
        var resolved = new PluginAttribute("drift") { Provides = [typeof(IDisposable)] };
        var activated = false;
        var execution = new Execution(resolved, (_, _) => { activated = true; return ValueTask.CompletedTask; });
        var host = new PluginHost([Row("drift")], PluginCatalog.FromDefinitions([Definition(declared, Returns(execution))]), NullLoggerFactory.Instance);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync("drift", default).AsTask().WaitAsync(Limit));
        Assert.Contains("declared contracts", failure.ToString());
        Assert.False(activated); Assert.Equal(1, execution.Closes);
        await host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
    }

    [Fact]
    public async Task CoordinationKeysSerializeOnlyTheSharedBackend()
    {
        var firstDeclaration = new PluginAttribute("first");
        var secondDeclaration = new PluginAttribute("second");
        var independentDeclaration = new PluginAttribute("independent");
        var entered = Signal(); var release = Signal(); var secondAcquisitions = 0;
        var firstFactory = new Factory(async (_, _) =>
        {
            entered.TrySetResult(); await release.Task;
            return new Execution(firstDeclaration, (_, _) => ValueTask.CompletedTask);
        }, "shared-worker");
        var secondFactory = new Factory((_, _) =>
        {
            Interlocked.Increment(ref secondAcquisitions);
            return ValueTask.FromResult<IPluginExecution>(new Execution(secondDeclaration, (_, _) => ValueTask.CompletedTask));
        }, "shared-worker");
        var independent = new Execution(independentDeclaration, (_, _) => ValueTask.CompletedTask);
        var host = new PluginHost([Row("first"), Row("second"), Row("independent")], PluginCatalog.FromDefinitions([
            Definition(firstDeclaration, firstFactory), Definition(secondDeclaration, secondFactory),
            Definition(independentDeclaration, Returns(independent))]), NullLoggerFactory.Instance);
        var first = host.RequestState("first", true); IPluginTransition? second = null;
        try
        {
            await entered.Task.WaitAsync(Limit);
            second = host.RequestState("second", true);
            await host.RequestState("independent", true).WaitAsync().AsTask().WaitAsync(Limit);
            Assert.Equal(0, secondAcquisitions); Assert.False(second.IsCompleted); Assert.False(first.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync().AsTask().WaitAsync(Limit);
            if (second is not null) await second.WaitAsync().AsTask().WaitAsync(Limit);
            await host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
        }
        Assert.Equal(1, secondAcquisitions);
    }

    [Fact]
    public async Task BackendCleanupFailureKeepsItsPinAndDoesNotRetryDisposal()
    {
        var declaration = new PluginAttribute("backend-failure"); PluginContext? saved = null;
        var execution = new Execution(declaration, (context, _) =>
        { saved = Assert.IsType<PluginContext>(context); return ValueTask.CompletedTask; },
            () => throw new InvalidOperationException("backend cleanup failed"));
        var host = new PluginHost([Row(declaration.Id)], PluginCatalog.FromDefinitions([Definition(declaration, Returns(execution))]), NullLoggerFactory.Instance);
        await host.StartAsync(default).AsTask().WaitAsync(Limit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.UnloadAsync(declaration.Id, default).AsTask().WaitAsync(Limit));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.UnloadAsync(declaration.Id, default).AsTask().WaitAsync(Limit));
        Assert.Equal(1, execution.Closes); Assert.Equal(1, saved!.RetainedExecutionCount);
        Assert.Equal(PluginState.CleanupFailed, Assert.Single(host.Instances).State);
        await Assert.ThrowsAsync<AggregateException>(() => host.ShutdownAsync(default).AsTask().WaitAsync(Limit));
        Assert.Equal(1, execution.Closes);
    }

    [Fact]
    public async Task FailedFactoryAcquisitionDoesNotPreventACleanExplicitRetry()
    {
        var declaration = new PluginAttribute("retry"); var attempts = 0; PluginContext? saved = null;
        var execution = new Execution(declaration, (context, _) =>
        { saved = Assert.IsType<PluginContext>(context); return ValueTask.CompletedTask; });
        var factory = new Factory((_, _) =>
        {
            if (++attempts == 1) throw new IOException("acquisition failed");
            return ValueTask.FromResult<IPluginExecution>(execution);
        });
        var host = new PluginHost([Row(declaration.Id)], PluginCatalog.FromDefinitions([Definition(declaration, factory)]), NullLoggerFactory.Instance);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync(declaration.Id, default).AsTask().WaitAsync(Limit));
            Assert.Equal(PluginState.Failed, Assert.Single(host.Instances).State);
            await host.LoadAsync(declaration.Id, default).AsTask().WaitAsync(Limit);
            Assert.Equal(2, attempts); Assert.Equal(4, saved!.NativeServices!.Registry.RetainedMetadataCount);
            Assert.Equal(1, saved.RetainedExecutionCount);
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
        Assert.Equal(0, saved!.RetainedExecutionCount); Assert.Equal(1, execution.Closes);
    }

    [Fact]
    public async Task AResolvedBackendCannotRequireAnUnavailableHostContract()
    {
        var declaration = new PluginAttribute("future"); var activated = false;
        var execution = new Execution(declaration, (_, _) => { activated = true; return ValueTask.CompletedTask; },
            contractVersions: new Dictionary<string, string> { ["BubbleFighter.Fantasy.Abstractions"] = "999.0.0.0" });
        var host = new PluginHost([Row(declaration.Id)], PluginCatalog.FromDefinitions([Definition(declaration, Returns(execution))]), NullLoggerFactory.Instance);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync(declaration.Id, default).AsTask().WaitAsync(Limit));
        Assert.Contains("host provides", failure.ToString()); Assert.False(activated); Assert.Equal(1, execution.Closes);
        await host.ShutdownAsync(default).AsTask().WaitAsync(Limit);
    }

    [Fact]
    public void DeclarationAndVersionInputsCannotMutateAnExistingDefinition()
    {
        var declaration = new PluginAttribute("copied") { Provides = [typeof(IValue)] };
        var versions = new Dictionary<string, string> { ["test"] = "1.0.0" };
        var execution = new Execution(declaration, (_, _) => ValueTask.CompletedTask);
        var definition = new PluginDescriptor(declaration, Returns(execution), "test", "1.0.0", versions);
        declaration.Provides[0] = typeof(IDisposable); versions["test"] = "2.0.0";
        Assert.Equal(typeof(IValue), Assert.Single(definition.Declaration.Provides));
        definition.Declaration.Provides[0] = typeof(IAsyncDisposable);
        Assert.Equal(typeof(IValue), Assert.Single(definition.Declaration.Provides));
        Assert.Equal("1.0.0", definition.ContractVersions["test"]);
        var catalog = PluginCatalog.FromDefinitions([definition]);
        Assert.Throws<NotSupportedException>(() => ((IList<PluginDescriptor>)catalog.Plugins).Clear());
    }

    [Fact]
    public async Task ANullBackendExecutionFailsLoudlyWithoutLeakingItsActivationSlot()
    {
        var declaration = new PluginAttribute("null-backend"); var attempts = 0; PluginContext? saved = null;
        var execution = new Execution(declaration, (context, _) =>
        { saved = Assert.IsType<PluginContext>(context); return ValueTask.CompletedTask; });
        var factory = new Factory((_, _) => ++attempts == 1
            ? ValueTask.FromResult<IPluginExecution>(null!)
            : ValueTask.FromResult<IPluginExecution>(execution));
        var host = new PluginHost([Row(declaration.Id)], PluginCatalog.FromDefinitions([Definition(declaration, factory)]), NullLoggerFactory.Instance);
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync(declaration.Id, default).AsTask().WaitAsync(Limit));
            Assert.Contains("returned no resource", failure.ToString());
            Assert.Null(saved); Assert.Equal(PluginState.Failed, Assert.Single(host.Instances).State);
            await host.LoadAsync(declaration.Id, default).AsTask().WaitAsync(Limit);
            Assert.Equal(2, attempts); Assert.Equal(1, saved!.RetainedExecutionCount);
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
        Assert.Equal(0, saved!.RetainedExecutionCount); Assert.Equal(1, execution.Closes);
    }

    [Fact]
    public async Task ABackendCannotRenameItsDefinitionIdentityDuringAcquisition()
    {
        var declared = new PluginAttribute("stable") { Provides = [typeof(IValue)] };
        var renamed = new PluginAttribute("renamed") { Provides = [typeof(IValue)] };
        var consumerDeclaration = new PluginAttribute("consumer") { Requires = [typeof(IValue)] };
        var activated = false;
        var execution = new Execution(renamed, (_, _) => { activated = true; return ValueTask.CompletedTask; });
        var host = new PluginHost([Row(declared.Id), Row("consumer")], PluginCatalog.FromDefinitions([
            Definition(declared, Returns(execution)),
            Definition(consumerDeclaration, Returns(new Execution(consumerDeclaration, (_, _) => ValueTask.CompletedTask)))]), NullLoggerFactory.Instance);
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync(declared.Id, default).AsTask().WaitAsync(Limit));
            Assert.Contains("changed its declared contracts", failure.ToString());
            Assert.False(activated); Assert.Equal(1, execution.Closes);
            Assert.Equal(PluginState.Failed, host.Instances.Single(instance => instance.RowId == declared.Id).State);
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync("consumer", default).AsTask().WaitAsync(Limit));
            Assert.Contains($"cannot load; missing service(s): {typeof(IValue).FullName}", missing.ToString());
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
    }

    [Fact]
    public async Task ABackendCannotChangeItsCoordinationKeyDuringAcquisition()
    {
        var declaration = new PluginAttribute("drifting-key");
        var consumerDeclaration = new PluginAttribute("consumer") { Requires = [typeof(IValue)] };
        var acquired = false; var activated = false;
        var execution = new Execution(declaration, (_, _) => { activated = true; return ValueTask.CompletedTask; });
        var factory = new Factory((_, _) => { acquired = true; return ValueTask.FromResult<IPluginExecution>(execution); },
            keySource: () => acquired ? "other-worker" : "shared-worker");
        var host = new PluginHost([Row(declaration.Id), Row("consumer")], PluginCatalog.FromDefinitions([
            Definition(declaration, factory),
            Definition(consumerDeclaration, Returns(new Execution(consumerDeclaration, (_, _) => ValueTask.CompletedTask)))]), NullLoggerFactory.Instance);
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync(declaration.Id, default).AsTask().WaitAsync(Limit));
            Assert.Contains("changed its execution coordination key", failure.ToString());
            Assert.False(activated); Assert.Equal(1, execution.Closes);
            Assert.Equal(PluginState.Failed, host.Instances.Single(instance => instance.RowId == declaration.Id).State);
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync("consumer", default).AsTask().WaitAsync(Limit));
            Assert.Contains("cannot load; missing service", missing.ToString());
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
    }

    [Fact]
    public void ABlankCoordinationKeyIsRejectedAtDefinitionConstruction()
    {
        var declaration = new PluginAttribute("blank-key");
        var execution = new Execution(declaration, (_, _) => ValueTask.CompletedTask);
        var failure = Assert.Throws<ConfigException>(() => new PluginDescriptor(declaration,
            new Factory((_, _) => ValueTask.FromResult<IPluginExecution>(execution), "   "), "backend-test", "1.0.0", Versions));
        Assert.Contains("nonempty or omitted", failure.Message);
        var omitted = new PluginDescriptor(declaration,
            new Factory((_, _) => ValueTask.FromResult<IPluginExecution>(execution)), "backend-test", "1.0.0", Versions);
        Assert.Null(omitted.CoordinationKey);
    }

    [Fact]
    public void AnUnparseableDefinitionContractVersionFailsAtCatalogComposition()
    {
        var declaration = new PluginAttribute("unparseable-definition");
        var execution = new Execution(declaration, (_, _) => ValueTask.CompletedTask);
        var versions = new Dictionary<string, string> { ["BubbleFighter.Fantasy.Abstractions"] = "not-a-version" };
        var definition = new PluginDescriptor(declaration, Returns(execution), "backend-test", "1.0.0", versions);
        var failure = Assert.Throws<ConfigException>(() => PluginCatalog.FromDefinitions([definition]));
        Assert.Contains("invalid shared contract version 'not-a-version'", failure.Message);
    }

    [Fact]
    public async Task AnUnparseableExecutionContractVersionIsRejectedBeforeActivation()
    {
        var declaration = new PluginAttribute("unparseable-execution");
        var activated = false;
        var execution = new Execution(declaration, (_, _) => { activated = true; return ValueTask.CompletedTask; },
            contractVersions: new Dictionary<string, string> { ["BubbleFighter.Fantasy.Contracts"] = "2.x" });
        var host = new PluginHost([Row(declaration.Id)], PluginCatalog.FromDefinitions([Definition(declaration, Returns(execution))]), NullLoggerFactory.Instance);
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync(declaration.Id, default).AsTask().WaitAsync(Limit));
            Assert.Contains("invalid shared contract version '2.x'", failure.ToString());
            Assert.False(activated); Assert.Equal(1, execution.Closes);
            Assert.Equal(PluginState.Failed, Assert.Single(host.Instances).State);
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
    }

    [Fact]
    public void ADefinitionRequiringAnOlderSharedContractMajorIsRejected()
    {
        var declaration = new PluginAttribute("older-major");
        var abi = typeof(IPlugin).Assembly.GetName().Version!;
        var older = new Version(abi.Major - 1, 0, 0, 0).ToString();
        var definition = new PluginDescriptor(declaration, Returns(new Execution(declaration, (_, _) => ValueTask.CompletedTask)),
            "backend-test", "1.0.0", new Dictionary<string, string> { ["BubbleFighter.Fantasy.Abstractions"] = older });
        var failure = Assert.Throws<ConfigException>(() => PluginCatalog.FromDefinitions([definition]));
        Assert.Contains(older, failure.Message); Assert.Contains("host provides", failure.Message);
    }

    [Fact]
    public async Task AnExecutionRequiringAHigherMinorOfTheSameMajorIsRejected()
    {
        var declaration = new PluginAttribute("higher-minor");
        var abi = typeof(IPlugin).Assembly.GetName().Version!;
        var higher = new Version(abi.Major, abi.Minor + 1, 0, 0).ToString();
        var activated = false;
        var execution = new Execution(declaration, (_, _) => { activated = true; return ValueTask.CompletedTask; },
            contractVersions: new Dictionary<string, string> { ["BubbleFighter.Fantasy.Abstractions"] = higher });
        var host = new PluginHost([Row(declaration.Id)], PluginCatalog.FromDefinitions([Definition(declaration, Returns(execution))]), NullLoggerFactory.Instance);
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync(declaration.Id, default).AsTask().WaitAsync(Limit));
            Assert.Contains(higher, failure.ToString()); Assert.Contains("host provides", failure.ToString());
            Assert.False(activated); Assert.Equal(1, execution.Closes);
            Assert.Equal(PluginState.Failed, Assert.Single(host.Instances).State);
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
    }

    private static async Task<InvalidOperationException> AssertDeclarationRejectedAsync(PluginAttribute declaration)
    {
        var activated = false;
        var execution = new Execution(declaration, (_, _) => { activated = true; return ValueTask.CompletedTask; });
        var host = new PluginHost([Row(declaration.Id)], PluginCatalog.FromDefinitions([Definition(declaration, Returns(execution))]), NullLoggerFactory.Instance);
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.LoadAsync(declaration.Id, default).AsTask().WaitAsync(Limit));
            Assert.False(activated); Assert.Equal(1, execution.Closes);
            Assert.Equal(PluginState.Failed, Assert.Single(host.Instances).State);
            return failure;
        }
        finally { await host.ShutdownAsync(default).AsTask().WaitAsync(Limit); }
    }

    [Fact]
    public void DuplicateDefinitionIdentitiesAreRejectedAtCatalogComposition()
    {
        var declaration = new PluginAttribute("duplicate");
        var execution = new Execution(declaration, (_, _) => ValueTask.CompletedTask);
        var failure = Assert.Throws<ConfigException>(() => PluginCatalog.FromDefinitions([
            Definition(declaration, Returns(execution)), Definition(declaration, Returns(execution))]));
        Assert.Contains("Duplicate plugin id 'duplicate'", failure.Message);
    }

    [Theory]
    [InlineData("rewritten")]
    [InlineData("   ")]
    public void ARewrittenDefinitionIdentityIsRejectedAtCatalogComposition(string identity)
    {
        var declaration = new PluginAttribute("original");
        var execution = new Execution(declaration, (_, _) => ValueTask.CompletedTask);
        var tampered = Definition(declaration, Returns(execution)) with { Id = identity };
        var failure = Assert.Throws<ConfigException>(() => PluginCatalog.FromDefinitions([tampered]));
        Assert.Contains("must match its nonempty declaration identity", failure.Message);
    }

    [Fact]
    public void ANonInterfaceOptionalDeclarationIsRejectedBeforeHostConstruction()
    {
        var declaration = new PluginAttribute("non-interface") { Optional = [typeof(Value)] };
        var activated = false;
        var execution = new Execution(declaration, (_, _) => { activated = true; return ValueTask.CompletedTask; });
        var failure = Assert.Throws<ConfigException>(() => PluginCatalog.FromDefinitions([Definition(declaration, Returns(execution))]));
        Assert.Contains("non-interface", failure.Message); Assert.Contains("Optional[0]", failure.Message);
        Assert.Contains("must be a closed interface", failure.Message);
        Assert.False(activated); Assert.Equal(0, execution.Closes);
    }

    [Fact]
    public async Task ConflictingReadModesForOneDeclaredServiceAreRejected()
    {
        var declaration = new PluginAttribute("conflicting-reads") { Optional = [typeof(IValue)], OptionalMany = [typeof(IValue)] };
        var failure = await AssertDeclarationRejectedAsync(declaration);
        Assert.Contains("Duplicate read declaration", failure.ToString());
    }

    [Fact]
    public async Task DuplicatePublicationDeclarationsAreRejected()
    {
        var declaration = new PluginAttribute("duplicate-publication") { Provides = [typeof(IValue)], ProvidesMany = [typeof(IValue)] };
        var failure = await AssertDeclarationRejectedAsync(declaration);
        Assert.Contains("Duplicate publication declaration", failure.ToString());
    }
}
