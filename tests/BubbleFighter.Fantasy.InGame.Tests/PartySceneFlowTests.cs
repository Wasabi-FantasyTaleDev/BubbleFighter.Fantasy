using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.InGame;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Party moves reuse the shared room and plaza owners with individual seats and live membership checks.</summary>
public sealed class PartySceneFlowTests
{
    /// <summary>An invitation accepted after the leader has already arrived follows that scene immediately.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewlyJoinedMemberFollowsExistingLeaderScene(bool roomScene)
    {
        await using var rig = new Rig();
        var leader = rig.Player(11);
        var member = rig.Player(22);
        if (roomScene) await rig.Flow.HandleCreateAsync(leader, new("existing", "", 1, "map"), default);
        else await rig.Flow.HandlePlazaEnterAsync(leader, new(), default);
        rig.Party = new([new(11, "player11"), new(22, "player22")]);
        await rig.Flow.HandlePartySceneFollowAsync(member, new(11), default);
        Assert.False(member.Connection.IsClosed);
        if (roomScene)
        {
            Assert.Same(rig.Registry.TryGet(leader.Id), rig.Registry.TryGet(member.Id));
            Assert.True(Assert.Single(rig.Entered.To(member)).PartyTransition);
        }
        else
        {
            Assert.Equal(SessionPhase.Plaza, member.Features.Get<IPlayerSession>().Phase);
            Assert.True(Assert.Single(rig.Plaza.To(member)).PartyTransition);
        }
    }

    /// <summary>A new party membership does not bypass password, capacity or active-round restrictions.</summary>
    [Theory]
    [InlineData("password")]
    [InlineData("full")]
    [InlineData("started")]
    public async Task NewMembershipDoesNotBypassRoomAdmission(string restriction)
    {
        await using var rig = new Rig(seats: restriction == "full" ? 1 : 4);
        var leader = rig.Player(11);
        var member = rig.Player(22);
        await rig.Flow.HandlePlazaEnterAsync(member, new(), default);
        await rig.Flow.HandleCreateAsync(leader, new("existing", restriction == "password" ? "key" : "", 1, "map"), default);
        if (restriction == "started") rig.Registry.TryGet(leader.Id)!.MarkPlaying();
        rig.Party = new([new(11, "player11"), new(22, "player22")]);
        await rig.Flow.HandlePartySceneFollowAsync(member, new(11), default);
        Assert.Null(rig.Registry.TryGet(member.Id));
        Assert.Equal(SessionPhase.Plaza, member.Features.Get<IPlayerSession>().Phase);
        Assert.Empty(rig.Entered.To(member));
        Assert.Empty(rig.PlazaDepartures.To(leader));
    }

    /// <summary>The member returns to the leader's menu without pulling other members through another scene.</summary>
    [Fact]
    public async Task NewlyJoinedMemberCanFollowLeaderBackToMenu()
    {
        await using var rig = new Rig();
        var leader = rig.Player(11);
        var member = rig.Player(22);
        await rig.Flow.HandlePlazaEnterAsync(member, new(), default);
        rig.Party = new([new(11, "player11"), new(22, "player22")]);
        await rig.Flow.HandlePartySceneFollowAsync(member, new(11), default);
        Assert.Equal(SessionPhase.MainMenu, member.Features.Get<IPlayerSession>().Phase);
        Assert.True(Assert.Single(rig.Menu.To(member)).PartyTransition);
        Assert.Empty(rig.Menu.To(leader));
    }

    /// <summary>A new party member can leave a waiting room and follow the leader to any idle scene.</summary>
    [Theory]
    [InlineData("room")]
    [InlineData("plaza")]
    [InlineData("menu")]
    public async Task MemberInAnotherRoomFollowsLeader(string scene)
    {
        await using var rig = new Rig();
        var leader = rig.Player(11);
        var member = rig.Player(22);
        await rig.Flow.HandleCreateAsync(member, new("source", "", 1, "map"), default);
        if (scene == "room") await rig.Flow.HandleCreateAsync(leader, new("target", "", 1, "map"), default);
        else if (scene == "plaza") await rig.Flow.HandlePlazaEnterAsync(leader, new(), default);
        rig.Party = new([new(11, "player11"), new(22, "player22")]);
        await rig.Flow.HandlePartySceneFollowAsync(member, new(11), default);
        Assert.False(member.Connection.IsClosed);
        if (scene == "room") Assert.Same(rig.Registry.TryGet(leader.Id), rig.Registry.TryGet(member.Id));
        else
        {
            Assert.Null(rig.Registry.TryGet(member.Id));
            Assert.Equal(scene == "plaza" ? SessionPhase.Plaza : SessionPhase.MainMenu,
                member.Features.Get<IPlayerSession>().Phase);
        }
    }

    /// <summary>A rejected invitation destination does not remove the member's existing room seat.</summary>
    [Theory]
    [InlineData("password")]
    [InlineData("full")]
    [InlineData("started")]
    public async Task RefusedDestinationRetainsOriginalRoom(string restriction)
    {
        await using var rig = new Rig(seats: restriction == "full" ? 1 : 4);
        var leader = rig.Player(11);
        var member = rig.Player(22);
        await rig.Flow.HandleCreateAsync(member, new("source", "", 1, "map"), default);
        var source = rig.Registry.TryGet(member.Id);
        await rig.Flow.HandleCreateAsync(leader, new("target", restriction == "password" ? "key" : "", 1, "map"), default);
        if (restriction == "started") rig.Registry.TryGet(leader.Id)!.MarkPlaying();
        rig.Party = new([new(11, "player11"), new(22, "player22")]);
        await rig.Flow.HandlePartySceneFollowAsync(member, new(11), default);
        Assert.Same(source, rig.Registry.TryGet(member.Id));
        Assert.Empty(rig.Left.To(member));
    }

    /// <summary>A departing member's reserved plaza slot receives no roster or movement traffic before installation.</summary>
    [Fact]
    public async Task PlazaReservationStaysInvisibleBetweenRoomDetachAndPlazaEntry()
    {
        await using var rig = new Rig(plazaCapacity: 3);
        var (leader, member) = rig.Pair();
        await rig.Flow.HandleCreateAsync(leader, new("party", "", 1, "map"), default);
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Left.BeforeSend = async (session, ct) =>
        {
            if (session != member) return;
            detached.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var leave = rig.Flow.HandleLeaveAsync(leader, new(), default).AsTask();
        try
        {
            await detached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(rig.Registry.TryGet(member.Id));
            var observer = rig.Player(33);
            await rig.Flow.HandlePlazaEnterAsync(observer, new(), default);
            Assert.DoesNotContain(rig.Plaza.To(observer).Single().Members, row => row.UserNumber == 22);
            await rig.Flow.HandlePlazaPositionAsync(observer, new(ReadOnlyMemory<byte>.Empty), default);
            await rig.Flow.HandlePlazaMenuReturnAsync(observer, new(), default);
            Assert.Empty(rig.Positions.To(member));
            Assert.Empty(rig.PlazaArrivals.To(member));
            Assert.Empty(rig.PlazaDepartures.To(member));
            release.TrySetResult();
            await leave.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(rig.Plaza.To(member));
        }
        finally { release.TrySetResult(); }
    }

    /// <summary>A scene operation queued on an old leader connection cannot create a room after reconnect.</summary>
    [Fact]
    public async Task ReplacedLeaderCannotRunItsQueuedSceneRequest()
    {
        await using var rig = new Rig();
        var (leader, member) = rig.Pair();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Entered.BeforeSend = async (session, ct) =>
        {
            if (session != member) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var create = rig.Flow.HandleCreateAsync(member, new("member", "", 1, "map"), default).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var queued = rig.Flow.HandleCreateAsync(leader, new("stale", "", 1, "map"), default).AsTask();
            Assert.False(queued.IsCompleted);
            rig.Player(11, sessionId: 111);
            release.TrySetResult();
            await Task.WhenAll(create, queued).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(rig.Registry.All());
            Assert.Null(rig.Registry.TryGet(leader.Id));
            Assert.Empty(rig.Entered.To(leader));
        }
        finally { release.TrySetResult(); }
    }

    /// <summary>Failure notifying an existing occupant never closes the successfully entering party member.</summary>
    [Fact]
    public async Task FailedObserverDoesNotAbortTheEnteringParty()
    {
        await using var rig = new Rig();
        var host = rig.Player(33);
        await rig.Flow.HandleCreateAsync(host, new("open", "", 1, "map"), default);
        var room = rig.Registry.TryGet(host.Id)!;
        var (leader, member) = rig.Pair();
        rig.Seats.BeforeSend = (session, _) => session == host
            ? ValueTask.FromException(new IOException("Observer closed.")) : ValueTask.CompletedTask;
        await rig.Flow.HandleJoinAsync(leader, new(room.Id, ""), default);
        Assert.True(host.Connection.IsClosed);
        Assert.False(leader.Connection.IsClosed);
        Assert.False(member.Connection.IsClosed);
        Assert.Same(room, rig.Registry.TryGet(member.Id));
        Assert.Single(rig.Entered.To(member));
    }

    /// <summary>The group does not leave an active round just because the leader exits it.</summary>
    [Fact]
    public async Task LeavingAnActiveRoundDoesNotPullThePartyOut()
    {
        await using var rig = new Rig();
        var (leader, member) = rig.Pair();
        await rig.Flow.HandleCreateAsync(leader, new("party", "", 1, "map"), default);
        var room = rig.Registry.TryGet(leader.Id)!;
        room.MarkPlaying();
        await rig.Flow.HandleLeaveAsync(leader, new(), default);
        Assert.Same(room, rig.Registry.TryGet(member.Id));
        Assert.Empty(rig.Plaza.To(member));
    }

    /// <summary>The leader's room, plaza and menu moves carry every available member through the same flow.</summary>
    [Fact]
    public async Task LeaderMovesTheGroupAcrossRoomPlazaAndMenu()
    {
        await using var rig = new Rig();
        var (leader, member) = rig.Pair();
        await rig.Flow.HandleCreateAsync(leader, new("party", "secret", 1, "map"), default);
        var room = rig.Registry.TryGet(leader.Id)!;
        Assert.Same(room, rig.Registry.TryGet(member.Id));
        Assert.Equal(0, rig.Entered.To(leader).Single().YourSlot);
        Assert.Equal(1, rig.Entered.To(member).Single().YourSlot);
        Assert.All(rig.Entered.Sent, item => Assert.True(item.Message.PartyTransition));
        Assert.Equal(new[] { 11, 22 }, rig.Entered.To(member).Single().Room.Seats.Select(seat => seat.UserNum));
        await rig.Flow.HandleLeaveAsync(leader, new(), default);
        Assert.Null(rig.Registry.TryGet(member.Id));
        Assert.All(new[] { leader, member }, player =>
        {
            Assert.True(rig.Plaza.To(player).Single().PartyTransition);
            Assert.Equal(SessionPhase.Plaza, player.Features.Get<IPlayerSession>().Phase);
        });
        Assert.NotEqual(rig.Plaza.To(leader).Single().You.Slot, rig.Plaza.To(member).Single().You.Slot);
        await rig.Flow.HandlePlazaMenuReturnAsync(leader, new(), default);
        Assert.All(new[] { leader, member }, player =>
        {
            Assert.True(rig.Menu.To(player).Single().PartyTransition);
            Assert.Equal(SessionPhase.MainMenu, player.Features.Get<IPlayerSession>().Phase);
        });
        await rig.Flow.HandlePlazaEnterAsync(leader, new(), default);
        Assert.Equal(2, rig.Plaza.To(member).Length);
        Assert.True(rig.Plaza.To(member).Last().PartyTransition);
    }

    /// <summary>A group uses the leader's accepted password and never bypasses a failed admission.</summary>
    [Fact]
    public async Task JoiningAProtectedRoomRequiresTheLeadersSuccessfulAdmission()
    {
        await using var rig = new Rig();
        var host = rig.Player(33);
        await rig.Flow.HandleCreateAsync(host, new("private", "key", 1, "map"), default);
        var room = rig.Registry.TryGet(host.Id)!;
        var (leader, member) = rig.Pair();
        await rig.Flow.HandleJoinAsync(leader, new(room.Id, "wrong"), default);
        Assert.Null(rig.Registry.TryGet(leader.Id));
        Assert.Null(rig.Registry.TryGet(member.Id));
        Assert.Equal(RoomJoinRefusal.WrongPassword, rig.Refused.To(leader).Single().Reason);
        await rig.Flow.HandleJoinAsync(leader, new(room.Id, "key"), default);
        Assert.Equal(1, rig.Entered.To(leader).Single().YourSlot);
        Assert.Equal(2, rig.Entered.To(member).Single().YourSlot);
        Assert.Same(room, rig.Registry.TryGet(member.Id));
    }

    /// <summary>A full or newly started target cannot pull a member out of its current scene.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableTargetRetainsTheFollowersScene(bool started)
    {
        await using var rig = new Rig(seats: started ? 4 : 1);
        var (leader, member) = rig.Pair();
        rig.Entered.BeforeSend = (session, _) =>
        {
            if (started && session == leader) rig.Registry.TryGet(leader.Id)!.MarkPlaying();
            return ValueTask.CompletedTask;
        };
        await rig.Flow.HandleCreateAsync(leader, new("party", "", 1, "map"), default);
        Assert.Null(rig.Registry.TryGet(member.Id));
        Assert.Empty(rig.Entered.To(member));
        Assert.Equal(SessionPhase.MainMenu, member.Features.Get<IPlayerSession>().Phase);
    }

    /// <summary>Only a party leader initiates following; a member can open an independent room.</summary>
    [Fact]
    public async Task MemberEntryDoesNotMoveTheLeaderOrLeaveAnUnrelatedRoom()
    {
        await using var rig = new Rig();
        var (leader, member) = rig.Pair();
        await rig.Flow.HandleCreateAsync(member, new("own", "", 1, "map"), default);
        var own = rig.Registry.TryGet(member.Id);
        Assert.False(rig.Entered.To(member).Single().PartyTransition);
        Assert.Null(rig.Registry.TryGet(leader.Id));
        await rig.Flow.HandleCreateAsync(leader, new("party", "", 1, "map"), default);
        Assert.Same(own, rig.Registry.TryGet(member.Id));
        Assert.NotSame(own, rig.Registry.TryGet(leader.Id));
    }

    /// <summary>Leadership or connection replacement during a send cannot move a stale member.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RechecksMembershipAndTheOriginalConnection(bool replaceConnection)
    {
        await using var rig = new Rig();
        var (leader, member) = rig.Pair();
        rig.Entered.BeforeSend = (session, _) =>
        {
            if (session == leader)
            {
                if (replaceConnection) rig.Player(22, sessionId: 222);
                else rig.Party = new([new(22, "player22"), new(11, "player11")]);
            }
            return ValueTask.CompletedTask;
        };
        await rig.Flow.HandleCreateAsync(leader, new("party", "", 1, "map"), default);
        Assert.Null(rig.Registry.TryGet(member.Id));
        Assert.Empty(rig.Entered.To(member));
        Assert.Null(rig.Registry.TryGet(222));
    }

    /// <summary>A full plaza keeps the follower's room; it never loses both memberships during a group return.</summary>
    [Fact]
    public async Task FullPlazaKeepsTheFollowersRoomSeat()
    {
        await using var rig = new Rig(plazaCapacity: 1);
        var (leader, member) = rig.Pair();
        await rig.Flow.HandleCreateAsync(leader, new("party", "", 1, "map"), default);
        var room = rig.Registry.TryGet(member.Id);
        await rig.Flow.HandleLeaveAsync(leader, new(), default);
        Assert.Same(room, rig.Registry.TryGet(member.Id));
        Assert.Empty(rig.Plaza.To(member));
        Assert.Equal(SessionPhase.Room, member.Features.Get<IPlayerSession>().Phase);
        Assert.Single(rig.Plaza.To(leader));
    }

    /// <summary>Quick matching enters through the same party-aware admission path.</summary>
    [Fact]
    public async Task QuickMatchBringsThePartyWithIndividualSeats()
    {
        await using var rig = new Rig();
        var host = rig.Player(33);
        await rig.Flow.HandleCreateAsync(host, new("open", "", 1, "map"), default);
        var (leader, member) = rig.Pair();
        var room = rig.Registry.TryGet(host.Id)!;
        Assert.True(await rig.Flow.TryMatchRoomAsync(leader, room, 1, default));
        Assert.Same(room, rig.Registry.TryGet(member.Id));
        Assert.Equal(2, rig.Entered.To(member).Single().YourSlot);
    }

    /// <summary>An already admitted move survives caller cancellation; a stale member click cannot duplicate its seat.</summary>
    [Fact]
    public async Task MemberClickWaitsForTheGroupAndDoesNotCreateADuplicateRoom()
    {
        await using var rig = new Rig();
        var (leader, member) = rig.Pair();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Entered.BeforeSend = async (session, ct) =>
        {
            if (session != leader) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        using var cancellation = new CancellationTokenSource();
        var create = rig.Flow.HandleCreateAsync(leader, new("party", "", 1, "map"), cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var click = rig.Flow.HandleCreateAsync(member, new("other", "", 1, "map"), default).AsTask();
            Assert.False(click.IsCompleted);
            await cancellation.CancelAsync();
            release.SetResult();
            await Task.WhenAll(create, click).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(rig.Registry.All());
            Assert.Same(rig.Registry.TryGet(leader.Id), rig.Registry.TryGet(member.Id));
            Assert.Single(rig.Entered.To(member));
        }
        finally { release.TrySetResult(); }
    }

    private sealed class Sink<T> : IMessageSender<T> where T : IMessage
    {
        internal List<(ISession Session, T Message)> Sent { get; } = [];
        internal Func<ISession, CancellationToken, ValueTask>? BeforeSend { get; set; }
        public string Protocol => "test/game";
        public async ValueTask SendAsync(ISession session, T message, CancellationToken ct)
        {
            if (BeforeSend is not null) await BeforeSend(session, ct);
            lock (Sent) Sent.Add((session, message));
        }
        internal T[] To(ISession session) { lock (Sent) return Sent.Where(row => row.Session == session).Select(row => row.Message).ToArray(); }
    }

    private sealed class Rig : IPartyDirectory, IOnlinePlayers, IAsyncDisposable
    {
        private readonly Dictionary<int, ISession> _online = [];
        internal PartySnapshot? Party { get; set; }
        internal RoomRegistry Registry { get; }
        internal RoomFlow Flow { get; }
        internal Sink<RoomEntered> Entered { get; } = new();
        internal Sink<RoomJoinRefused> Refused { get; } = new();
        internal Sink<RoomSeatUpdated> Seats { get; } = new();
        internal Sink<RoomLeft> Left { get; } = new();
        internal Sink<PlazaEntered> Plaza { get; } = new();
        internal Sink<PlazaMemberEntered> PlazaArrivals { get; } = new();
        internal Sink<PlazaMemberLeft> PlazaDepartures { get; } = new();
        internal Sink<PlazaPositionReceived> Positions { get; } = new();
        internal Sink<PlazaMenuReturnRequested> Menu { get; } = new();
        internal Rig(int seats = 4, int plazaCapacity = 36)
        {
            var options = new InGameOptions { MaxPlayersPerMatch = seats, PlazaCapacity = plazaCapacity };
            Registry = new RoomRegistry(options);
            Flow = new RoomFlow(Registry, new RoomSenders(Entered, Seats, new Sink<RoomSeatVacated>(),
                new Sink<RoomMasterChanged>(), Refused, Left, PlazaEntered: Plaza,
                PlazaMemberEntered: PlazaArrivals, PlazaMemberLeft: PlazaDepartures,
                PlazaPositionReceived: Positions, MenuReturn: Menu), NullLogger.Instance,
                plazaCapacity: plazaCapacity, options: options, parties: this, online: this);
        }
        internal RecordingSession Player(int user, long? sessionId = null)
        {
            var session = new RecordingSession(sessionId ?? user);
            session.SetLobbyPlayer(user, "player" + user);
            _online[user] = session;
            return session;
        }
        internal (RecordingSession Leader, RecordingSession Member) Pair()
        {
            var first = Player(11);
            var second = Player(22);
            Party = new([new(11, "player11"), new(22, "player22")]);
            return (first, second);
        }
        public PartySnapshot? PartyOf(int userNum) => Party?.Members.Any(member => member.UserNum == userNum) == true ? Party : null;
        public ISession? FindByUserNum(int userNum) => _online.GetValueOrDefault(userNum);
        public ISession? FindByNickname(string nickname) => _online.Values.FirstOrDefault(session => SessionPlayer.RequireIdentity(session).Nickname == nickname);
        public async ValueTask DisposeAsync() { await Flow.StopScenesAsync(); await Registry.StopAllAsync(); }
    }
}
