using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Persistence.Memory;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Lobby.Data;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

public sealed class AccountAdmissionTests
{
    [Theory]
    [InlineData("kick_old", false)]
    [InlineData("reject_new", true)]
    public void SharedAdmissionRequiresRefusingNewDuplicates(string policy, bool valid)
    {
        var options = new LobbyOptions { RequireAccountOwnership = true, DuplicateLogin = policy };
        Assert.Equal(valid, !options.Validate().Any());
    }

    [Fact]
    public void ConfiguredAdmissionFailsWhenItsProviderIsMissing()
    {
        var services = new ServiceRegistry();
        var error = Assert.Throws<ConfigException>(() => LobbyPlugin.ResolveAccountAdmission(
            new LobbyOptions { RequireAccountOwnership = true }, services));
        Assert.Contains("IAccountSessionAdmission", error.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LoginConsultsAdmissionBeforeInstallingPlayerState(bool required, bool accept)
    {
        await using var fixture = await Fixture.CreateAsync(required, accept);
        var session = new Session(1);
        await fixture.LoginAsync(session);
        Assert.Equal(required ? 1 : 0, fixture.Admission.Attempts);
        Assert.Equal(!required || accept, session.TryFeature<IPlayerSession>(out _));
        Assert.Equal(!required || accept ? 1 : 0, fixture.Success.Sent.Count);
        Assert.Equal(required && !accept ? 1 : 0, fixture.Rejection.Sent.Count);
    }

    [Fact]
    public async Task LocalDuplicateRefusalReleasesTheNewlyAcquiredSharedHold()
    {
        await using var fixture = await Fixture.CreateAsync(true, true);
        var first = new Session(1);
        var second = new Session(2);
        await fixture.LoginAsync(first);
        await fixture.LoginAsync(second);
        Assert.Single(fixture.Success.Sent);
        Assert.Single(fixture.Rejection.Sent);
        Assert.Equal(2, fixture.Admission.Attempts);
        Assert.Same(second, Assert.Single(fixture.Admission.Released));
        Assert.True(first.TryFeature<IPlayerSession>(out _));
        Assert.False(second.TryFeature<IPlayerSession>(out _));
    }

    [Fact]
    public async Task FailedLocalClaimReleasesItsSharedHoldBeforePropagating()
    {
        await using var fixture = await Fixture.CreateAsync(true, true);
        var session = new Session(1);
        session.SetLobbyPlayer(999);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.LoginAsync(session).AsTask());
        Assert.Same(session, Assert.Single(fixture.Admission.Released));
        Assert.Empty(fixture.Success.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingCharacterInitializationObtainsAdmissionBeforeOpeningAndRetainsItForLogin(bool accept)
    {
        await using var fixture = await Fixture.CreateAsync(true, accept, creation: true);
        var session = new Session(1);
        await fixture.LoginAsync(session);
        Assert.Equal(1, fixture.Admission.Attempts);
        Assert.Equal(accept ? 1 : 0, fixture.Opened.Sent.Count);
        Assert.False(session.TryFeature<IPlayerSession>(out _));
        Assert.Empty(fixture.Success.Sent);
        if (!accept)
        {
            Assert.Single(fixture.Rejection.Sent);
            Assert.Equal("fighter", (await fixture.Players.FindByUsernameAsync("account", default))!.Nickname);
            return;
        }
        await fixture.DispatchAsync(session, new CharacterCreateRequested("Initialized", 1));
        await fixture.DispatchAsync(session, new CharacterLoginRequested());
        Assert.Equal(2, fixture.Admission.Attempts);
        Assert.Equal("Initialized", Assert.Single(fixture.Success.Sent).Nickname);
        Assert.True(session.TryFeature<IPlayerSession>(out _));
        Assert.Empty(fixture.Rejection.Sent);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceRegistry _services = new();
        private readonly EventBus _events = new(NullLogger.Instance);
        private readonly List<IDisposable> _registrations = [];
        private PluginContext? _routerContext;
        private PluginContext? _lobbyContext;
        internal Admission Admission { get; } = new();
        internal Wire<LoginSucceeded> Success { get; } = new();
        internal Wire<LoginRejected> Rejection { get; } = new();
        internal Wire<CharacterCreationOpened> Opened { get; } = new();
        internal InMemoryPlayerRepository Players { get; } = new();

        internal static async Task<Fixture> CreateAsync(bool required, bool accept, bool creation = false)
        {
            var fixture = new Fixture();
            try
            {
                fixture.Admission.Accept = accept;
                var players = fixture.Players;
                await players.CreateAsync("account", "fighter", default);
                fixture._registrations.Add(fixture._services.Register<IPlayerRepository>(players));
                fixture._registrations.Add(fixture._services.Register<IAccountSessionAdmission>(fixture.Admission));
                fixture._registrations.Add(fixture._services.RegisterMany<IInboundMessageBinding<LoginRequested>>(new Wire<LoginRequested>()));
                fixture._registrations.Add(fixture._services.RegisterMany<IMessageSender<LoginSucceeded>>(fixture.Success));
                fixture._registrations.Add(fixture._services.RegisterMany<IMessageSender<LoginRejected>>(fixture.Rejection));
                if (creation)
                {
                    fixture._registrations.Add(fixture._services.Register<IInventoryRepository>(new MemoryInventoryRepository(new MemoryPlayerTable(4))));
                    fixture._registrations.Add(fixture._services.RegisterMany<IInboundMessageBinding<CharacterNameRequested>>(new Wire<CharacterNameRequested>()));
                    fixture._registrations.Add(fixture._services.RegisterMany<IInboundMessageBinding<CharacterCreateRequested>>(new Wire<CharacterCreateRequested>()));
                    fixture._registrations.Add(fixture._services.RegisterMany<IInboundMessageBinding<CharacterLoginRequested>>(new Wire<CharacterLoginRequested>()));
                    fixture._registrations.Add(fixture._services.RegisterMany<IMessageSender<CharacterCreationOpened>>(fixture.Opened));
                    fixture._registrations.Add(fixture._services.RegisterMany<IMessageSender<CharacterNameReplied>>(new Wire<CharacterNameReplied>()));
                    fixture._registrations.Add(fixture._services.RegisterMany<IMessageSender<CharacterCreateReplied>>(new Wire<CharacterCreateReplied>()));
                }
                fixture._routerContext = fixture.Context("core.message-router");
                fixture._lobbyContext = fixture.Context("lobby", new()
                {
                    ["protocol"] = "admission/game", ["authentication"] = "open",
                    ["duplicate_login"] = "reject_new", ["require_account_ownership"] = required,
                    ["character_creation"] = creation, ["inventory_source"] = creation ? "mysql" : "reference",
                });
                await new MessageRouterPlugin().ActivateAsync(fixture._routerContext, default);
                await new LobbyPlugin().ActivateAsync(fixture._lobbyContext, default);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        internal async ValueTask LoginAsync(ISession session) =>
            Assert.True(await _services.Get<IMessageRouter>().DispatchAsync("admission/game", session, new LoginRequested("account"), default));

        internal async ValueTask DispatchAsync<T>(ISession session, T message) where T : IMessage =>
            Assert.True(await _services.Get<IMessageRouter>().DispatchAsync("admission/game", session, message, default));

        private PluginContext Context(string id, Dictionary<string, object?>? config = null) =>
            new(new PluginRow { Id = id, Plugin = id, Source = "test", Config = config }, _services, _events, NullLogger.Instance);

        public async ValueTask DisposeAsync()
        {
            if (_lobbyContext is not null) await _lobbyContext.UnwindAsync();
            if (_routerContext is not null) await _routerContext.UnwindAsync();
            foreach (var registration in _registrations.AsEnumerable().Reverse()) registration.Dispose();
        }
    }

    private sealed class Admission : IAccountSessionAdmission
    {
        internal bool Accept { get; set; }
        internal int Attempts { get; private set; }
        internal List<ISession> Released { get; } = [];
        public ValueTask<bool> TryAdmitAsync(ISession session, long userNum, CancellationToken cancellationToken)
        {
            if (session.TryFeature<IPlayerSession>(out var player))
                Assert.Equal(999, player.Identity.UserNum);
            Assert.True(userNum > 0);
            ++Attempts;
            return ValueTask.FromResult(Accept);
        }
        public ValueTask ReleaseAsync(ISession session) { Released.Add(session); return ValueTask.CompletedTask; }
    }

    private sealed class Wire<T> : IInboundMessageBinding<T>, IMessageSender<T> where T : IMessage
    {
        internal List<T> Sent { get; } = [];
        public string Protocol => "admission/game";
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct) { Sent.Add(message); return ValueTask.CompletedTask; }
    }

    private sealed class Session(long id) : ISessionWithFeatures
    {
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public long Id => id;
        public IConnection Connection { get; } = new Connection(id);
        public ClientProfile Profile { get; } = new("kr-1256", "kr", 1256, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Connection(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason) { IsClosed = true; return ValueTask.CompletedTask; }
    }
}
