using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

/// <summary>
/// Verifies sponsor prize defaults publication on login, atomic offline claim flushing before
/// <see cref="LoginSucceeded"/>, and fail-loud validation in <see cref="LobbyPlugin"/>.
/// </summary>
public sealed class UserHostLobbyTests
{
    [Theory]
    [InlineData(0u, 10_000u, false)]
    [InlineData(20_000u, 10_000u, false)]
    [InlineData(1_000u, 100_000u, true)]
    public void LobbyOptionsValidateUserHostBoundsWhenEnabled(uint minPrize, uint maxPrize, bool valid)
    {
        var options = new LobbyOptions
        {
            UserHostEnabled = true,
            UserHostMinPrize = minPrize,
            UserHostMaxPrize = maxPrize,
        };
        Assert.Equal(valid, !options.Validate().Any());
    }

    [Fact]
    public async Task LoginPublishesDefaultsAndFlushesOfflinePrizeClaimsBeforeLoginSucceeded()
    {
        await using var rig = await Rig.StartAsync(userHostEnabled: true, minPrize: 1_000, maxPrize: 50_000);
        var host = await rig.Players.CreateAsync("host", "Host", default);
        var winner = await rig.Players.CreateAsync("winner", "Winner", default);
        await rig.Wallets.GetOrCreateAsync(new(host.UserNum, 20_000, 0), default);
        await rig.Wallets.GetOrCreateAsync(new(winner.UserNum, 2_000, 0), default);

        // Give host a ticket so escrow begin succeeds, escrow 5,000 Lucci, settle with offline winner.
        await rig.Inventory.ReplaceSlotsAsync(host.UserNum, Contracts.Inventory.InventoryBag.CharacterBag,
            [new(host.UserNum, Contracts.Inventory.InventoryBag.CharacterBag, 1L, 101164, 2, 0)], default);
        var begin = await rig.Prizes.BeginEscrowAsync(new UserHostEscrowBeginCommand(
            "kr-1256", "match-login-1", 10, host.UserNum, 5_000, 10, 101164, 1_000), default);
        Assert.Equal(UserHostEscrowBeginOutcome.Committed, begin.Outcome);
        var settle = await rig.Prizes.SettleEscrowAsync(new UserHostSettleCommand(
            "kr-1256", "match-login-1", false, [new UserHostWinnerAllocation(winner.UserNum, 4_500)], [], 2_000), default);
        Assert.Equal(UserHostSettlementOutcome.Applied, settle.Outcome);

        var session = new Session(1);
        await rig.LoginAsync(session, "winner");

        // LoginSucceeded already sees the flushed claim (2,000 + 4,500 = 6,500), and UserHostDefaultsPublished fired.
        var login = Assert.Single(rig.Succeeded.Sent);
        Assert.Equal(6_500, login.Wallet?.Lucci);
        var defaults = Assert.Single(rig.Defaults.Sent);
        Assert.Equal(1_000u, defaults.MinPrize);
        Assert.Equal(50_000u, defaults.MaxPrize);

        // Re-login does not double-credit the flushed claim.
        var session2 = new Session(2);
        await rig.LoginAsync(session2, "winner");
        Assert.Equal(2, rig.Succeeded.Sent.Count);
        Assert.Equal(6_500, rig.Succeeded.Sent[1].Wallet?.Lucci);
    }

    [Fact]
    public async Task LoginWithoutUserHostEnabledDoesNotPublishDefaults()
    {
        await using var rig = await Rig.StartAsync(userHostEnabled: false, minPrize: 1_000, maxPrize: 50_000);
        await rig.Players.CreateAsync("plain", "Plain", default);

        await rig.LoginAsync(new Session(1), "plain");

        Assert.Single(rig.Succeeded.Sent);
        Assert.Empty(rig.Defaults.Sent);
    }

    [Fact]
    public async Task MissingPrizeStoreFailsActivationWhenUserHostEnabled()
    {
        await Assert.ThrowsAsync<ConfigException>(async () =>
        {
            await using var _ = await Rig.StartAsync(userHostEnabled: true, minPrize: 1_000, maxPrize: 50_000, registerPrizeStore: false);
        });
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceRegistry _services = new();
        private readonly EventBus _events = new(NullLogger.Instance);
        private readonly List<IDisposable> _disposables = [];
        private PluginContext? _routerContext;
        private PluginContext? _lobbyContext;

        public MemoryPlayerTable Table { get; } = new(16);
        public MemoryPlayerRepository Players { get; }
        public MemoryInventoryRepository Inventory { get; }
        public MemoryPlayerWalletRepository Wallets { get; }
        public MemoryUserHostPrizeStore Prizes { get; }
        public RecordingSender<LoginSucceeded> Succeeded { get; } = new();
        public RecordingSender<LoginRejected> Rejected { get; } = new();
        public RecordingSender<UserHostDefaultsPublished> Defaults { get; } = new();

        private Rig()
        {
            Players = new MemoryPlayerRepository(Table);
            Inventory = new MemoryInventoryRepository(Table);
            Wallets = new MemoryPlayerWalletRepository(Table);
            Prizes = new MemoryUserHostPrizeStore(Table);
        }

        public static async Task<Rig> StartAsync(bool userHostEnabled, uint minPrize, uint maxPrize, bool registerPrizeStore = true)
        {
            var rig = new Rig();
            try
            {
                rig._disposables.Add(rig._services.Register<IPlayerRepository>(rig.Players));
                rig._disposables.Add(rig._services.Register<IInventoryRepository>(rig.Inventory));
                rig._disposables.Add(rig._services.Register<IPlayerWalletRepository>(rig.Wallets));
                if (registerPrizeStore)
                    rig._disposables.Add(rig._services.Register<IUserHostPrizeStore>(rig.Prizes));
                rig._disposables.Add(rig._services.RegisterMany<IInboundMessageBinding<LoginRequested>>(new RecordingBinding<LoginRequested>()));
                rig._disposables.Add(rig._services.RegisterMany<IMessageSender<LoginSucceeded>>(rig.Succeeded));
                rig._disposables.Add(rig._services.RegisterMany<IMessageSender<LoginRejected>>(rig.Rejected));
                rig._disposables.Add(rig._services.RegisterMany<IMessageSender<UserHostDefaultsPublished>>(rig.Defaults));

                rig._routerContext = new PluginContext(
                    new PluginRow { Id = "core.message-router", Plugin = "core.message-router", Source = "test" },
                    rig._services, rig._events, NullLogger.Instance);
                rig._lobbyContext = new PluginContext(
                    new PluginRow
                    {
                        Id = "lobby",
                        Plugin = "lobby",
                        Source = "test",
                        ClientBuild = new ClientBuild("kr", 1256, 0),
                        Config = new Dictionary<string, object?>
                        {
                            ["protocol"] = "kr-1256/game",
                            ["authentication"] = "open",
                            ["inventory_source"] = "mysql",
                            ["money_source"] = "mysql",
                            ["user_host_enabled"] = userHostEnabled,
                            ["user_host_min_prize"] = minPrize,
                            ["user_host_max_prize"] = maxPrize,
                        },
                    },
                    rig._services, rig._events, NullLogger.Instance);

                await new MessageRouterPlugin().ActivateAsync(rig._routerContext, default);
                await new LobbyPlugin().ActivateAsync(rig._lobbyContext, default);
                return rig;
            }
            catch
            {
                await rig.DisposeAsync();
                throw;
            }
        }

        public async ValueTask LoginAsync(ISession session, string username) =>
            Assert.True(await _services.Get<IMessageRouter>().DispatchAsync("kr-1256/game", session, new LoginRequested(username), default));

        public async ValueTask DisposeAsync()
        {
            if (_lobbyContext is not null) await _lobbyContext.UnwindAsync();
            if (_routerContext is not null) await _routerContext.UnwindAsync();
            foreach (var d in _disposables) d.Dispose();
        }
    }

    private sealed class RecordingBinding<T> : IInboundMessageBinding<T> where T : IMessage
    {
        public string Protocol => "kr-1256/game";
    }

    private sealed class RecordingSender<T> : IMessageSender<T> where T : IMessage
    {
        public string Protocol => "kr-1256/game";
        public List<T> Sent { get; } = [];
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        {
            Sent.Add(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Session(long id) : ISessionWithFeatures
    {
        public long Id => id;
        public IConnection Connection { get; } = new FakeConnection(id);
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public ClientProfile Profile { get; } = new("kr-1256", "kr", 1256, 0, 0, "kr-1256");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class FakeConnection(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
