using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Lobby.Tests;

public sealed class LauncherTicketCreationTests
{
    [Fact]
    public async Task CreationResumesVerifiedIdentityAndLanguageWithoutRedeemingTheTicketAgain()
    {
        var services = new ServiceRegistry();
        var events = new EventBus(NullLogger.Instance);
        var tickets = new Tickets();
        using var ticketService = services.Register<ILauncherTicketService>(tickets);
        using var login = services.RegisterMany<IInboundMessageBinding<LoginRequested>>(new Wire<LoginRequested>());
        using var name = services.RegisterMany<IInboundMessageBinding<CharacterNameRequested>>(new Wire<CharacterNameRequested>());
        using var create = services.RegisterMany<IInboundMessageBinding<CharacterCreateRequested>>(new Wire<CharacterCreateRequested>());
        using var resume = services.RegisterMany<IInboundMessageBinding<CharacterLoginRequested>>(new Wire<CharacterLoginRequested>());
        var success = new Wire<LoginSucceeded>();
        using var successRegistration = services.RegisterMany<IMessageSender<LoginSucceeded>>(success);
        using var rejection = services.RegisterMany<IMessageSender<LoginRejected>>(new Wire<LoginRejected>());
        var opened = new Wire<CharacterCreationOpened>();
        using var opening = services.RegisterMany<IMessageSender<CharacterCreationOpened>>(opened);
        using var nameReply = services.RegisterMany<IMessageSender<CharacterNameReplied>>(new Wire<CharacterNameReplied>());
        var created = new Wire<CharacterCreateReplied>();
        using var createReply = services.RegisterMany<IMessageSender<CharacterCreateReplied>>(created);
        var routerContext = Context("core.message-router");
        var lobbyContext = Context("lobby", new()
        {
            ["protocol"] = "ticket-creation/game", ["authentication"] = "ticket",
            ["character_creation_preview"] = true,
        });
        var session = new Session(1);
        var redeemed = new List<LauncherLaunchRedeemed>();
        using var launches = events.On<LauncherLaunchRedeemed>(redeemed.Add);
        try
        {
            await new MessageRouterPlugin().ActivateAsync(routerContext, default);
            await new LobbyPlugin().ActivateAsync(lobbyContext, default);
            var router = services.Get<IMessageRouter>();
            Assert.True(await router.DispatchAsync("ticket-creation/game", session, new LoginRequested(new string('a', 64)), default));
            Assert.Single(opened.Sent);
            Assert.Empty(success.Sent);
            Assert.Equal("zh-CN", session.Feature<ISessionLanguage>().LauncherLanguage);
            // The launch is announced once, when the ticket is redeemed, so the companion binds this exact connection.
            var launch = Assert.Single(redeemed);
            Assert.Equal(("launch-1", "verified-account", "kr-1256"), (launch.LaunchId, launch.Account, launch.Build));
            Assert.Same(session, launch.Session);
            await router.DispatchAsync("ticket-creation/game", session, new CharacterCreateRequested("NewFighter", 1), default);
            Assert.Equal(0, Assert.Single(created.Sent).Result);
            await router.DispatchAsync("ticket-creation/game", session, new CharacterLoginRequested(), default);
            Assert.Equal("verified-account", Assert.Single(success.Sent).Account);
            Assert.Equal(SessionPhase.MainMenu, session.Feature<IPlayerSession>().Phase);
            Assert.Equal("zh-CN", session.Feature<ISessionLanguage>().LauncherLanguage);
            Assert.Equal(1, tickets.Calls);
            Assert.Single(redeemed);
            Assert.False(session.Connection.IsClosed);

            var replay = new Session(2);
            await router.DispatchAsync("ticket-creation/game", replay, new LoginRequested(new string('a', 64)), default);
            Assert.True(replay.Connection.IsClosed);
            Assert.False(replay.TryFeature<ISessionLanguage>(out _));
            Assert.Single(success.Sent);

            var unsolicited = new Session(3);
            await router.DispatchAsync("ticket-creation/game", unsolicited, new CharacterLoginRequested(), default);
            Assert.True(unsolicited.Connection.IsClosed);
            Assert.False(unsolicited.TryFeature<IPlayerSession>(out _));
            Assert.Equal(2, tickets.Calls);
        }
        finally { await lobbyContext.UnwindAsync(); await routerContext.UnwindAsync(); }
        Assert.False(session.TryFeature<ISessionLanguage>(out _));

        PluginContext Context(string id, Dictionary<string, object?>? config = null) =>
            new(new PluginRow { Id = id, Plugin = id, Source = "ticket-creation-test", Config = config },
                services, events, NullLogger.Instance);
    }

    private sealed class Tickets : ILauncherTicketService
    {
        internal int Calls { get; private set; }
        public string? Redeem(string ticket, string build) => RedeemIdentity(ticket, build)?.Username;
        public LauncherTicketIdentity? RedeemIdentity(string ticket, string build) =>
            ++Calls == 1 && ticket == new string('a', 64) && build == "kr-1256"
                ? new("verified-account", "zh-CN", "launch-1") : null;
    }

    private sealed class Wire<T> : IInboundMessageBinding<T>, IMessageSender<T> where T : IMessage
    {
        internal List<T> Sent { get; } = [];
        public string Protocol => "ticket-creation/game";
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        { Sent.Add(message); return ValueTask.CompletedTask; }
    }

    private sealed class Session(long id) : ISessionWithFeatures
    {
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public long Id => id;
        public IConnection Connection { get; } = new Connection(id);
        public ClientProfile Profile { get; } = new("kr-1256", "kr", 1256, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
            throw new InvalidOperationException("This test dispatches typed messages without a wire codec.");
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
