using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Party.Tests;

/// <summary>Exercises concurrent requests and connection lifetimes through the mounted party row.</summary>
public sealed class PartyPluginTests
{
    [Fact]
    public async Task RoomAcceptCannotConsumePartyInvitation()
    {
        await using var rig = await Rig.StartAsync();
        await rig.InviteAsync();
        await rig.SendAsync(rig.B, new PartyInviteAccepted(false, "Al"));
        Assert.Null(rig.Parties.PartyOf(2));
        await rig.AcceptAsync();
        Assert.Equal(2, rig.Parties.PartyOf(2)!.Members.Count);
    }

    [Fact]
    public async Task UnsupportedAnswerCannotConsumePartyInvitation()
    {
        await using var rig = await Rig.StartAsync();
        await rig.InviteAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => rig.SendAsync(rig.B, new PartyInviteAnswered("Al", 99)));
        await rig.AcceptAsync();
        Assert.Equal(2, rig.Parties.PartyOf(2)!.Members.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReconnectingEitherEndpointInvalidatesPendingInvitation(bool inviter)
    {
        await using var rig = await Rig.StartAsync();
        await rig.InviteAsync();
        var old = inviter ? rig.A : rig.B;
        await old.Connection.CloseAsync("test reconnect");
        rig.Events.Emit(new ConnectionClosedEvent(old.Connection, "test reconnect"));
        var replacement = new Session(inviter ? 1 : 2, inviter ? "Al" : "Bo", 10);
        rig.Online.Players[inviter ? 1 : 2] = replacement;
        await rig.SendAsync(inviter ? rig.B : replacement, new PartyInviteAccepted(true, "Al"));
        Assert.Null(rig.Parties.PartyOf(2));
    }

    [Fact]
    public async Task LeaveCannotOvertakeAnAcceptedInvitationSnapshot()
    {
        await using var rig = await Rig.StartAsync();
        await rig.InviteAsync();
        var entered = Signal();
        var release = Signal();
        rig.BeforeSend = async (_, message, ct) =>
        {
            if (message is PartyUpdated { Party.Members.Count: 2 } && entered.TrySetResult())
                await release.Task.WaitAsync(ct);
        };
        var join = rig.AcceptAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var leave = rig.SendAsync(rig.B, new PartyLeaveRequested());
        try { Assert.False(leave.IsCompleted); }
        finally { release.TrySetResult(); }
        await Task.WhenAll(join, leave).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(rig.Parties.PartyOf(1));
        Assert.False(rig.A.TryFeature<IPartyMembership>(out _));
        Assert.False(rig.B.TryFeature<IPartyMembership>(out _));
        Assert.All(new[] { rig.A, rig.B }, session =>
            Assert.Single(rig.Sent.Where(s => s.Session == session).Select(s => s.Message)
                .OfType<PartyUpdated>().Last().Party.Members));
    }

    [Fact]
    public async Task UnloadCancelsAndJoinsDisconnectDelivery()
    {
        await using var rig = await Rig.StartAsync();
        await rig.InviteAsync();
        await rig.AcceptAsync();
        var entered = Signal();
        var finished = Signal();
        rig.BeforeSend = async (_, _, ct) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { finished.TrySetResult(); }
        };
        await rig.B.Connection.CloseAsync("test close");
        rig.Events.Emit(new ConnectionClosedEvent(rig.B.Connection, "test close"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rig.Context.UnwindAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(finished.Task.IsCompleted);
        Assert.False(rig.A.TryFeature<IPartyMembership>(out _));
        Assert.False(rig.Services.TryGet<IPartyDirectory>(out _));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task FailedKickReplyCannotStrandTheKickedMembersFeature()
    {
        await using var rig = await Rig.StartAsync();
        await rig.InviteAsync();
        await rig.AcceptAsync();
        rig.BeforeSend = (_, message, _) => message is PartyKickReplied
            ? ValueTask.FromException(new IOException("Recipient stopped reading.")) : ValueTask.CompletedTask;
        await rig.SendAsync(rig.A, new PartyKickRequested("Bo"));
        Assert.True(rig.A.Connection.IsClosed);
        Assert.False(rig.B.TryFeature<IPartyMembership>(out _));
        Assert.Contains(rig.Sent, item => item.Session == rig.B && item.Message is PartyKicked);
        Assert.Single(rig.Sent.Where(item => item.Session == rig.B).Select(item => item.Message)
            .OfType<PartyUpdated>().Last().Party.Members);
    }

    [Fact]
    public async Task FailedInvitationPromptReportsOfflineAndInvalidatesInvitation()
    {
        await using var rig = await Rig.StartAsync();
        rig.BeforeSend = (_, message, _) => message is PartyInviteNotified
            ? ValueTask.FromException(new IOException("Target stopped reading.")) : ValueTask.CompletedTask;
        await rig.InviteAsync();
        Assert.True(rig.B.Connection.IsClosed);
        Assert.Equal(PartyInviteResult.Offline, Assert.Single(rig.Sent.Select(item => item.Message)
            .OfType<PartyInviteReplied>()).Result);
        var replacement = new Session(2, "Bo", 10);
        rig.Online.Players[2] = replacement;
        await rig.SendAsync(replacement, new PartyInviteAccepted(true, "Al"));
        Assert.Null(rig.Parties.PartyOf(2));
    }

    [Fact]
    public async Task LateCloseOfReplacedSessionCannotRemoveTheNewParty()
    {
        await using var rig = await Rig.StartAsync();
        await rig.InviteAsync();
        await rig.AcceptAsync();
        var replacement = new Session(2, "Bo", 10);
        rig.Online.Players[2] = replacement;
        await rig.InviteAsync();
        await rig.SendAsync(replacement, new PartyInviteAccepted(true, "Al"));
        await rig.B.Connection.CloseAsync("late old close");
        rig.Events.Emit(new ConnectionClosedEvent(rig.B.Connection, "late old close"));
        await rig.SendAsync(rig.A, new PartyLeaderChangeRequested("Bo"));
        Assert.Equal(2, rig.Parties.PartyOf(1)!.Leader.UserNum);
        Assert.True(replacement.TryFeature<IPartyMembership>(out _));
        Assert.False(rig.B.TryFeature<IPartyMembership>(out _));
    }

    [Fact]
    public async Task TokenRefusalClearsThePendingInvitationAndTellsTheInviter()
    {
        await using var rig = await Rig.StartAsync();
        await rig.InviteAsync();
        var token = rig.LastToken();
        Assert.NotEqual(0u, token);
        await rig.SendAsync(rig.B, new PartyInviteTokenRefused(token));
        var reply = Assert.Single(rig.Sent.Where(s => s.Session == rig.A).Select(s => s.Message).OfType<PartyInviteReplied>(),
            r => r.Result == PartyInviteResult.Refused);
        Assert.Equal("Bo", reply.Target);
        // The invitation is gone: a late accept has nothing to join.
        await rig.AcceptAsync();
        Assert.Null(rig.Parties.PartyOf(2));
    }

    [Fact]
    public async Task UnknownOrReplayedTokenIsIgnored()
    {
        await using var rig = await Rig.StartAsync();
        await rig.InviteAsync();
        var token = rig.LastToken();
        await rig.SendAsync(rig.B, new PartyInviteTokenRefused(token + 1000));
        await rig.SendAsync(rig.B, new PartyInviteTokenRefused(0));
        Assert.DoesNotContain(rig.Sent, s => s.Message is PartyInviteReplied { Result: PartyInviteResult.Refused });
        await rig.AcceptAsync();
        Assert.Equal(2, rig.Parties.PartyOf(2)!.Members.Count);
        // The accepted invitation's token no longer names anything.
        await rig.SendAsync(rig.B, new PartyInviteTokenRefused(token));
        Assert.DoesNotContain(rig.Sent, s => s.Message is PartyInviteReplied { Result: PartyInviteResult.Refused });
    }

    [Fact]
    public async Task ATokenBelongsToTheInvitedPlayerOnly()
    {
        await using var rig = await Rig.StartAsync();
        await rig.InviteAsync();
        // The inviter cannot refuse on the target's behalf with the target's token.
        await rig.SendAsync(rig.A, new PartyInviteTokenRefused(rig.LastToken()));
        Assert.DoesNotContain(rig.Sent, s => s.Message is PartyInviteReplied { Result: PartyInviteResult.Refused });
        await rig.AcceptAsync();
        Assert.Equal(2, rig.Parties.PartyOf(2)!.Members.Count);
    }

    [Fact]
    public async Task EveryInvitationGetsItsOwnToken()
    {
        await using var rig = await Rig.StartAsync();
        await rig.InviteAsync();
        var first = rig.LastToken();
        await rig.SendAsync(rig.B, new PartyInviteTokenRefused(first));
        await rig.InviteAsync();
        var second = rig.LastToken();
        Assert.NotEqual(first, second);
        // The first token cannot refuse the second invitation.
        await rig.SendAsync(rig.B, new PartyInviteTokenRefused(first));
        await rig.AcceptAsync();
        Assert.Equal(2, rig.Parties.PartyOf(2)!.Members.Count);
    }

    [Fact]
    public async Task ATokenFromAReplacementConnectionIsStale()
    {
        await using var rig = await Rig.StartAsync();
        await rig.InviteAsync();
        var token = rig.LastToken();
        await rig.B.Connection.CloseAsync("test reconnect");
        rig.Events.Emit(new ConnectionClosedEvent(rig.B.Connection, "test reconnect"));
        var replacement = new Session(2, "Bo", 10);
        rig.Online.Players[2] = replacement;
        await rig.SendAsync(replacement, new PartyInviteTokenRefused(token));
        Assert.DoesNotContain(rig.Sent, s => s.Message is PartyInviteReplied { Result: PartyInviteResult.Refused });
    }

    [Fact]
    public async Task AnExpiredInvitationIgnoresItsToken()
    {
        await using var rig = await Rig.StartAsync(inviteTimeoutMs: 1000);
        await rig.InviteAsync();
        var token = rig.LastToken();
        await Task.Delay(1300);
        await rig.SendAsync(rig.B, new PartyInviteTokenRefused(token));
        Assert.DoesNotContain(rig.Sent, s => s.Message is PartyInviteReplied { Result: PartyInviteResult.Refused });
        await rig.AcceptAsync();
        Assert.Null(rig.Parties.PartyOf(2));
    }

    private sealed class Rig : IAsyncDisposable
    {
        internal ServiceRegistry Services { get; } = new();
        internal EventBus Events { get; } = new(NullLogger.Instance);
        internal Online Online { get; } = new();
        internal Session A { get; } = new(1, "Al", 1);
        internal Session B { get; } = new(2, "Bo", 2);
        internal ConcurrentQueue<(Session Session, IMessage Message)> Sent { get; } = new();
        internal Func<ISession, IMessage, CancellationToken, ValueTask>? BeforeSend { get; set; }
        internal PluginContext Context { get; }
        private readonly PluginContext _routerContext;
        private readonly List<IDisposable> _services = [];
        private readonly int _inviteTimeoutMs;
        internal uint LastToken() => Sent.Select(s => s.Message).OfType<PartyInviteNotified>().Last().Token;
        internal IPartyDirectory Parties => Services.Get<IPartyDirectory>();
        private Rig(int inviteTimeoutMs)
        {
            _inviteTimeoutMs = inviteTimeoutMs;
            Context = ContextFor("party");
            _routerContext = ContextFor("core.message-router");
            Online.Players[1] = A;
            Online.Players[2] = B;
            _services.Add(Services.Register<IOnlinePlayers>(Online));
            Register<PartyInviteReplied>(); Register<PartyInviteNotified>(); Register<PartyUpdated>();
            Register<PartyLeaderReplaced>(); Register<PartyLeft>(); Register<PartyMemberLeft>();
            Register<PartyMemberJoined>(); Register<PartyKickReplied>(); Register<PartyKicked>();
            Register<PartyMemberKicked>(); Register<PartyLeaderChanged>();
        }
        internal static async Task<Rig> StartAsync(int inviteTimeoutMs = 30000)
        {
            var rig = new Rig(inviteTimeoutMs);
            await new MessageRouterPlugin().ActivateAsync(rig._routerContext, default);
            await new PartyPlugin().ActivateAsync(rig.Context, default);
            return rig;
        }
        private PluginContext ContextFor(string name) => new(new PluginRow
        {
            Id = name, Plugin = name, Source = "party-test",
            Config = name == "party" ? new Dictionary<string, object?> { ["protocol"] = "test/game", ["invite_timeout_ms"] = _inviteTimeoutMs } : null
        }, Services, Events, NullLogger.Instance);
        private void Register<T>() where T : IMessage =>
            _services.Add(Services.RegisterMany<IMessageSender<T>>(new Sender<T>(this)));
        internal Task InviteAsync() => SendAsync(A, new PartyInviteRequested(true, "Bo"));
        internal Task AcceptAsync() => SendAsync(B, new PartyInviteAccepted(true, "Al"));
        internal async Task SendAsync<T>(ISession session, T message) where T : IMessage =>
            Assert.True(await Services.Get<IMessageRouter>().DispatchAsync("test/game", session, message, default));
        public async ValueTask DisposeAsync()
        {
            await Context.UnwindAsync();
            await _routerContext.UnwindAsync();
            foreach (var service in _services) service.Dispose();
        }
        private sealed class Sender<T>(Rig rig) : IMessageSender<T> where T : IMessage
        {
            public string Protocol => "test/game";
            public async ValueTask SendAsync(ISession session, T message, CancellationToken ct)
            {
                if (rig.BeforeSend is { } before) await before(session, message, ct);
                ct.ThrowIfCancellationRequested();
                rig.Sent.Enqueue(((Session)session, message));
            }
        }
    }

    private sealed class Online : IOnlinePlayers
    {
        internal ConcurrentDictionary<int, Session> Players { get; } = new();
        public ISession? FindByUserNum(int userNum) =>
            Players.GetValueOrDefault(userNum) is { Connection.IsClosed: false } session ? session : null;
        public ISession? FindByNickname(string nickname) => Players.Values.FirstOrDefault(s =>
            !s.Connection.IsClosed && s.Feature<IPlayerSession>().Identity.Nickname == nickname);
    }

    private sealed class Session : ISessionWithFeatures
    {
        internal Session(int user, string nickname, long connection)
        {
            Id = connection;
            Connection = new Connection(connection);
            this.SetLobbyPlayer(user, nickname: nickname);
        }
        public long Id { get; }
        public IConnection Connection { get; }
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public ClientProfile Profile => new("test", "kr", 812, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Connection(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason) { IsClosed = true; return ValueTask.CompletedTask; }
    }
}
