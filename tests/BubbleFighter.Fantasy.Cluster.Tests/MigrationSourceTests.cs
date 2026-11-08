using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

public sealed class MigrationSourceTests
{
    [Fact]
    public async Task FurtherRoomVisitsPreserveTheFirstLoginNodeAndNativeChannel()
    {
        await using var rig = new Rig(away: true);
        await rig.Begin();
        Assert.Equal("home", rig.Broker.Request!.HomeNode);
        Assert.Equal(80, rig.Broker.Request.SourceChannel);
    }

    [Fact]
    public async Task APartyLeaderMovesFirstAndEachIdleMemberIsOfferedTheSameRoom()
    {
        await using var rig = new Rig();
        rig.Party((8, SessionPhase.Plaza, null), (9, SessionPhase.MainMenu, null));
        Assert.Null(await rig.Begin());
        Assert.Equal(new[] { "reserve", "handoff", "offer", "reserve", "handoff", "offer", "reserve", "handoff", "offer" }, rig.Events);
        Assert.Equal(new long[] { 1, 8, 9 }, rig.Sender.Recipients);
        Assert.Equal(new long[] { 7, 8, 9 }, rig.Broker.Requests.Select(request => request.UserNum));
        Assert.All(rig.Broker.Requests, request =>
        {
            Assert.Equal(7, request.PartyLeader);
            Assert.Equal(rig.Target.InstanceId, request.Room!.InstanceId);
            Assert.Equal(request.UserNum, request.Room.UserNumber);
            Assert.Equal("secret", request.Room.Password);
        });
        Assert.False(rig.Parties.Reserved);
        Assert.All(rig.Members, member => Assert.Equal(SessionPhase.Migrating, member.Player.Phase));
    }

    [Fact]
    public async Task AMemberInARoundOrRefusedStaysWithoutUndoingTheLeadersMove()
    {
        await using var rig = new Rig();
        rig.Party((8, SessionPhase.Match, null), (9, SessionPhase.MainMenu, null));
        rig.Broker.AfterRequest = () =>
        {
            if (rig.Broker.Request!.UserNum == 9) rig.Broker.Offer = new(null, 0, RoomJoinRefusal.Full, TimeSpan.Zero);
        };
        Assert.Null(await rig.Begin());
        Assert.Equal(new long[] { 7, 9 }, rig.Broker.Requests.Select(request => request.UserNum));
        Assert.Equal(new long[] { 1 }, rig.Sender.Recipients);
        Assert.Equal(SessionPhase.Migrating, rig.Player.Phase);
        Assert.Equal(SessionPhase.Match, rig.Members[0].Player.Phase);
        Assert.Equal(SessionPhase.MainMenu, rig.Members[1].Player.Phase);
        Assert.True(rig.Members[1].Account.Held);
    }

    [Fact]
    public async Task AMemberCannotLeaveForARemoteRoomWithoutItsLeader()
    {
        await using var rig = new Rig();
        rig.Party((8, SessionPhase.MainMenu, null));
        var member = rig.Members[0];
        Assert.Equal(RoomJoinRefusal.NoRoom, await rig.Source.BeginAsync(member.Session, rig.Target, "", default));
        Assert.Empty(rig.Events);
    }

    [Fact]
    public async Task OnlyMembersFromTheLeadersHomeReturnWithIt()
    {
        await using var rig = new Rig(away: true);
        rig.Party((8, SessionPhase.Plaza, "home"), (9, SessionPhase.MainMenu, "elsewhere"), (10, SessionPhase.MainMenu, null));
        await rig.Source.ReturnHomeAsync(rig.Session, default);
        Assert.Equal(new long[] { 7, 8 }, rig.Broker.Requests.Select(request => request.UserNum));
        Assert.All(rig.Broker.Requests, request => Assert.Equal(MigrationKind.Home, request.Kind));
        Assert.Equal(SessionPhase.Plaza, rig.Broker.Requests[1].HomeScene);
        Assert.Equal(7, rig.Broker.Requests[1].PartyLeader);
        Assert.Equal(0, rig.Notices.Count);
    }

    [Fact]
    public async Task ASoloMoveCarriesNoPartyLeaderOnTheNodeTransport()
    {
        await using var rig = new Rig();
        await rig.Begin();
        Assert.Null(rig.Broker.Request!.PartyLeader);
        var json = System.Text.Json.JsonSerializer.Serialize(rig.Broker.Request, MigrationTransport.Json);
        Assert.DoesNotContain("party_leader", json, StringComparison.Ordinal);
        var party = System.Text.Json.JsonSerializer.Serialize(rig.Broker.Request with { PartyLeader = 7 }, MigrationTransport.Json);
        Assert.Contains("\"party_leader\":7", party, StringComparison.Ordinal);
        Assert.Equal(7, System.Text.Json.JsonSerializer.Deserialize<MigrationRequest>(party, MigrationTransport.Json)!.PartyLeader);
        Assert.True(MigrationTransport.Valid(rig.Broker.Request with { PartyLeader = 7 }));
        Assert.False(MigrationTransport.Valid(rig.Broker.Request with { PartyLeader = 0 }));
    }

    [Theory]
    [InlineData(SessionPhase.MainMenu)]
    [InlineData(SessionPhase.Plaza)]
    public async Task HomeReturnResolvesTheLiveEpochBeforeHandingOffWithoutARoomReservation(SessionPhase scene)
    {
        await using var rig = new Rig(away: true);
        rig.Player.MoveTo(scene);
        await rig.Source.ReturnHomeAsync(rig.Session, default);
        Assert.Equal(new[] { "resolve", "reserve", "handoff", "offer" }, rig.Events);
        Assert.Equal(MigrationKind.Home, rig.Broker.Request!.Kind);
        Assert.Null(rig.Broker.Request.Room);
        Assert.Equal("home", rig.Broker.Request.DestinationNode);
        Assert.Equal(7, rig.Broker.Request.DestinationEpoch);
        Assert.Equal(80, rig.Broker.Request.SourceChannel);
        Assert.Equal(scene, rig.Broker.Request.HomeScene);
        Assert.Equal(SessionPhase.Migrating, rig.Player.Phase);
        Assert.Equal(0, rig.Notices.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableHomeOrPartyMembershipLeavesThePlayerWithAuthorityAndANativeNotice(bool party)
    {
        await using var rig = new Rig(away: true);
        rig.Parties.Occupied = party;
        if (!party) rig.Targets.Target = null;
        await rig.Source.ReturnHomeAsync(rig.Session, default);
        Assert.Equal(new[] { "resolve" }, rig.Events);
        Assert.Equal(1, rig.Notices.Count);
        Assert.True(rig.Account.Held);
        Assert.Equal(SessionPhase.MainMenu, rig.Player.Phase);
    }

    [Fact]
    public async Task ABuildWithoutNativeNoticesRetainsAuthorityWhenItsHomeIsUnavailable()
    {
        await using var rig = new Rig(away: true, nativeNotices: false);
        rig.Targets.Target = null;
        await rig.Source.ReturnHomeAsync(rig.Session, default);
        Assert.Equal(new[] { "resolve" }, rig.Events);
        Assert.Equal(0, rig.Notices.Count);
        Assert.True(rig.Account.Held);
        Assert.Equal(SessionPhase.MainMenu, rig.Player.Phase);
    }

    [Fact]
    public async Task HomeReturnDoesNothingForOrdinaryLogins()
    {
        await using var ordinary = new Rig();
        await ordinary.Source.ReturnHomeAsync(ordinary.Session, default);
        Assert.Empty(ordinary.Events);
    }

    [Fact]
    public async Task SeatThenAuthorityThenOfferAndExactWithdrawalRestoreTheSameScene()
    {
        await using var rig = new Rig();
        rig.Player.MoveTo(SessionPhase.Plaza);
        Assert.Null(await rig.Begin());
        Assert.Equal(new[] { "reserve", "handoff", "offer" }, rig.Events);
        Assert.Equal(SessionPhase.Migrating, rig.Player.Phase);
        Assert.True(rig.Parties.Reserved);
        Assert.False(rig.Account.Held);
        Assert.Equal(IPAddress.Parse("192.0.2.20"), rig.Broker.Request!.ClientAddress);
        Assert.Equal("a", rig.Broker.Request.HomeNode);
        Assert.Equal(SessionPhase.MainMenu, rig.Broker.Request.HomeScene);
        Assert.Equal(50, rig.Broker.Request.SourceChannel);
        Assert.Equal(rig.Broker.Request.MigrationId, rig.Handoff.Record!.MigrationId);
        Assert.Equal(rig.Target.InstanceId, rig.Broker.Request.Room!.InstanceId);
        Assert.Equal("secret", rig.Broker.Request.Room.Password);
        rig.Clock.Advance(11);
        await rig.Source.SweepAsync(default);
        Assert.Equal(new[] { "reserve", "handoff", "offer", "cancel", "withdraw" }, rig.Events);
        Assert.Equal(SessionPhase.Plaza, rig.Player.Phase);
        Assert.True(rig.Account.Held);
        Assert.False(rig.Parties.Reserved);
        Assert.False(rig.Session.Connection.IsClosed);
    }

    [Fact]
    public async Task ClaimedHandoffClosesSourceWithoutRegainingAuthority()
    {
        await using var rig = new Rig();
        await rig.Begin();
        rig.Handoff.Claimed = true;
        rig.Clock.Advance(11);
        await rig.Source.SweepAsync(default);
        Assert.True(rig.Session.Connection.IsClosed);
        Assert.False(rig.Account.Held);
        Assert.False(rig.Parties.Reserved);
    }

    [Theory]
    [InlineData("party")]
    [InlineData("closed")]
    [InlineData("replaced")]
    [InlineData("room")]
    [InlineData("relay")]
    [InlineData("lease")]
    [InlineData("local")]
    [InlineData("denied")]
    [InlineData("ipv6")]
    public async Task IneligibleSourcesNeverReserveOrHandOff(string reason)
    {
        await using var rig = new Rig();
        switch (reason)
        {
            case "party": rig.Parties.Occupied = true; break;
            case "closed": await rig.Session.Connection.CloseAsync("test"); break;
            case "replaced": rig.Online.ByUser.Remove(7); break;
            case "room": rig.Player.MoveTo(SessionPhase.Room); break;
            case "relay": rig.Session.Features = new SessionFeatures(8); rig.Session.RegisterFeature<IPlayerSession>(new PlayerSession(new(7, "u", "n", 1), 9)); break;
            case "lease": rig.Leases.Current = null; break;
            case "local": rig.Target = rig.Target with { Local = true }; break;
            case "denied": rig.Target = rig.Target with { JoinAllowed = false }; break;
            case "ipv6": rig.Session.Socket.RemoteEndPoint = new IPEndPoint(IPAddress.IPv6Loopback, 1); break;
        }
        Assert.Equal(RoomJoinRefusal.NoRoom, await rig.Begin());
        Assert.Empty(rig.Events);
        Assert.False(rig.Parties.Reserved);
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("lost-reply")]
    [InlineData("stale-lease")]
    [InlineData("stale-scene")]
    [InlineData("handoff")]
    [InlineData("offer")]
    public async Task FailuresCancelTheReservationAndNeverLeaveTwoWriters(string failure)
    {
        await using var rig = new Rig();
        switch (failure)
        {
            case "refused": rig.Broker.Offer = new(null, 0, RoomJoinRefusal.WrongPassword, TimeSpan.Zero); break;
            case "lost-reply": rig.Broker.FailRequest = true; break;
            case "stale-lease": rig.Broker.AfterRequest = () => rig.Leases.Current = new("changed", 2); break;
            case "stale-scene": rig.Broker.AfterRequest = () => rig.Player.MoveTo(SessionPhase.Plaza); break;
            case "handoff": rig.Handoff.Fail = true; break;
            case "offer": rig.Sender.Fail = true; break;
        }
        if (failure == "refused") Assert.Equal(RoomJoinRefusal.WrongPassword, await rig.Begin());
        else await Assert.ThrowsAnyAsync<Exception>(() => rig.Begin().AsTask());
        Assert.Contains("cancel", rig.Events);
        Assert.False(rig.Parties.Reserved);
        if (failure is "stale-lease" or "stale-scene") Assert.True(rig.Session.Connection.IsClosed);
        else
        {
            Assert.Equal(SessionPhase.MainMenu, rig.Player.Phase);
            Assert.True(rig.Account.Held);
        }
        Assert.Equal(failure == "offer", rig.Events.Contains("withdraw"));
    }

    [Fact]
    public async Task CleanupFailureRetainsSoloGuardAndCapacityUntilCancellationCanRetry()
    {
        await using var rig = new Rig();
        await rig.Begin();
        rig.Clock.Advance(11);
        rig.Broker.FailCancel = true;
        await rig.Source.SweepAsync(default);
        Assert.True(rig.Parties.Reserved);
        Assert.Equal(SessionPhase.Migrating, rig.Player.Phase);
        Assert.False(rig.Account.Held);
        Assert.DoesNotContain("withdraw", rig.Events);
        rig.Broker.FailCancel = false;
        await rig.Source.SweepAsync(default);
        Assert.False(rig.Parties.Reserved);
        Assert.True(rig.Account.Held);
    }

    [Fact]
    public async Task DuplicateDepartureDoesNotCreateAnotherReservation()
    {
        await using var rig = new Rig();
        await rig.Begin();
        Assert.Equal(RoomJoinRefusal.NoRoom, await rig.Begin());
        Assert.Equal(1, rig.Events.Count(value => value == "reserve"));
    }

    [Fact]
    public async Task DisconnectedSourceCancelsWithoutTryingToRestoreAnEndedConnection()
    {
        await using var rig = new Rig();
        await rig.Begin();
        await rig.Session.Connection.CloseAsync("promoted");
        await rig.Source.SweepAsync(default);
        Assert.Contains("cancel", rig.Events);
        Assert.DoesNotContain("withdraw", rig.Events);
        Assert.False(rig.Parties.Reserved);
    }

    [Fact]
    public async Task UnloadRetiresPendingSourceAndReportsCleanupFailure()
    {
        var rig = new Rig();
        await rig.Begin();
        rig.Broker.FailCancel = true;
        await Assert.ThrowsAsync<AggregateException>(() => rig.Source.DisposeAsync().AsTask());
        Assert.True(rig.Parties.Reserved);
        rig.Broker.FailCancel = false;
        await rig.DisposeAsync();
        Assert.True(rig.Session.Connection.IsClosed);
        Assert.False(rig.Parties.Reserved);
    }

    private sealed class Rig : IAsyncDisposable
    {
        internal readonly List<string> Events = [];
        internal readonly Session Session = new();
        internal readonly PlayerSession Player;
        internal readonly Account Account = new();
        internal readonly Leases Leases = new();
        internal readonly Parties Parties = new();
        internal readonly Clock Clock = new();
        internal readonly Online Online;
        internal readonly Broker Broker;
        internal readonly Handoff Handoff;
        internal readonly Sender Sender;
        internal readonly MigrationSource Source;
        internal readonly MigrationSourceOptions Options = new() { ClientBuild = "kr-812", Channel = 50, MaxMigrations = 1 };
        internal readonly List<(Session Session, PlayerSession Player, Account Account)> Members = [];
        internal readonly Targets Targets;
        internal readonly Notices Notices = new();
        internal RoomBrowserSelection Target = new(false, "b", 3, 12, Guid.NewGuid(), true);
        internal Rig(bool away = false, bool nativeNotices = true)
        {
            Player = new(new(7, "u", "n", 1), Session.Connection.Id);
            if (away) Player.SetMigrationHome(new("home", 80));
            Player.MoveTo(SessionPhase.MainMenu);
            Session.RegisterFeature<IPlayerSession>(Player);
            Session.RegisterFeature<IAccountSession>(Account);
            Online = new() { Session = Session };
            Online.ByUser[7] = Session;
            Broker = new(Events);
            Handoff = new(Events);
            Sender = new(Events);
            Targets = new(Events);
            Options.HomeUnavailableNoticeEnabled = nativeNotices;
            Source = new(Options, Broker, Leases, Handoff, Online, Parties, Sender, null, Clock, NullLogger.Instance, Targets, nativeNotices ? Notices : null);
        }
        internal ValueTask<RoomJoinRefusal?> Begin() => Source.BeginAsync(Session, Target, "secret", default);

        // Puts the rig's player at the head of a party whose other members are online on this node.
        internal void Party(params (int UserNum, SessionPhase Phase, string? Home)[] members)
        {
            Options.MaxMigrations = members.Length + 1;
            Parties.Party = new([new(7, "n"), .. members.Select(member => new PartyMember(member.UserNum, $"m{member.UserNum}"))]);
            foreach (var (user, phase, home) in members)
            {
                var session = new Session(user);
                var player = new PlayerSession(new(user, "u", $"m{user}", 1), session.Connection.Id);
                if (home is not null) player.SetMigrationHome(new(home, 80));
                player.MoveTo(SessionPhase.MainMenu);
                if (phase == SessionPhase.Match) player.MoveTo(SessionPhase.Room);
                player.MoveTo(phase);
                var account = new Account(user);
                session.RegisterFeature<IPlayerSession>(player);
                session.RegisterFeature<IAccountSession>(account);
                Online.ByUser[user] = session;
                Members.Add((session, player, account));
            }
        }
        public ValueTask DisposeAsync() => Source.DisposeAsync();
    }

    private sealed class Targets(List<string> events) : IMigrationTargetDirectory
    {
        internal MigrationTarget? Target = new("home", 7);
        public ValueTask<MigrationTarget?> ResolveAsync(string nodeId, string clientBuild, CancellationToken cancellationToken)
        {
            Assert.Equal("home", nodeId);
            events.Add("resolve");
            return ValueTask.FromResult(Target);
        }
    }

    private sealed class Notices : IMessageSender<ServerNotice>
    {
        public string Protocol => "kr-812/game";
        internal int Count;
        public ValueTask SendAsync(ISession session, ServerNotice message, CancellationToken ct) { Count++; return ValueTask.CompletedTask; }
    }

    private sealed class Account(long userNum = 7) : IAccountSession
    {
        internal bool Held = true;
        public long UserNum => userNum;
        public ValueTask<bool> IsHeldAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Held);
    }

    private sealed class Handoff(List<string> events) : IAccountSessionHandoff
    {
        internal AccountHandoff? Record;
        internal readonly List<AccountHandoff> Records = [];
        internal bool Claimed;
        internal bool Fail;
        public ValueTask HandOffAsync(ISession session, AccountHandoff handoff, CancellationToken cancellationToken)
        {
            events.Add("handoff");
            var held = (Account)session.Feature<IAccountSession>();
            Assert.True(held.Held);
            if (Fail) throw new IOException("handoff failed before recording");
            Record = handoff;
            Records.Add(handoff);
            held.Held = false;
            return ValueTask.CompletedTask;
        }
        public ValueTask<bool> TryWithdrawAsync(ISession session, string migrationId, CancellationToken cancellationToken)
        {
            events.Add("withdraw");
            Assert.Contains(Records, record => record.MigrationId == migrationId);
            ((Account)session.Feature<IAccountSession>()).Held = !Claimed;
            return ValueTask.FromResult(!Claimed);
        }
        public ValueTask<bool> TryClaimAsync(ISession session, long userNum, string migrationId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Broker(List<string> events) : IMigrationBroker
    {
        internal MigrationRequest? Request;
        internal readonly List<MigrationRequest> Requests = [];
        internal MigrationOffer Offer = new(IPAddress.Parse("198.51.100.10"), 1304, null, TimeSpan.FromSeconds(30));
        internal Action? AfterRequest;
        internal bool FailRequest;
        internal bool FailCancel;
        public ValueTask<MigrationOffer> RequestAsync(MigrationRequest request, CancellationToken cancellationToken)
        {
            events.Add("reserve");
            Request = request;
            Requests.Add(request);
            AfterRequest?.Invoke();
            if (FailRequest) throw new IOException("lost reply");
            return ValueTask.FromResult(Offer);
        }
        public ValueTask CancelAsync(string destinationNode, string migrationId, CancellationToken cancellationToken)
        {
            events.Add("cancel");
            Assert.Contains(Requests, request => request.DestinationNode == destinationNode && request.MigrationId == migrationId);
            if (FailCancel) throw new IOException("cancel unavailable");
            return ValueTask.CompletedTask;
        }
        public IDisposable Serve(Func<MigrationRequest, CancellationToken, ValueTask<MigrationOffer>> accept,
            Func<string, CancellationToken, ValueTask> cancel) => throw new NotSupportedException();
    }

    private sealed class Sender(List<string> events) : IMessageSender<ChannelMoveOffered>
    {
        public string Protocol => "kr-812/game";
        internal bool Fail;
        internal readonly List<long> Recipients = [];
        public ValueTask SendAsync(ISession session, ChannelMoveOffered message, CancellationToken ct)
        {
            Assert.False(((Account)session.Feature<IAccountSession>()).Held);
            Recipients.Add(session.Connection.Id);
            events.Add("offer");
            if (Fail) throw new IOException("offer send failed");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Parties : IPartyDirectory
    {
        internal bool Occupied;
        internal bool Reserved;
        public bool SupportsSoloReservation => true;
        internal PartySnapshot? Party;
        public PartySnapshot? PartyOf(int userNum) => Party?.Members.Any(member => member.UserNum == userNum) == true ? Party : null;
        public IDisposable? TryReserveSolo(int userNum)
        {
            if (Occupied || Reserved || PartyOf(userNum) is not null) return null;
            Reserved = true;
            return new Release(this);
        }
        private sealed class Release(Parties parties) : IDisposable { public void Dispose() => parties.Reserved = false; }
    }

    private sealed class Leases : INodeLeaseSource
    {
        public string NodeId => "a";
        public string DirectoryUrl => "http://localhost/";
        public NodeLeaseCredentials? Current { get; set; } = new("token", 1);
    }

    private sealed class Online : IOnlinePlayers
    {
        internal ISession? Session;
        internal readonly Dictionary<int, ISession> ByUser = [];
        public ISession? FindByUserNum(int userNum) => ByUser.GetValueOrDefault(userNum);
        public ISession? FindByNickname(string nickname) => Session;
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    private sealed class Session(long id = 1) : ISessionWithFeatures
    {
        public ISessionFeatures Features { get; set; } = new SessionFeatures(8);
        public long Id => id;
        internal Socket Socket { get; } = new(id);
        public IConnection Connection => Socket;
        public ClientProfile Profile { get; } = new("kr-812", "kr", 812, 0, 1304, "kr-812");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Socket(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint { get; set; } = new IPEndPoint(IPAddress.Parse("::ffff:192.0.2.20"), 40000);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason) { IsClosed = true; return ValueTask.CompletedTask; }
    }
}
