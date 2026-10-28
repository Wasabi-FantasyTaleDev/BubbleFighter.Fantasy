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
    /// <summary>Committed membership requests a scene move once, and a replayed accept cannot move again.</summary>
    [Fact]
    public async Task AcceptFollowsTheCommittedPartyLeaderOnce()
    {
        await using var rig = await Rig.StartAsync();
        await rig.InviteAsync();
        await rig.AcceptAsync();
        var follow = Assert.Single(rig.SceneFollows);
        Assert.Same(rig.B, follow.Session);
        Assert.Equal(1, follow.Request.LeaderUserNum);
        await rig.AcceptAsync();
        Assert.Single(rig.SceneFollows);
    }

    /// <summary>A build without a visit-answer packet joins and follows directly instead of sending an unusable prompt.</summary>
    [Fact]
    public async Task DirectVisitJoinsAndFollowsWithoutAskingTheHost()
    {
        await using var rig = await Rig.StartAsync();
        await rig.SendAsync(rig.A, new PartyVisitRequested("Bo", RequiresApproval: false));
        Assert.DoesNotContain(rig.Sent, sent => sent.Message is PartyVisitAsked or PartyVisitGranted);
        Assert.Equal(new[] { 2, 1 }, rig.Parties.PartyOf(1)!.Members.Select(member => member.UserNum));
        var follow = Assert.Single(rig.SceneFollows);
        Assert.Same(rig.A, follow.Session);
        Assert.Equal(2, follow.Request.LeaderUserNum);
        Assert.True(follow.Request.AcknowledgeVisit);
    }

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

    /// <summary>
    /// A visit asks the visited player; an accepted visit becomes an invitation from the visited player that the
    /// visitor's client accepts by itself, joining the visitor to the visited player's party.
    /// </summary>
    [Fact]
    public async Task AnAcceptedVisitJoinsTheVisitorToTheHostsParty()
    {
        await using var rig = await Rig.StartAsync();
        await rig.SendAsync(rig.A, new PartyVisitRequested("Bo"));
        var asked = Assert.IsType<PartyVisitAsked>(rig.Sent.Last().Message);
        Assert.Same(rig.B, rig.Sent.Last().Session);
        Assert.Equal("Al", asked.Visitor);
        Assert.NotEqual(0u, asked.Token);
        await rig.SendAsync(rig.B, new PartyVisitTokenAnswered(asked.Token, true));
        Assert.Equal(new PartyVisitGranted("Bo"), rig.Sent.Last().Message);
        Assert.Same(rig.A, rig.Sent.Last().Session);
        await rig.SendAsync(rig.A, new PartyInviteAccepted(true, "Bo"));
        var party = rig.Parties.PartyOf(1)!;
        Assert.Equal(new[] { 2, 1 }, party.Members.Select(member => member.UserNum));
    }

    /// <summary>The legacy answer names the visitor; a declined visit tells the visitor and leaves no invitation.</summary>
    [Fact]
    public async Task ADeclinedVisitTellsTheVisitorAndCannotBeAcceptedLater()
    {
        await using var rig = await Rig.StartAsync();
        await rig.SendAsync(rig.A, new PartyVisitRequested("Bo"));
        await rig.SendAsync(rig.B, new PartyVisitAnswered("Al", false));
        Assert.Equal(new PartyVisitFailed(PartyVisitFailure.Declined, "Bo"), rig.Sent.Last().Message);
        await rig.SendAsync(rig.A, new PartyInviteAccepted(true, "Bo"));
        Assert.Null(rig.Parties.PartyOf(1));
        var count = rig.Sent.Count;
        await rig.SendAsync(rig.B, new PartyVisitAnswered("Al", true));
        Assert.Equal(count, rig.Sent.Count);
    }

    /// <summary>A visit that cannot be asked fails at once: oneself, an absent player, or the visitor in a round.</summary>
    [Fact]
    public async Task AVisitThatCannotBeAskedFailsAtOnce()
    {
        await using var rig = await Rig.StartAsync();
        await rig.SendAsync(rig.A, new PartyVisitRequested("Al"));
        Assert.Equal(new PartyVisitFailed(PartyVisitFailure.Self, "Al"), rig.Sent.Last().Message);
        await rig.SendAsync(rig.A, new PartyVisitRequested("Nobody"));
        Assert.Equal(new PartyVisitFailed(PartyVisitFailure.Offline, "Nobody"), rig.Sent.Last().Message);
        await rig.InviteAsync();
        await rig.AcceptAsync();
        await rig.SendAsync(rig.B, new PartyVisitRequested("Al"));
        Assert.Equal(new PartyVisitFailed(PartyVisitFailure.SameParty, "Al"), rig.Sent.Last().Message);
    }

    /// <summary>Only the visited player's own connection can answer its prompt, once.</summary>
    [Fact]
    public async Task AVisitTokenBelongsToTheVisitedPlayerOnly()
    {
        await using var rig = await Rig.StartAsync();
        await rig.SendAsync(rig.A, new PartyVisitRequested("Bo"));
        var token = Assert.IsType<PartyVisitAsked>(rig.Sent.Last().Message).Token;
        var count = rig.Sent.Count;
        await rig.SendAsync(rig.A, new PartyVisitTokenAnswered(token, true));
        await rig.SendAsync(rig.B, new PartyVisitTokenAnswered(token + 1000, true));
        Assert.Equal(count, rig.Sent.Count);
        await rig.SendAsync(rig.B, new PartyVisitTokenAnswered(token, false));
        Assert.Equal(new PartyVisitFailed(PartyVisitFailure.Declined, "Bo"), rig.Sent.Last().Message);
        await rig.SendAsync(rig.B, new PartyVisitTokenAnswered(token, true));
        Assert.Equal(count + 1, rig.Sent.Count);
    }

    private sealed class Rig : IAsyncDisposable
    {
        internal ServiceRegistry Services { get; } = new();
        internal EventBus Events { get; } = new(NullLogger.Instance);
        internal Online Online { get; } = new();
        internal Session A { get; } = new(1, "Al", 1);
        internal Session B { get; } = new(2, "Bo", 2);
        internal ConcurrentQueue<(Session Session, IMessage Message)> Sent { get; } = new();
        internal List<(ISession Session, PartySceneFollowRequested Request)> SceneFollows { get; } = [];
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
            Register<PartyVisitAsked>(); Register<PartyVisitFailed>(); Register<PartyVisitGranted>();
        }
        internal static async Task<Rig> StartAsync(int inviteTimeoutMs = 30000)
        {
            var rig = new Rig(inviteTimeoutMs);
            await new MessageRouterPlugin().ActivateAsync(rig._routerContext, default);
            rig.Context.Effect(rig.Services.Get<IMessageRouter>().For(rig.Context).On<PartySceneFollowRequested>("test/game", (session, request, _) =>
            {
                rig.SceneFollows.Add((session, request));
                return ValueTask.CompletedTask;
            }));
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
