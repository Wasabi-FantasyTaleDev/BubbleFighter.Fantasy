using System.Collections.Concurrent;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using Xunit;
using static BubbleFighter.Fantasy.Loader.Remote.Tests.ServiceProviderTests;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

/// <summary>Exercises SDK listeners and publishers through the existing hosted native event bus.</summary>
[Collection("SDK compilation")]
public sealed class EventBridgeTests
{
    private static readonly EquipmentSelectionApplying Event = new("test/game", 9007199254740993, 13, true);

    /// <summary>All execution paths preserve notification modes, exact integers, mixed middleware and bail semantics.</summary>
    [Theory]
    [InlineData("python", false)]
    [InlineData("typescript", false)]
    [InlineData("go", false)]
    [InlineData("rust", false)]
    [InlineData("go", true)]
    [InlineData("rust", true)]
    public async Task NativeAndSdkDispatchShareAllModesAndReentrantServices(string language, bool wasm)
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Host(language, wasm, key, Row("listener", "conformance.events"));
        await Start(host);
        var bus = SdkEventProbe.Contexts[key].Events;
        var sync = 0;
        var async = 0;
        using var nativeSync = bus.On<EquipmentSelectionApplying>(_ => sync++);
        using var nativeAsync = bus.OnAsync<EquipmentSelectionApplying>((_, _) => { async++; return ValueTask.CompletedTask; });
        bus.Emit(Event);
        Assert.Equal(1, sync);
        Assert.Equal(0, async);
        Assert.Equal(1, Count(host, "event:listener:sync"));
        Assert.Equal(0, Count(host, "event:listener:async"));
        await bus.SerialAsync(Event, default);
        await bus.ParallelAsync(Event, default);
        Assert.Equal(3, sync);
        Assert.Equal(2, async);
        Assert.Equal(3, Count(host, "event:listener:sync"));
        Assert.Equal(2, Count(host, "event:listener:async"));

        using var nativeAround = bus.Intercept<EquipmentSelectionApplying, int>(async (_, next, ct) => 10 * await next(ct), 10);
        Assert.Equal(110, await bus.WaterfallAsync<EquipmentSelectionApplying, int>(Event, _ => ValueTask.FromResult(10), default));
        using var nativeBail = bus.OnBail<EquipmentSelectionApplying, string>(_ => "native", -10);
        Assert.Equal("native", bus.Bail<EquipmentSelectionApplying, string>(Event));
        Assert.Equal(1, Count(host, "event:listener:bail"));
        await host.Host.UnloadAsync("listener", default);
        Assert.Equal(100, await bus.WaterfallAsync<EquipmentSelectionApplying, int>(Event, _ => ValueTask.FromResult(10), default));
        Assert.Equal(PluginState.Unloaded, host.Instance("listener").State);
    }

    /// <summary>A publisher in each SDK runs native listeners and an SDK listener, including its scoped terminal.</summary>
    [Theory]
    [InlineData("python", false)]
    [InlineData("typescript", false)]
    [InlineData("go", false)]
    [InlineData("rust", false)]
    [InlineData("go", true)]
    [InlineData("rust", true)]
    public async Task SdkPublishesThroughNativeDispatchAndRetainsItsTerminal(string language, bool wasm)
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Host(language, wasm, key,
            Row("listener", "conformance.events"),
            Row("publisher", "conformance.events", ("modes", Array.Empty<string>()), ("publisher", true)));
        await Start(host);
        var bus = SdkEventProbe.Contexts[key].Events;
        var sync = 0;
        var async = 0;
        using var nativeSync = bus.On<EquipmentSelectionApplying>(value => { Assert.Equal(Event, value); sync++; });
        using var nativeAsync = bus.OnAsync<EquipmentSelectionApplying>((_, _) => { async++; return ValueTask.CompletedTask; });
        using var nativeAround = bus.Intercept<EquipmentSelectionApplying, int>(async (_, next, ct) => 10 * await next(ct), 10);
        using var nativeBail = bus.OnBail<EquipmentSelectionApplying, string>(_ => "native", -10);
        foreach (var mode in new[] { "emit", "serial", "parallel", "waterfall", "bail" })
            Assert.True(await host.DispatchLoginAsync(mode));
        Assert.Equal(3, sync);
        Assert.Equal(2, async);
        Assert.Equal(1, Count(host, "published:waterfall:1010"));
        Assert.Contains(host.Loggers.Entries, entry => entry.Contains("published:bail:", StringComparison.Ordinal) && entry.Contains("native", StringComparison.Ordinal));
    }

    /// <summary>Native service dependencies resolve identically when the provider and listener use different languages.</summary>
    [Theory]
    [InlineData("python", "typescript")]
    [InlineData("typescript", "go")]
    [InlineData("go", "rust")]
    [InlineData("rust", "python")]
    public async Task EventCallbackResolvesAServiceFromAnotherLanguage(string listener, string provider)
    {
        var key = Guid.NewGuid().ToString("N");
        var providerEntry = Entry(provider, false);
        providerEntry["max_definitions"] = 7;
        await using var host = new RemoteHarness([EventsEntry(listener, false), providerEntry], initialRows:
            [Row("listener", "conformance.events"), Row("provider", "conformance.provider"), Row("probe", "test.sdk-event-probe", ("key", key))]);
        await Start(host);
        Assert.Equal(PluginState.Active, host.Instance("listener").State);
        Assert.Equal(11, await SdkEventProbe.Contexts[key].Events.WaterfallAsync<EquipmentSelectionApplying, int>(Event, _ => ValueTask.FromResult(10), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Host.UnloadAsync("provider", default).AsTask());
        await host.Host.UnloadAsync("listener", default);
        await host.Host.UnloadAsync("provider", default);
    }

    /// <summary>Publishers and listeners in different SDK processes participate in one native event pipeline.</summary>
    [Theory]
    [InlineData("python", "typescript")]
    [InlineData("typescript", "go")]
    [InlineData("go", "rust")]
    [InlineData("rust", "python")]
    public async Task PublisherAndListenerCanUseDifferentLanguages(string publisher, string listener)
    {
        var key = Guid.NewGuid().ToString("N");
        var publication = EventsEntry(publisher, false);
        publication["source_id"] = "publisher-source";
        publication["env"] = new Dictionary<string, object?> { ["EVENT_PLUGIN_ID"] = "conformance.publisher" };
        await using var host = new RemoteHarness([publication, EventsEntry(listener, false)], initialRows:
        [
            Row("probe", "test.sdk-event-probe", ("key", key)), Row("policy", "test.sdk-event-policy"),
            Row("listener", "conformance.events"),
            Row("publisher", "conformance.publisher", ("publisher", true), ("modes", Array.Empty<string>())),
        ]);
        await Start(host);
        using var native = SdkEventProbe.Contexts[key].Events.Intercept<EquipmentSelectionApplying, int>(
            async (_, next, ct) => 10 * await next(ct), 10);
        Assert.True(await host.DispatchLoginAsync("waterfall"));
        Assert.Single(host.Loggers.Entries, entry => entry.Contains("event:listener:waterfall", StringComparison.Ordinal));
        Assert.Single(host.Loggers.Entries, entry => entry.Contains("published:waterfall:1010", StringComparison.Ordinal));
    }

    /// <summary>SDK message middleware crosses the loader boundary, sends the intercepted session, and continues once.</summary>
    [Theory]
    [InlineData("python", false)]
    [InlineData("typescript", false)]
    [InlineData("go", false)]
    [InlineData("rust", false)]
    [InlineData("go", true)]
    [InlineData("rust", true)]
    public async Task MessageMiddlewareUsesNativePipelineAndTeardown(string language, bool wasm)
    {
        await using var host = MiddlewareHost(language, wasm, Row("target", "test.middleware-target"));
        await Start(host);
        await host.AddRowAsync("middleware", "conformance.middleware", new() { ["protocol"] = TestDialect.Protocol });

        Assert.True(await host.DispatchLoginAsync("alice"));
        var sent = Assert.Single(host.Sent);
        Assert.Equal("middleware:alice", sent.Account);
        Assert.Equal("alice", sent.Nickname);

        await host.Host.UnloadAsync("middleware", default);
        Assert.Equal(PluginState.Unloaded, host.Instance("middleware").State);
        Assert.True(await host.DispatchLoginAsync("after-unload"));
        Assert.Single(host.Sent);
    }

    /// <summary>Self-removal preserves the running callback result and prevents another admission.</summary>
    [Theory]
    [InlineData("python", false)]
    [InlineData("typescript", false)]
    [InlineData("go", false)]
    [InlineData("rust", false)]
    [InlineData("go", true)]
    [InlineData("rust", true)]
    public async Task CallbackCanRemoveItselfWithoutDroppingItsResult(string language, bool wasm)
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Host(language, wasm, key, Row("listener", "conformance.events", ("behavior", "self")));
        await Start(host);
        var bus = SdkEventProbe.Contexts[key].Events;
        bus.Emit(Event);
        bus.Emit(Event);
        await bus.SerialAsync(Event, default);
        await bus.SerialAsync(Event, default);
        Assert.Equal(1, Count(host, "event:listener:sync"));
        Assert.Equal(1, Count(host, "event:listener:async"));
        Assert.Equal(77, await bus.WaterfallAsync<EquipmentSelectionApplying, int>(Event, _ => ValueTask.FromResult(10), default));
        Assert.Equal(10, await bus.WaterfallAsync<EquipmentSelectionApplying, int>(Event, _ => ValueTask.FromResult(10), default));
        Assert.Equal(PluginState.Active, host.Instance("listener").State);
    }

    /// <summary>A middleware may short-circuit or call its native continuation repeatedly, matching native semantics.</summary>
    [Theory]
    [InlineData("short", 77, 0)]
    [InlineData("twice", 20, 2)]
    public async Task WaterfallDoesNotInventAOneShotContinuation(string behavior, int expected, int expectedCalls)
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Host("python", false, key, Row("listener", "conformance.events", ("behavior", behavior)));
        await Start(host);
        var calls = 0;
        var result = await SdkEventProbe.Contexts[key].Events.WaterfallAsync<EquipmentSelectionApplying, int>(Event,
            _ => { calls++; return ValueTask.FromResult(10); }, default);
        Assert.Equal(expected, result);
        Assert.Equal(expectedCalls, calls);
    }

    /// <summary>Sync failures remain logged even under serial dispatch; async errors propagate from the native bus.</summary>
    [Theory]
    [InlineData("sync-error", false)]
    [InlineData("async-error", true)]
    public async Task ListenerErrorsKeepTheNativeModeSemantics(string behavior, bool throws)
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Host("python", false, key, Row("listener", "conformance.events", ("behavior", behavior)));
        await Start(host);
        var dispatch = SdkEventProbe.Contexts[key].Events.SerialAsync(Event, default).AsTask();
        if (throws) await Assert.ThrowsAsync<RpcException>(() => dispatch);
        else await dispatch;
        Assert.Equal(1, Count(host, "event:listener:async"));
        Assert.Equal(PluginState.Active, host.Instance("listener").State);
    }

    /// <summary>Result shape failures are rejected at the native event boundary.</summary>
    [Fact]
    public async Task MalformedWaterfallResultDoesNotReachNativeConsumers()
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Host("python", false, key, Row("listener", "conformance.events", ("behavior", "null")));
        await Start(host);
        await Assert.ThrowsAsync<JsonException>(() => SdkEventProbe.Contexts[key].Events.WaterfallAsync<EquipmentSelectionApplying, int>(
            Event, _ => ValueTask.FromResult(10), default).AsTask());
    }

    /// <summary>Priorities and stable ties are selected by the native bus, and bail skips null/error before stopping.</summary>
    [Fact]
    public async Task NativeOrderingAndBailShortCircuitApplyToSdkListeners()
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = Host("python", false, key,
            Row("first", "conformance.events"),
            Row("high", "conformance.events", ("priority", 10), ("behavior", "bail-error")),
            Row("second", "conformance.events", ("decision", "second")),
            Row("last", "conformance.events", ("decision", "last")));
        await Start(host);
        var bus = SdkEventProbe.Contexts[key].Events;
        Assert.Equal(14, await bus.WaterfallAsync<EquipmentSelectionApplying, int>(Event, _ => ValueTask.FromResult(10), default));
        Assert.Equal(new[] { "event:high:waterfall", "event:first:waterfall", "event:second:waterfall", "event:last:waterfall" },
            host.Loggers.Entries.Where(entry => entry.Contains(":waterfall", StringComparison.Ordinal)).Select(entry => entry[(entry.IndexOf("event:", StringComparison.Ordinal))..]));
        Assert.Equal("second", bus.Bail<EquipmentSelectionApplying, string>(Event));
        Assert.Equal(1, Count(host, "event:high:bail"));
        Assert.Equal(1, Count(host, "event:first:bail"));
        Assert.Equal(1, Count(host, "event:second:bail"));
        Assert.Equal(0, Count(host, "event:last:bail"));
    }

    /// <summary>Declarations bind actual event/result identities and reject invalid mode/type combinations before activation.</summary>
    [Theory]
    [InlineData("Unknown", "sync", null)]
    [InlineData("ConnectionClosedEvent", "sync", null)]
    [InlineData("EquipmentSelectionApplying", "emit", null)]
    [InlineData("EquipmentSelectionApplying", "waterfall", null)]
    [InlineData("EquipmentSelectionApplying", "bail", "Int32")]
    [InlineData("EquipmentSelectionApplying", "sync", "Int32")]
    public void InvalidDeclarationsFailBeforeDispatch(string type, string mode, string? result)
    {
        var declaration = JsonSerializer.SerializeToElement(new { type, mode, result_type = result });
        Assert.Throws<ConfigException>(() => RemoteEventContract.Resolve(declaration, publishing: false));
    }

    /// <summary>Event results enforce collection item nullability even without a service method return parameter.</summary>
    [Fact]
    public void EventResultCollectionsRejectNullRecordsButAllowNullableRecordProperties()
    {
        var contract = RemoteEventContract.Resolve(JsonSerializer.SerializeToElement(new
        {
            type = "EquipmentSelectionReadRequested", mode = "waterfall",
            result_type = ContractSchema.TypeName(typeof(IReadOnlyList<InventorySlotRecord>)),
        }), publishing: false);
        Assert.Throws<JsonException>(() => contract.Read(JsonSerializer.Deserialize<JsonElement>("[null]"), contract.Result, new SessionMap(4, true)));
        Assert.Empty((IReadOnlyList<InventorySlotRecord>)contract.Read(JsonSerializer.Deserialize<JsonElement>("[]"), contract.Result, new SessionMap(4, true))!);
        var decision = new EquipmentSelectionDecision(false, 13, true);
        Assert.Equal(decision, contract.Read(JsonSerializer.SerializeToElement(decision, WireJson.Options), typeof(EquipmentSelectionDecision), new SessionMap(4, true)));
    }

    private static int Count(RemoteHarness host, string marker) => host.Loggers.Entries.Count(entry => entry.Contains(marker, StringComparison.Ordinal));

    private static RemoteHarness Host(string language, bool wasm, string key, params PluginRow[] rows) =>
        new([EventsEntry(language, wasm)], loader: wasm ? "loader.wasm" : "loader.process", initialRows:
            new[] { Row("probe", "test.sdk-event-probe", ("key", key)), Row("policy", "test.sdk-event-policy") }.Concat(rows));

    private static RemoteHarness MiddlewareHost(string language, bool wasm, params PluginRow[] rows) =>
        new([MiddlewareEntry(language, wasm)], loader: wasm ? "loader.wasm" : "loader.process", initialRows: rows);

    private static Dictionary<string, object?> MiddlewareEntry(string language, bool wasm)
    {
        if (language is "go" or "rust")
        {
            var binary = language == "go"
                ? (wasm ? CompiledSdk.GoModule : CompiledSdk.Go)
                : (wasm ? CompiledSdk.RustModule : CompiledSdk.Rust);
            Assert.True(binary.Path is not null, binary.Skip);
            return new()
            {
                [wasm ? "module" : "command"] = binary.Path,
                ["source_id"] = "middleware",
                ["max_definitions"] = 4,
            };
        }

        return new()
        {
            ["command"] = language == "python" ? Python.Command : Node.Command,
            ["args"] = new List<object?> { Path.Combine(Python.RepoRoot, language == "python"
                ? "sdk/python/tests/conformance_plugins.py" : "sdk/typescript/dist/tests/conformance-plugins.js") },
            ["source_id"] = "middleware",
            ["max_definitions"] = 4,
        };
    }

    internal static Dictionary<string, object?> EventsEntry(string language, bool wasm)
    {
        if (language is "go" or "rust")
        {
            var binary = CompiledSdk.Events(language, wasm);
            Assert.True(binary.Path is not null, binary.Skip);
            return new() { [wasm ? "module" : "command"] = binary.Path, ["source_id"] = "events", ["max_definitions"] = 1 };
        }
        return new()
        {
            ["command"] = language == "python" ? Python.Command : Node.Command,
            ["args"] = new List<object?> { Path.Combine(Python.RepoRoot, language == "python"
                ? "sdk/python/tests/event_plugins.py" : "sdk/typescript/dist/tests/event-plugins.js") },
            ["source_id"] = "events",
            ["max_definitions"] = 1,
        };
    }

    private static async Task Start(RemoteHarness host)
    {
        try { await host.StartAsync(); }
        catch (Exception error) { throw new Xunit.Sdk.XunitException($"{error}\n{string.Join('\n', host.Loggers.Entries)}"); }
    }
}

/// <summary>Provides the native downstream handler used to prove SDK middleware continues the normal message pipeline.</summary>
[Plugin("test.middleware-target", Requires = [typeof(IMessageRouter)])]
internal sealed class MiddlewareTarget : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Effect(context.Services.Get<IMessageRouter>().For(context).On<LoginRequested>(TestDialect.Protocol,
            (_, _, _) => ValueTask.CompletedTask));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Exposes a hosted native event view for mixed-language dispatch tests.</summary>
[Plugin("test.sdk-event-probe")]
public sealed class SdkEventProbe : IPlugin
{
    internal static ConcurrentDictionary<string, IPluginContext> Contexts { get; } = new();

    /// <summary>Retains the owned native context only until this test row is unloaded.</summary>
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var key = (string)context.Row.Config!["key"]!;
        Contexts[key] = context;
        context.Effect(() => Contexts.TryRemove(key, out _));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Provides a synchronous native service called reentrantly from SDK event listeners.</summary>
[Plugin("test.sdk-event-policy", Provides = [typeof(IEquipmentReplyPolicy)])]
public sealed class SdkEventPolicy : IPlugin
{
    /// <summary>Publishes an ordinary owned service through the shared registry.</summary>
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Effect(context.Services.Register<IEquipmentReplyPolicy>(new Policy()));
        return ValueTask.CompletedTask;
    }

    private sealed class Policy : IEquipmentReplyPolicy
    {
        public bool ReplyAfterInventory => false;
    }
}
