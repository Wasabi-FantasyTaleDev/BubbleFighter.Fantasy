using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Manual survival rooms publish identities that the native affinity and spawn consumers can use.</summary>
public sealed class SurvivalRoomFlowTests
{
    private static InGameOptions Options(int groups = 12) => new()
        { SurvivalTeamAssignments = true, SurvivalMaximumGroups = groups, MaxPlayersPerMatch = 16 };

    /// <summary>Individual seats and unpaired duo seats receive distinct identities before entry and start are published.</summary>
    [Theory]
    [InlineData(23)]
    [InlineData(30)]
    public async Task SurvivalTeamsReachEntryAndFrozenStart(int mode)
    {
        var rig = new RoomFlowTests.Rig(options: Options());
        try
        {
            var players = Enumerable.Range(1, 3).Select(id => RoomFlowTests.Rig.Player(id, "p" + id)).ToArray();
            await rig.Flow.HandleCreateAsync(players[0], new("room", "", mode, "map"), default);
            var room = rig.Registry.TryGet(players[0].Id)!;
            foreach (var player in players.Skip(1)) await rig.Flow.HandleJoinAsync(player, new(room.Id, ""), default);
            Assert.Equal(new[] { 1, 2, 3 }, rig.Entered.To(players[2])[^1].Room.Seats.Select(seat => seat.Team));
            await rig.Flow.HandleStartAsync(players[0], new("map") { Force = true }, default);
            Assert.Equal(new[] { 1, 2, 3 }, Assert.Single(rig.Starting.To(players[0])).Room.Seats.Select(seat => seat.Team));
            Assert.Equal(RoomPhase.Starting, room.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A manual duo move can join a group while preserving the native within-group spawn ordinal.</summary>
    [Fact]
    public async Task SurvivalTeamsManualPairPublishesDistinctNativeSpawnKeys()
    {
        var rig = new RoomFlowTests.Rig(options: Options());
        try
        {
            var host = RoomFlowTests.Rig.Player(1, "host");
            var guest = RoomFlowTests.Rig.Player(2, "guest");
            var third = RoomFlowTests.Rig.Player(3, "third");
            await rig.Flow.HandleCreateAsync(host, new("room", "", 30, "map"), default);
            var room = rig.Registry.TryGet(host.Id)!;
            await rig.Flow.HandleJoinAsync(guest, new(room.Id, ""), default);
            await rig.Flow.HandleJoinAsync(third, new(room.Id, ""), default);
            await rig.Flow.HandleTeamChangeAsync(host, new(), default);
            Assert.Equal(2, rig.SeatUpdated.To(guest)[^1].Seat.Team);
            await rig.Flow.HandleStartAsync(host, new("map") { Force = true }, default);
            var roster = Assert.Single(rig.Starting.To(host)).Room.Seats.OrderBy(seat => seat.Slot).ToArray();
            Assert.Equal(new[] { 2, 2, 3 }, roster.Select(seat => seat.Team));
            // kr-1256 sub_C80010 passes group * 10 + its occupied-seat ordinal into the map spawn lookup.
            var counts = new Dictionary<int, int>();
            var keys = roster.Select(seat => 10 * seat.Team + (counts[seat.Team] = counts.GetValueOrDefault(seat.Team) + 1));
            Assert.Equal(new[] { 21, 22, 31 }, keys);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The button skips a full group and an individual round never changes affinity through that button.</summary>
    [Theory]
    [InlineData(30, 3)]
    [InlineData(23, 1)]
    public async Task SurvivalTeamsButtonRespectsModeAndGroupCapacity(int mode, byte expected)
    {
        var rig = new RoomFlowTests.Rig(options: Options(3));
        try
        {
            var host = RoomFlowTests.Rig.Player(1, "host");
            await rig.Flow.HandleCreateAsync(host, new("room", "", mode, "map"), default);
            var room = rig.Registry.TryGet(host.Id)!;
            foreach (var id in new[] { 2, 3 }) await rig.Flow.HandleJoinAsync(RoomFlowTests.Rig.Player(id, "p" + id), new(room.Id, ""), default);
            Assert.Equal(1, room.FindByGame(host.Id)!.Team);
            if (mode == 30) room.Seats[2].Team = 2;
            await rig.Flow.HandleTeamChangeAsync(host, new(), default);
            Assert.Equal(expected, room.FindByGame(host.Id)!.Team);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A thirteenth unrelated solo is refused without occupying a seat or changing existing groups.</summary>
    [Fact]
    public async Task SurvivalTeamsRefuseThirteenthSoloBeforePublishingAdmission()
    {
        var rig = new RoomFlowTests.Rig(options: Options());
        try
        {
            var host = RoomFlowTests.Rig.Player(1, "host");
            await rig.Flow.HandleCreateAsync(host, new("room", "", 30, "map"), default);
            var room = rig.Registry.TryGet(host.Id)!;
            foreach (var id in Enumerable.Range(2, 11)) await rig.Flow.HandleJoinAsync(RoomFlowTests.Rig.Player(id, "p" + id), new(room.Id, ""), default);
            var overflow = RoomFlowTests.Rig.Player(13, "overflow");
            await rig.Flow.HandleJoinAsync(overflow, new(room.Id, ""), default);
            Assert.Equal(RoomJoinRefusal.Full, Assert.Single(rig.Refused.To(overflow)).Reason);
            Assert.Null(rig.Registry.TryGet(overflow.Id));
            Assert.Empty(rig.Entered.To(overflow));
            Assert.Equal(Enumerable.Range(1, 12), room.Seats.Select(seat => (int)seat.Team));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Individual identities use the existing room seat cap, independently of the configured duo group count.</summary>
    [Fact]
    public async Task SurvivalTeamsIndividualModeSupportsIdentitiesAboveTwelve()
    {
        var rig = new RoomFlowTests.Rig(options: Options(1));
        try
        {
            var host = RoomFlowTests.Rig.Player(1, "host");
            await rig.Flow.HandleCreateAsync(host, new("room", "", 23, "map"), default);
            var room = rig.Registry.TryGet(host.Id)!;
            foreach (var id in Enumerable.Range(2, 15)) await rig.Flow.HandleJoinAsync(RoomFlowTests.Rig.Player(id, "p" + id), new(room.Id, ""), default);
            Assert.Equal(Enumerable.Range(1, 16), room.Seats.Select(seat => (int)seat.Team));
            Assert.Equal(16, room.MaxSeats);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A mode change is atomic when the current solos exceed the configured group count.</summary>
    [Fact]
    public async Task SurvivalTeamsRefuseModeChangeWithoutMutatingRoom()
    {
        var rig = new RoomFlowTests.Rig(options: Options(2));
        try
        {
            var host = RoomFlowTests.Rig.Player(1, "host");
            await rig.Flow.HandleCreateAsync(host, new("room", "", 1, "old-map"), default);
            var room = rig.Registry.TryGet(host.Id)!;
            foreach (var id in new[] { 2, 3 }) await rig.Flow.HandleJoinAsync(RoomFlowTests.Rig.Player(id, "p" + id), new(room.Id, ""), default);
            var teams = room.Seats.Select(seat => seat.Team).ToArray();
            await rig.Flow.HandleSettingsChangeAsync(host, new(30, "new-map"), default);
            Assert.Equal(1, room.ModeId);
            Assert.Equal("old-map", room.MapKey);
            Assert.Equal(teams, room.Seats.Select(seat => seat.Team));
            Assert.Empty(rig.SettingsChanged.To(host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Moving into and out of survival resets old affinities and republishes every changed roster row.</summary>
    [Fact]
    public async Task SurvivalTeamsModeTransitionsReplaceOrdinaryTeamsAndRestoreThem()
    {
        var rig = new RoomFlowTests.Rig(options: Options());
        try
        {
            var host = RoomFlowTests.Rig.Player(1, "host");
            await rig.Flow.HandleCreateAsync(host, new("room", "", 1, "map"), default);
            var room = rig.Registry.TryGet(host.Id)!;
            foreach (var id in new[] { 2, 3 }) await rig.Flow.HandleJoinAsync(RoomFlowTests.Rig.Player(id, "p" + id), new(room.Id, ""), default);
            await rig.Flow.HandleSettingsChangeAsync(host, new(30, ""), default);
            Assert.Equal(new byte[] { 1, 2, 3 }, room.Seats.Select(seat => seat.Team));
            Assert.Equal(new[] { 1, 2, 3 }, rig.SeatUpdated.To(host).TakeLast(3).Select(update => update.Seat.Team));
            await rig.Flow.HandleSettingsChangeAsync(host, new(1, ""), default);
            Assert.Equal(new byte[] { 1, 2, 1 }, room.Seats.Select(seat => seat.Team));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A room created as survival uses its current ordinary mode when it later assigns spawn teams.</summary>
    [Fact]
    public async Task SurvivalTeamsCreatedAsSurvivalCanReturnToOrdinarySpawnTeams()
    {
        var rig = new RoomFlowTests.Rig(options: Options());
        try
        {
            var host = RoomFlowTests.Rig.Player(1, "host");
            await rig.Flow.HandleCreateAsync(host, new("room", "", 30, "map"), default);
            var room = rig.Registry.TryGet(host.Id)!;
            foreach (var id in new[] { 2, 3 }) await rig.Flow.HandleJoinAsync(RoomFlowTests.Rig.Player(id, "p" + id), new(room.Id, ""), default);
            await rig.Flow.HandleSettingsChangeAsync(host, new(1, ""), default);
            Assert.Equal(new byte[] { 1, 2, 1 }, room.Seats.Select(seat => seat.Team));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Three members in one group cannot enter loading even through a forced start.</summary>
    [Fact]
    public async Task SurvivalTeamsRejectInvalidGroupBeforeStarting()
    {
        var rig = new RoomFlowTests.Rig(options: Options());
        try
        {
            var host = RoomFlowTests.Rig.Player(1, "host");
            await rig.Flow.HandleCreateAsync(host, new("room", "", 30, "map"), default);
            var room = rig.Registry.TryGet(host.Id)!;
            foreach (var id in new[] { 2, 3 }) await rig.Flow.HandleJoinAsync(RoomFlowTests.Rig.Player(id, "p" + id), new(room.Id, ""), default);
            foreach (var seat in room.Seats) seat.Team = 1;
            await rig.Flow.HandleStartAsync(host, new("map") { Force = true }, default);
            Assert.Equal(RoomPhase.Waiting, room.Phase);
            Assert.Empty(rig.Starting.To(host));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The policy is opt-in and does not enable either unaudited multi-group mode.</summary>
    [Theory]
    [InlineData(false, 30)]
    [InlineData(true, 42)]
    [InlineData(true, 44)]
    public async Task SurvivalTeamsLeaveUnenabledProfilesAndModesUnchanged(bool enabled, int mode)
    {
        var options = Options();
        options.SurvivalTeamAssignments = enabled;
        var rig = new RoomFlowTests.Rig(options: options);
        try
        {
            var host = RoomFlowTests.Rig.Player(1, "host");
            await rig.Flow.HandleCreateAsync(host, new("room", "", mode, "map"), default);
            Assert.Equal(0, rig.Registry.TryGet(host.Id)!.Seats[0].Team);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Only a reciprocal current two-person party is paired, and deployments can disable that policy.</summary>
    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(true, false, 2)]
    [InlineData(false, true, 2)]
    public async Task SurvivalTeamsPairOnlyProvenPartyMembership(bool pairEnabled, bool reciprocal, int secondTeam)
    {
        var options = Options();
        options.SurvivalPairParties = pairEnabled;
        var directory = new PartyDirectory { Reciprocal = reciprocal };
        var entered = new RoomFlowTests.Sink<RoomEntered>();
        var registry = new RoomRegistry(options);
        var flow = new RoomFlow(registry, new RoomSenders(entered, new RoomFlowTests.Sink<RoomSeatUpdated>(),
            new RoomFlowTests.Sink<RoomSeatVacated>(), new RoomFlowTests.Sink<RoomMasterChanged>(),
            SettingsChanged: new RoomFlowTests.Sink<RoomSettingsChanged>()), NullLogger.Instance,
            options: options, parties: directory, online: directory);
        try
        {
            var host = directory.Add(1);
            var guest = directory.Add(2);
            await flow.HandleCreateAsync(host, new("room", "", 4, "map"), default);
            var room = registry.TryGet(host.Id)!;
            await flow.HandleJoinAsync(guest, new(room.Id, ""), default);
            directory.Party = new([new(1, "p1"), new(2, "p2")]);
            await flow.HandleSettingsChangeAsync(host, new(30, ""), default);
            Assert.Equal(new[] { 1, secondTeam }, room.Seats.Select(seat => (int)seat.Team));
        }
        finally { await flow.StopScenesAsync(); await registry.StopAllAsync(); }
    }

    /// <summary>A verified party member uses their partner's group on admission, but cannot displace a manual teammate.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SurvivalTeamsPartyJoinRespectsPartnersRemainingPlace(bool full)
    {
        var options = Options(2);
        var directory = new PartyDirectory { Reciprocal = true };
        var entered = new RoomFlowTests.Sink<RoomEntered>();
        var refused = new RoomFlowTests.Sink<RoomJoinRefused>();
        var registry = new RoomRegistry(options);
        var flow = new RoomFlow(registry, new RoomSenders(entered, new RoomFlowTests.Sink<RoomSeatUpdated>(),
            new RoomFlowTests.Sink<RoomSeatVacated>(), new RoomFlowTests.Sink<RoomMasterChanged>(), refused),
            NullLogger.Instance, options: options, parties: directory, online: directory);
        try
        {
            var host = directory.Add(1);
            var guest = directory.Add(2);
            await flow.HandleCreateAsync(host, new("room", "", 30, "map"), default);
            var room = registry.TryGet(host.Id)!;
            if (full)
            {
                var stranger = directory.Add(3);
                await flow.HandleJoinAsync(stranger, new(room.Id, ""), default);
                room.FindByGame(stranger.Id)!.Team = 1;
            }
            directory.Party = new([new(1, "p1"), new(2, "p2")]);
            await flow.HandleJoinAsync(guest, new(room.Id, ""), default);
            if (full)
            {
                Assert.Equal(RoomJoinRefusal.Full, Assert.Single(refused.To(guest)).Reason);
                Assert.Null(registry.TryGet(guest.Id));
                Assert.Equal(new byte[] { 1, 1 }, room.Seats.Select(seat => seat.Team));
            }
            else
            {
                Assert.Empty(refused.To(guest));
                Assert.Equal(new[] { 1, 1 }, Assert.Single(entered.To(guest)).Room.Seats.Select(seat => seat.Team));
            }
        }
        finally { await flow.StopScenesAsync(); await registry.StopAllAsync(); }
    }

    /// <summary>Observer 36 survives mode changes and starting without consuming a duo group or entering team cycling.</summary>
    [Theory]
    [InlineData(23)]
    [InlineData(30)]
    public async Task SurvivalTeamsPreserveObserversWithoutConsumingGroups(int mode)
    {
        var rig = new RoomFlowTests.Rig(options: Options(1));
        try
        {
            var host = RoomFlowTests.Rig.Player(1, "host");
            var observer = RoomFlowTests.Rig.Player(2, "observer");
            await rig.Flow.HandleCreateAsync(host, new("room", "", 4, "map"), default);
            var room = rig.Registry.TryGet(host.Id)!;
            await rig.Flow.HandleJoinAsync(observer, new(room.Id, ""), default);
            room.FindByGame(observer.Id)!.Team = 36;
            await rig.Flow.HandleSettingsChangeAsync(host, new(mode, ""), default);
            await rig.Flow.HandleTeamChangeAsync(observer, new(), default);
            await rig.Flow.HandleStartAsync(host, new("map") { Force = true }, default);
            Assert.Equal(new[] { 1, 36 }, Assert.Single(rig.Starting.To(host)).Room.Seats.Select(seat => seat.Team));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A disabled policy does not impose its configured group bound or reject an existing three-person group.</summary>
    [Fact]
    public async Task SurvivalTeamsDisabledPolicyLeavesAdmissionAndStartIsolated()
    {
        var options = Options(1);
        options.SurvivalTeamAssignments = false;
        var rig = new RoomFlowTests.Rig(options: options);
        try
        {
            var host = RoomFlowTests.Rig.Player(1, "host");
            await rig.Flow.HandleCreateAsync(host, new("room", "", 30, "map"), default);
            var room = rig.Registry.TryGet(host.Id)!;
            foreach (var id in new[] { 2, 3 }) await rig.Flow.HandleJoinAsync(RoomFlowTests.Rig.Player(id, "p" + id), new(room.Id, ""), default);
            foreach (var seat in room.Seats) seat.Team = 1;
            await rig.Flow.HandleStartAsync(host, new("map") { Force = true }, default);
            Assert.Equal(new[] { 1, 1, 1 }, Assert.Single(rig.Starting.To(host)).Room.Seats.Select(seat => seat.Team));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>The configured group count cannot exceed the native controls or be empty.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(12, true)]
    [InlineData(13, false)]
    public void SurvivalTeamsValidateConfiguredGroupBound(int groups, bool valid)
    {
        var options = Options(groups);
        var context = new ValidationContext(options) { MemberName = nameof(InGameOptions.SurvivalMaximumGroups) };
        Assert.Equal(valid, Validator.TryValidateProperty(groups, context, []));
    }

    private sealed class PartyDirectory : IPartyDirectory, IOnlinePlayers
    {
        private readonly Dictionary<int, ISession> _players = [];
        internal PartySnapshot? Party { get; set; }
        internal bool Reciprocal { get; init; }
        internal RecordingSession Add(int user)
        {
            var session = RoomFlowTests.Rig.Player(user, "p" + user);
            _players.Add(user, session);
            return session;
        }
        public PartySnapshot? PartyOf(int userNum) => Party?.Members.Any(member => member.UserNum == userNum) == true
            && (userNum == 1 || Reciprocal) ? Party : null;
        public ISession? FindByUserNum(int userNum) => _players.GetValueOrDefault(userNum);
        public ISession? FindByNickname(string nickname) => _players.Values.FirstOrDefault(session => SessionPlayer.RequireIdentity(session).Nickname == nickname);
    }
}
