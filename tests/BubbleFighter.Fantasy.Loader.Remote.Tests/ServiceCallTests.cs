using System.Collections.Concurrent;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Loader.Remote.Tests;

[Collection("SDK compilation")]
public sealed class ServiceCallTests
{
    private const long LargeId = 9007199254740993;

    [Fact]
    public async Task ServiceCallsPreserveInt64AndStoreIdempotentSnapshots()
    {
        using var h = new Calls();
        var wallet = new MemoryPlayerWalletRepository(new MemoryPlayerTable(4));
        using var registration = h.Registry.Register<IPlayerWalletRepository>(wallet);
        Assert.Equal(JsonValueKind.Null, (await h.Call("FindByUserNumAsync", new { user_num = LargeId })).ValueKind);
        for (var i = 0; i < 2; i++)
            Assert.Equal(JsonValueKind.Null, (await h.Call("UpsertAsync", new { wallet = new { user_num = LargeId, lucci = 73, cash = 19 } })).ValueKind);
        var result = await h.Call("FindByUserNumAsync", new { user_num = LargeId });
        Assert.Equal(LargeId, result.GetProperty("user_num").GetInt64());
        Assert.Equal(new PlayerWallet(LargeId, 73, 19), await wallet.FindByUserNumAsync(LargeId, default));
    }

    [Theory]
    [InlineData("{\"user_num\":null}")]
    [InlineData("{\"user_num\":1,\"ct\":null}")]
    [InlineData("{\"user_num\":1,\"user_num\":2}")]
    [InlineData("{\"user_num\":9223372036854775808}")]
    [InlineData("{}")]
    public async Task MalformedArgumentsCannotInvokeService(string args)
    {
        using var h = new Calls();
        var wallet = new BlockingWallet();
        using var registration = h.Registry.Register<IPlayerWalletRepository>(wallet);
        var error = await Assert.ThrowsAsync<RpcException>(() => h.Call("FindByUserNumAsync", JsonSerializer.Deserialize<JsonElement>(args)));
        Assert.Equal(RpcCodes.InvalidParams, error.Code);
        Assert.False(wallet.Entered.Task.IsCompleted);
    }

    [Fact]
    public async Task DeclarationOptionalAbsenceAndServiceErrorsAreDistinct()
    {
        using var h = new Calls();
        var absent = await Assert.ThrowsAsync<RpcException>(() => h.Call("FindByUserNumAsync", new { user_num = 1 }));
        Assert.Equal(RpcCodes.ServiceUnavailable, absent.Code);
        var undeclared = await Assert.ThrowsAsync<RpcException>(() => h.Call("FindByUserNum", new { user_num = 1 }, "IOnlinePlayers"));
        Assert.Equal(RpcCodes.TypeNotAllowed, undeclared.Code);
        using var registration = h.Registry.Register<IPlayerWalletRepository>(new MemoryPlayerWalletRepository(new MemoryPlayerTable(4)));
        var failed = await Assert.ThrowsAsync<RpcException>(() => h.Call("FindByUserNumAsync", new { user_num = -1 }));
        Assert.Equal(RpcCodes.ServiceError, failed.Code);
        Assert.Equal(typeof(ArgumentOutOfRangeException).FullName, failed.ErrorData!.Value.GetProperty("exception").GetString());
        Assert.DoesNotContain("positive login", failed.Message);
    }

    [Fact]
    public async Task DuplicateWalletFieldsCannotMutatePersistence()
    {
        using var h = new Calls();
        var wallet = new MemoryPlayerWalletRepository(new MemoryPlayerTable(4));
        using var registration = h.Registry.Register<IPlayerWalletRepository>(wallet);
        var args = JsonSerializer.Deserialize<JsonElement>("""{"wallet":{"user_num":42,"lucci":73,"lucci":999,"cash":19}}""");
        var error = await Assert.ThrowsAsync<RpcException>(() => h.Call("UpsertAsync", args));
        Assert.Equal(RpcCodes.InvalidParams, error.Code);
        Assert.Null(await wallet.FindByUserNumAsync(42, default));
    }

    [Fact]
    public void V1CannotDeclareDomainServices()
    {
        var definition = JsonSerializer.SerializeToElement(new { id = "test.calls", version = "1.0.0", requires = new[] { "IPlayerWalletRepository" } });
        Assert.Throws<ConfigException>(() => RemoteDefinition.Parse(definition, "test", 1));
        Assert.Contains(typeof(IPlayerWalletRepository), RemoteDefinition.Parse(definition, "test", 2).Declaration.Requires);
    }

    [Fact]
    public async Task FullMethodIdAndNullabilityAreHonored()
    {
        using var h = new Calls("IOnlinePlayers");
        using var registration = h.Registry.Register<IOnlinePlayers>(new Online());
        var id = ContractSchema.MethodId(typeof(IOnlinePlayers).GetMethod(nameof(IOnlinePlayers.FindByNickname))!);
        var error = await Assert.ThrowsAsync<RpcException>(() => h.Call(id, new { nickname = (string?)null }, "IOnlinePlayers"));
        Assert.Equal(RpcCodes.InvalidParams, error.Code);
        var first = await h.Call(id, new { nickname = "present" }, "IOnlinePlayers");
        Assert.True(h.Sessions.TryGet(first.GetString()!, out _));
        Assert.Equal(first.GetString(), (await h.Call(id, new { nickname = "present" }, "IOnlinePlayers")).GetString());
        Assert.Equal(JsonValueKind.Null, (await h.Call(id, new { nickname = "absent" }, "IOnlinePlayers")).ValueKind);
    }

    [Fact]
    public void OpaqueSessionHandlesDoNotAliasReusedNativeIdsOrSurviveClear()
    {
        var map = new SessionMap(2, opaqueHandles: true);
        var first = new FakeSession(7);
        var second = new FakeSession(7);
        var a = map.Add(first);
        var b = map.Add(second);
        Assert.NotEqual(a, b);
        Assert.Same(first, map.TryGet(a, out var saved) ? saved : null);
        Assert.Throws<InvalidOperationException>(() => map.Add(new FakeSession(9)));
        map.Clear();
        Assert.False(map.TryGet(a, out _));
        Assert.NotEqual(a, map.Add(first));
    }

    [Fact]
    public async Task NestedSessionResultsRoundTripButForeignAndClosedHandlesCannotInvokeProviders()
    {
        using var h = new Calls("IRoomRoster", "IInventoryPushService");
        var session = new ClosingSession();
        using var roster = h.Registry.Register<IRoomRoster>(new Roster(session));
        var push = new Push();
        using var registration = h.Registry.Register<IInventoryPushService>(push);
        var result = await h.Call("Find", new { room_id = 1 }, "IRoomRoster");
        var seat = result.GetProperty("seats")[0];
        Assert.Equal(JsonValueKind.Null, seat.GetProperty("relay").ValueKind);
        var handle = seat.GetProperty("game").GetString()!;
        await h.Call("PushInventoryAsync", new { session = handle, user_num = 42 }, "IInventoryPushService");
        Assert.Same(session, push.Last);
        push.Last = null;
        var foreign = new SessionMap(4, opaqueHandles: true).Add(session);
        Assert.Equal(RpcCodes.SessionUnknown, (await Assert.ThrowsAsync<RpcException>(() =>
            h.Call("PushInventoryAsync", new { session = foreign, user_num = 42 }, "IInventoryPushService"))).Code);
        await session.Connection.CloseAsync("test close");
        Assert.Equal(RpcCodes.SessionUnknown, (await Assert.ThrowsAsync<RpcException>(() =>
            h.Call("PushInventoryAsync", new { session = handle, user_num = 42 }, "IInventoryPushService"))).Code);
        Assert.Null(push.Last);
        Assert.Equal(0, h.Sessions.Count);
        Assert.Equal(RpcCodes.ServiceError, (await Assert.ThrowsAsync<RpcException>(() =>
            h.Call("Find", new { room_id = 1 }, "IRoomRoster"))).Code);
    }

    [Fact]
    public async Task NullCollectionElementsAreRejectedBeforeProviderInvocation()
    {
        using var h = new Calls("IInventoryPushService");
        var push = new Push();
        using var registration = h.Registry.Register<IInventoryPushService>(push);
        var handle = h.Sessions.Add(new FakeSession(42));
        var error = await Assert.ThrowsAsync<RpcException>(() => h.Call("PushPurchaseAsync",
            new { session = handle, user_num = 42, grants = new object?[] { null } }, "IInventoryPushService"));
        Assert.Equal(RpcCodes.InvalidParams, error.Code);
        Assert.Null(push.Last);
    }

    [Fact]
    public async Task TimeoutKeepsUnderlyingCallOwnedUntilItActuallyFinishes()
    {
        using var h = new Calls();
        var wallet = new BlockingWallet();
        using var registration = h.Registry.Register<IPlayerWalletRepository>(wallet);
        var activation = new RemoteActivation(null!, h.Definition, h.Context, 4, 16, TimeSpan.FromMilliseconds(200));
        activation.GoLive();
        try
        {
            var pending = activation.CallServiceAsync(Request("FindByUserNumAsync", new { user_num = 1 }), default);
            await wallet.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RpcCodes.InternalError, (await Assert.ThrowsAsync<RpcException>(() => pending)).Code);
            Assert.True(activation.Faulted.IsCompleted);
            var shutdown = h.Context.UnwindAsync().AsTask();
            Assert.False(shutdown.IsCompleted);
            wallet.Release.SetResult();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { wallet.Release.TrySetResult(); }
    }

    [Fact]
    public async Task CooperativeTimeoutAlsoFaultsActivationAndRefusesLaterCalls()
    {
        using var h = new Calls();
        using var registration = h.Registry.Register<IPlayerWalletRepository>(new CancelledWallet());
        var activation = new RemoteActivation(null!, h.Definition, h.Context, 4, 16, TimeSpan.FromMilliseconds(200));
        activation.GoLive();
        var error = await Assert.ThrowsAsync<RpcException>(() => activation.CallServiceAsync(Request("FindByUserNumAsync", new { user_num = 1 }), default));
        Assert.Equal(RpcCodes.InternalError, error.Code);
        Assert.True(activation.Faulted.IsCompleted);
        var stopped = await Assert.ThrowsAsync<RpcException>(() => activation.CallServiceAsync(Request("FindByUserNumAsync", new { user_num = 1 }), default));
        Assert.Equal(RpcCodes.ActivationNotLive, stopped.Code);
    }

    [Theory]
    [InlineData("python")]
    [InlineData("typescript")]
    public async Task InterpretedPluginCallsRealMountedMemoryService(string language)
    {
        var key = Guid.NewGuid().ToString("N");
        var entry = new Dictionary<string, object?>
        {
            ["command"] = language == "python" ? Python.Command : "node",
            ["args"] = new List<object?> { Path.Combine(Python.RepoRoot, language == "python" ? "sdk/python/examples/services_wallet.py" : "sdk/typescript/dist/examples/services-wallet.js") },
            ["source_id"] = "wallet",
        };
        await using var host = new RemoteHarness([entry], initialRows:
            [new PluginRow { Id = "wallet-store", Plugin = "test.sdk-wallet-store", Source = "test", Config = new Dictionary<string, object?> { ["key"] = key } }]);
        await host.StartAsync();
        await host.AddRowAsync("wallet-consumer", "example.services-wallet", new());
        Assert.Equal(PluginState.Active, host.Instance("wallet-consumer").State);
        Assert.Equal(new PlayerWallet(LargeId, 73, 19), await SdkWalletStore.Stores[key].FindByUserNumAsync(LargeId, default));
        await host.Host.UnloadAsync("wallet-consumer", default);
        Assert.Equal(PluginState.Unloaded, host.Instance("wallet-consumer").State);
    }

    [Theory]
    [InlineData("rust", false)]
    [InlineData("rust", true)]
    [InlineData("go", false)]
    [InlineData("go", true)]
    public async Task CompiledProcessAndWasmCallRealMountedMemoryService(string language, bool wasm)
    {
        var binary = CompiledSdk.Wallet(language, wasm);
        Assert.True(binary.Path is not null, binary.Skip);
        var key = Guid.NewGuid().ToString("N");
        var entry = new Dictionary<string, object?> { [wasm ? "module" : "command"] = binary.Path, ["source_id"] = "wallet" };
        await using var host = new RemoteHarness([entry], loader: wasm ? "loader.wasm" : "loader.process", initialRows:
            [new PluginRow { Id = "wallet-store", Plugin = "test.sdk-wallet-store", Source = "test", Config = new Dictionary<string, object?> { ["key"] = key } }]);
        await host.StartAsync();
        await host.AddRowAsync("wallet-consumer", "example.services-wallet", new());
        Assert.Equal(PluginState.Active, host.Instance("wallet-consumer").State);
        Assert.Equal(new PlayerWallet(LargeId, 73, 19), await SdkWalletStore.Stores[key].FindByUserNumAsync(LargeId, default));
        await host.Host.UnloadAsync("wallet-consumer", default);
        Assert.Equal(PluginState.Unloaded, host.Instance("wallet-consumer").State);
    }

    private static JsonElement Request(string method, object args, string service = "IPlayerWalletRepository") =>
        JsonSerializer.SerializeToElement(new { service, method, args });

    private sealed class Calls : IDisposable
    {
        public ServiceRegistry Registry { get; } = new();
        public PluginContext Context { get; }
        public SessionMap Sessions { get; } = new(8, opaqueHandles: true);
        public RemoteDefinition Definition { get; }
        public Calls(params string[] services)
        {
            Definition = RemoteDefinition.Parse(JsonSerializer.SerializeToElement(new { id = "test.calls", version = "1.0.0", optional = services.Length == 0 ? ["IPlayerWalletRepository"] : services }), "test", 2);
            Context = new(new PluginRow { Id = "calls", Plugin = "test.calls", Source = "test" }, Registry, new EventBus(NullLogger.Instance), NullLogger.Instance);
        }
        public Task<JsonElement> Call(string method, object args, string service = "IPlayerWalletRepository") =>
            RemoteServiceInvoker.InvokeAsync(Definition, Context, Sessions, Request(method, args, service), TimeSpan.FromSeconds(10), default);
        public void Dispose() => Context.Unwind();
    }

    private sealed class BlockingWallet : IPlayerWalletRepository
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<PlayerWallet?> FindByUserNumAsync(long userNum, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task;
            return null;
        }
        public ValueTask UpsertAsync(PlayerWallet wallet, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CancelledWallet : IPlayerWalletRepository
    {
        public async ValueTask<PlayerWallet?> FindByUserNumAsync(long userNum, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        }
        public ValueTask UpsertAsync(PlayerWallet wallet, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Online : IOnlinePlayers
    {
        private readonly ISession _session = new FakeSession(42);
        public ISession? FindByNickname(string nickname) => nickname == "present" ? _session : null;
        public ISession? FindByUserNum(int userNum) => userNum == 42 ? _session : null;
    }

    private sealed class Roster(ISession session) : IRoomRoster
    {
        public RoomRosterView? Find(int roomId) => new(roomId, RoomStage.Waiting, 1, 0,
            [new RoomSeatView(0, 1, 42, session, null, false)], false);
        public RoomRosterView? FindBySession(long sessionId) => Find(1);
    }

    private sealed class Push : IInventoryPushService
    {
        public ISession? Last;
        public ValueTask PushInventoryAsync(ISession session, long userNum, CancellationToken ct) { Last = session; return ValueTask.CompletedTask; }
        public ValueTask PushPurchaseAsync(ISession session, long userNum, IReadOnlyList<InventorySlotRecord> grants, CancellationToken ct) { Last = session; return ValueTask.CompletedTask; }
        public ValueTask PushSlotUpdateAsync(ISession session, InventorySlotRecord slot, CancellationToken ct) { Last = session; return ValueTask.CompletedTask; }
    }

    private sealed class ClosingSession : ISession
    {
        public long Id => 42;
        public BubbleFighter.Fantasy.Abstractions.Net.IConnection Connection { get; } = new ClosingConnection();
        public ClientProfile Profile { get; } = new("kr-812", "kr", 812, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ClosingConnection : BubbleFighter.Fantasy.Abstractions.Net.IConnection
    {
        public long Id => 42;
        public System.Net.EndPoint RemoteEndPoint => throw new NotSupportedException();
        public System.IO.Pipelines.PipeReader Input => throw new NotSupportedException();
        public System.IO.Pipelines.PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason) { IsClosed = true; return ValueTask.CompletedTask; }
    }
}

[Plugin("test.sdk-wallet-store", Provides = [typeof(IPlayerWalletRepository)])]
public sealed class SdkWalletStore : IPlugin
{
    internal static ConcurrentDictionary<string, IPlayerWalletRepository> Stores { get; } = new();
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var key = (string)context.Row.Config!["key"]!;
        var store = new MemoryPlayerWalletRepository(new MemoryPlayerTable(4));
        Stores[key] = store;
        context.Effect(() => Stores.TryRemove(key, out _));
        context.Effect(context.Services.Register<IPlayerWalletRepository>(store));
        return ValueTask.CompletedTask;
    }
}
