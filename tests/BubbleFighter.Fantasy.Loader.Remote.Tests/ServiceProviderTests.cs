using System.Collections.Concurrent;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Exercises SDK providers through real hosted consumers and the native service registry.</summary>
[Collection("SDK compilation")]
public sealed class ServiceProviderTests
{
    private const long LargeId = 9007199254740993;

    /// <summary>Caller cancellation reaches the individual SDK callback without stopping its provider row.</summary>
    [Theory]
    [InlineData("python")]
    [InlineData("typescript")]
    [InlineData("go")]
    [InlineData("rust")]
    public async Task CallerCancellationSignalsActualProviderWorkAndAllowsAnotherCall(string language)
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = new RemoteHarness([Entry(language, false)], initialRows:
            [Row("provider", "conformance.provider", ("behavior", "wait")), Row("consumer", "test.sdk-provider-consumer", ("key", key))]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Loggers.OnEntry = entry =>
        {
            if (entry.Contains("provider-call-started", StringComparison.Ordinal)) entered.TrySetResult();
            if (entry.Contains("provider-call-cancelled", StringComparison.Ordinal)) cancelled.TrySetResult();
        };
        await StartAsync(host);
        var service = SdkProviderConsumer.States[key].Fighters;
        using var cancellation = new CancellationTokenSource();
        var call = service.ListByUserNumAsync(LargeId, cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(await service.ListByUserNumAsync(1, default));
        Assert.Equal(PluginState.Active, host.Instance("provider").State);
    }

    /// <summary>A null callback result cannot satisfy a non-nullable collection contract.</summary>
    [Fact]
    public async Task MalformedProviderResultFailsAtTheNativeCallBoundary()
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = new RemoteHarness([Entry("python", false)], initialRows:
            [Row("provider", "conformance.provider", ("behavior", "null")), Row("consumer", "test.sdk-provider-consumer", ("key", key))]);
        await StartAsync(host);
        await Assert.ThrowsAsync<JsonException>(() => SdkProviderConsumer.States[key].Fighters.ListByUserNumAsync(1, default).AsTask());
    }

    /// <summary>Failed SDK activation never publishes an incomplete or partially acquired provider projection.</summary>
    [Theory]
    [InlineData("undeclared")]
    [InlineData("wrong-mode")]
    [InlineData("incomplete")]
    [InlineData("rollback")]
    public async Task InvalidRegistrationsAndActivationRollbackLeaveConsumersPending(string behavior)
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = new RemoteHarness([Entry("python", false)], initialRows:
            [Row("provider", "conformance.provider", ("behavior", behavior)), Row("consumer", "test.sdk-provider-consumer", ("key", key))]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("Activation of row 'provider'", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Host.Instances, row => row.State == PluginState.Active);
        Assert.False(SdkProviderConsumer.States.ContainsKey(key));
    }

    /// <summary>Unregistering a service leaves a previously selected projection callable until its provider activation ends.</summary>
    [Fact]
    public async Task RetainedNativeProjectionSurvivesItsProvidersRegistrationDisposal()
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = new RemoteHarness([Entry("python", false)], initialRows:
            [Row("provider", "conformance.provider", ("behavior", "self-unregister")), Row("consumer", "test.sdk-provider-consumer", ("key", key))]);
        await StartAsync(host);
        var projection = SdkProviderConsumer.States[key].Policy;
        Assert.False(projection.ReplyAfterInventory);
        Assert.False(projection.ReplyAfterInventory);
        Assert.Equal(PluginState.Active, host.Instance("provider").State);
    }

    /// <summary>Interface validation distinguishes overload ids and requires every method exactly once.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("[\"Echo\"]")]
    [InlineData("[\"not-a-method\"]")]
    public void IncompleteOrAmbiguousProviderMethodListsAreRejected(string methods)
    {
        Assert.Throws<RpcException>(() => new RemoteServiceProvider(null!, typeof(IOverloadedProvider), "test", JsonSerializer.Deserialize<JsonElement>(methods)));
        var complete = JsonSerializer.SerializeToElement(ContractSchema.Methods(typeof(IOverloadedProvider)).Select(ContractSchema.MethodId));
        _ = new RemoteServiceProvider(null!, typeof(IOverloadedProvider), "test", complete);
    }

    /// <summary>A caller context is consumable through injection but needs its own representation for an SDK provider.</summary>
    [Fact]
    public void ProviderShapeDoesNotReplaceTheNativeCallersContextWithTheProvidersContext()
    {
        var operation = new ContractSchema().Operation(typeof(ICallerContextProvider).GetMethod("Read")!);
        Assert.Equal("supported", operation["rpc_shape"]!.GetValue<string>());
        Assert.Equal("unsupported", operation["provider_rpc_shape"]!.GetValue<string>());
        Assert.Contains("invocation-scoped", operation["provider_unsupported_reason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Throws<ConfigException>(() => RemoteServiceProvider.Validate(typeof(ICallerContextProvider)));
    }

    private interface IOverloadedProvider
    {
        int Echo(int value);
        string Echo(string value);
    }

    private interface ICallerContextProvider
    {
        int Read(IPluginContext caller);
    }

    /// <summary>A second SDK row resolves a provider registered by the first row in the same runtime.</summary>
    [Theory]
    [InlineData("python", false)]
    [InlineData("typescript", false)]
    [InlineData("go", false)]
    [InlineData("rust", false)]
    [InlineData("go", true)]
    [InlineData("rust", true)]
    public async Task SdkConsumerResolvesAnotherSdkRowsProvider(string language, bool wasm)
    {
        await using var host = new RemoteHarness([Entry(language, wasm)], loader: Loader(wasm), initialRows:
            [Row("provider", "conformance.provider"), Row("consumer", "conformance.provider-consumer")]);
        await StartAsync(host);
        Assert.True(host.Instance("consumer").State == PluginState.Active, string.Join("\n", host.Host.Instances) + "\n" + string.Join("\n", host.Loggers.Entries));
        Assert.Contains(host.Loggers.Entries, entry => entry.Contains("provider-consumer-ok", StringComparison.Ordinal));
    }

    /// <summary>Native getter and ValueTask callers round-trip records and exact int64 identities through each SDK.</summary>
    [Theory]
    [InlineData("python", false)]
    [InlineData("typescript", false)]
    [InlineData("go", false)]
    [InlineData("rust", false)]
    [InlineData("go", true)]
    [InlineData("rust", true)]
    public async Task NativeConsumersUseSdkGettersAndAsyncRepositories(string language, bool wasm)
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = new RemoteHarness([Entry(language, wasm)], loader: Loader(wasm), initialRows:
            [Row("provider", "conformance.provider"), Row("consumer", "test.sdk-provider-consumer", ("key", key))]);
        await StartAsync(host);
        Assert.Equal(PluginState.Active, host.Instance("provider").State);
        Assert.True(host.Instance("consumer").State == PluginState.Active, string.Join("\n", host.Host.Instances) + "\n" + string.Join("\n", host.Loggers.Entries));
        var consumer = SdkProviderConsumer.States[key];
        Assert.False(consumer.Policy.ReplyAfterInventory);
        Assert.Empty(await consumer.Fighters.ListByUserNumAsync(LargeId, default));
        var record = new FighterRecord(LargeId, 1, 13, 7, 300013, "provider-test");
        await consumer.Fighters.UpsertAsync(record, default);
        Assert.Equal(record, Assert.Single(await consumer.Fighters.ListByUserNumAsync(LargeId, default)));
        Assert.Empty(await consumer.Fighters.ListByUserNumAsync(LargeId - 1, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Host.UnloadAsync("provider", default).AsTask());
        await host.Host.UnloadAsync("consumer", default);
        await host.Host.UnloadAsync("provider", default);
        Assert.Equal(PluginState.Unloaded, host.Instance("provider").State);
        Assert.False(SdkProviderConsumer.States.ContainsKey(key));
    }

    /// <summary>Mixed native and SDK decorators invoke their selected inner layers in registry priority order.</summary>
    [Theory]
    [InlineData("python", false)]
    [InlineData("typescript", false)]
    [InlineData("go", false)]
    [InlineData("rust", false)]
    [InlineData("go", true)]
    [InlineData("rust", true)]
    public async Task NativeAndSdkDecoratorsCallTheirSelectedInnerServices(string language, bool wasm)
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = new RemoteHarness([Entry(language, wasm)], loader: Loader(wasm), initialRows:
        [
            Row("provider", "conformance.provider"),
            Row("inner", "conformance.decorator", ("priority", 5), ("operation", "negate")),
            Row("outer", "conformance.decorator", ("priority", 10), ("operation", "false")),
            Row("native", "test.sdk-provider-decorator"),
            Row("consumer", "test.sdk-provider-consumer", ("key", key)),
        ]);
        await StartAsync(host);
        foreach (var id in new[] { "provider", "inner", "outer", "native", "consumer" })
            Assert.Equal(PluginState.Active, host.Instance(id).State);
        Assert.True(SdkProviderConsumer.States[key].Policy.ReplyAfterInventory);
    }

    /// <summary>Many registrations expose descending priority with stable registration order for ties.</summary>
    [Theory]
    [InlineData("python", false)]
    [InlineData("typescript", false)]
    [InlineData("go", false)]
    [InlineData("rust", false)]
    [InlineData("go", true)]
    [InlineData("rust", true)]
    public async Task ManyProvidersPreservePriorityAndEqualPriorityRegistrationOrder(string language, bool wasm)
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = new RemoteHarness([Entry(language, wasm)], loader: Loader(wasm), initialRows:
        [
            Row("first", "conformance.provider-many", ("priority", 0), ("value", true)),
            Row("second", "conformance.provider-many", ("priority", 0), ("value", false)),
            Row("high", "conformance.provider-many", ("priority", 10), ("value", false)),
            Row("consumer", "test.sdk-many-consumer", ("key", key)),
        ]);
        await StartAsync(host);
        Assert.True(host.Instance("consumer").State == PluginState.Active, string.Join("\n", host.Host.Instances) + "\n" + string.Join("\n", host.Loggers.Entries));
        Assert.Equal(new[] { false, true, false }, SdkManyConsumer.States[key].Select(policy => policy.ReplyAfterInventory));
    }

    /// <summary>SDK consumers select native and remote many providers in the same priority order, then release the snapshot.</summary>
    [Theory]
    [InlineData("python", false)]
    [InlineData("typescript", false)]
    [InlineData("go", false)]
    [InlineData("rust", false)]
    [InlineData("go", true)]
    [InlineData("rust", true)]
    public async Task SdkManyConsumersUseTheNativeOrderedSelection(string language, bool wasm)
    {
        await using var host = new RemoteHarness([Entry(language, wasm)], loader: Loader(wasm), initialRows:
        [
            Row("native", "test.sdk-many-provider"),
            Row("low", "conformance.provider-many", ("priority", -1), ("value", false)),
            Row("high", "conformance.provider-many", ("priority", 10), ("value", false)),
            Row("consumer", "conformance.many-consumer"),
        ]);
        await StartAsync(host);
        Assert.Equal(PluginState.Active, host.Instance("consumer").State);
        Assert.Contains(host.Loggers.Entries, entry => entry.Contains("many-consumer-ok", StringComparison.Ordinal));
    }

    /// <summary>An optional extension activates with an empty collection on every supported backend.</summary>
    [Theory]
    [InlineData("python", false)]
    [InlineData("typescript", false)]
    [InlineData("go", false)]
    [InlineData("rust", false)]
    [InlineData("go", true)]
    [InlineData("rust", true)]
    public async Task OptionalManyConsumersAcceptAnAbsentProvider(string language, bool wasm)
    {
        await using var host = new RemoteHarness([Entry(language, wasm)], loader: Loader(wasm), initialRows:
            [Row("consumer", "conformance.optional-many-consumer")]);
        await StartAsync(host);
        Assert.Equal(PluginState.Active, host.Instance("consumer").State);
        Assert.Contains(host.Loggers.Entries, entry => entry.Contains("many-consumer-ok", StringComparison.Ordinal));
    }

    /// <summary>Required extension dependencies stay fail-loud when the loader publishes no matching provider.</summary>
    [Fact]
    public async Task RequiredManyConsumerCannotStartWithoutAProvider()
    {
        await using var host = new RemoteHarness([Entry("python", false)], initialRows:
            [Row("consumer", "conformance.many-consumer")]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("IEquipmentReplyPolicy", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Loggers.Entries, entry => entry.Contains("many-consumer-ok", StringComparison.Ordinal));
    }

    /// <summary>Provider declarations are v2-only and populate ordinary hosted service metadata.</summary>
    [Theory]
    [InlineData("provides", "IEquipmentReplyPolicy")]
    [InlineData("provides_many", "IEquipmentReplyPolicy")]
    [InlineData("decorates", "IEquipmentReplyPolicy")]
    public void ProviderDeclarationsRequireV2AndUseRealServiceMetadata(string mode, string service)
    {
        var json = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            { ["id"] = "test.provider", ["version"] = "1.0.0", [mode] = new[] { service } });
        Assert.Throws<ConfigException>(() => RemoteDefinition.Parse(json, "test", 1));
        var definition = RemoteDefinition.Parse(json, "test", 2);
        var types = mode switch
        {
            "provides" => definition.Declaration.Provides,
            "provides_many" => definition.Declaration.ProvidesMany,
            _ => definition.Declaration.Decorates,
        };
        Assert.Equal(typeof(IEquipmentReplyPolicy), Assert.Single(types));
    }

    internal static Dictionary<string, object?> Entry(string language, bool wasm)
    {
        if (language is "go" or "rust")
        {
            var binary = CompiledSdk.Providers(language, wasm);
            Assert.True(binary.Path is not null, binary.Skip);
            return new() { [wasm ? "module" : "command"] = binary.Path, ["source_id"] = "providers" };
        }
        return new()
        {
            ["command"] = language == "python" ? Python.Command : Node.Command,
            ["args"] = new List<object?> { Path.Combine(Python.RepoRoot, language == "python"
                ? "sdk/python/tests/provider_plugins.py" : "sdk/typescript/dist/tests/provider-plugins.js") },
            ["source_id"] = "providers",
        };
    }

    private static async Task StartAsync(RemoteHarness host)
    {
        try { await host.StartAsync(); }
        catch (Exception error)
        {
            throw new Xunit.Sdk.XunitException($"{error}\n{string.Join('\n', host.Loggers.Entries)}");
        }
    }

    private static string Loader(bool wasm) => wasm ? "loader.wasm" : "loader.process";

    internal static PluginRow Row(string id, string plugin, params (string Key, object? Value)[] config) => new()
    {
        Id = id, Plugin = plugin, Source = "test", ClientBuild = new ClientBuild("kr", 812, 0),
        Config = config.ToDictionary(pair => pair.Key, pair => pair.Value),
        Lifecycle = new() { EffectCapacity = 32, TeardownTimeoutMs = 10000 },
    };
}

/// <summary>Captures the native projections selected for one hosted consumer activation.</summary>
[Plugin("test.sdk-provider-consumer", Requires = [typeof(IEquipmentReplyPolicy), typeof(IFighterRepository)])]
public sealed class SdkProviderConsumer : IPlugin
{
    internal static ConcurrentDictionary<string, (IEquipmentReplyPolicy Policy, IFighterRepository Fighters)> States { get; } = new();

    /// <summary>Resolves both services and removes the test observation when the consumer unloads.</summary>
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var key = (string)context.Row.Config!["key"]!;
        States[key] = (context.Services.Get<IEquipmentReplyPolicy>(), context.Services.Get<IFighterRepository>());
        context.Effect(() => States.TryRemove(key, out _));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Captures the ordered extension providers selected for one hosted consumer activation.</summary>
[Plugin("test.sdk-many-consumer", RequiresMany = [typeof(IEquipmentReplyPolicy)])]
public sealed class SdkManyConsumer : IPlugin
{
    internal static ConcurrentDictionary<string, IReadOnlyList<IEquipmentReplyPolicy>> States { get; } = new();

    /// <summary>Resolves the entire many-service projection and owns its observation until unload.</summary>
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var key = (string)context.Row.Config!["key"]!;
        States[key] = context.Services.GetAll<IEquipmentReplyPolicy>().ToArray();
        context.Effect(() => States.TryRemove(key, out _));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Provides a native extension alongside the SDK implementations in ordered-selection tests.</summary>
[Plugin("test.sdk-many-provider", ProvidesMany = [typeof(IEquipmentReplyPolicy)])]
public sealed class SdkManyProvider : IPlugin
{
    /// <summary>Publishes one true-valued policy at the default priority.</summary>
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Effect(context.Services.RegisterMany<IEquipmentReplyPolicy>(new Policy()));
        return ValueTask.CompletedTask;
    }

    private sealed class Policy : IEquipmentReplyPolicy
    {
        public bool ReplyAfterInventory => true;
    }
}

/// <summary>Adds a native layer outside lower-priority SDK decorators.</summary>
[Plugin("test.sdk-provider-decorator", Decorates = [typeof(IEquipmentReplyPolicy)])]
public sealed class SdkProviderDecorator : IPlugin
{
    /// <summary>Registers an owned priority-20 policy that negates the selected inner value.</summary>
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Effect(context.Services.Decorate<IEquipmentReplyPolicy>(inner => new Policy(inner), 20));
        return ValueTask.CompletedTask;
    }

    private sealed class Policy(IEquipmentReplyPolicy inner) : IEquipmentReplyPolicy
    {
        public bool ReplyAfterInventory => !inner.ReplyAfterInventory;
    }
}
