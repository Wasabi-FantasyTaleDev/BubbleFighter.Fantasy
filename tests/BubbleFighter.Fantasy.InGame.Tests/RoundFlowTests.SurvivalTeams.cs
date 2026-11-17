using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>An observer is not a surviving group and receives no participant place in the final winner set.</summary>
    [Fact]
    public async Task SurvivalTeamsObserversDoNotDelayLastGroupOutcome()
    {
        var rig = new Rig(configure: options => options.SurvivalTeamAssignments = true);
        var eliminated = new Sink<RoundFinalZonePlayerEliminated>();
        rig.Round.FinalZoneEliminated = eliminated;
        try
        {
            var observer = CaptainPlayer(33, "observer");
            await rig.Rooms.HandleCreateAsync(rig.Host, new("room", "", 30, "map"), default);
            var room = rig.Registry.TryGet(rig.Host.Id)!;
            foreach (var member in new[] { rig.Guest, observer })
            {
                await rig.Rooms.HandleJoinAsync(member, new(room.Id, ""), default);
                await rig.Rooms.HandleReadyAsync(member, new(true), default);
            }
            room.FindByGame(observer.Id)!.Team = 36;
            await rig.Rooms.HandleStartAsync(rig.Host, new("map"), default);
            foreach (var member in new[] { rig.Host, rig.Guest, observer })
                await rig.Round.HandleLoadReportedAsync(member, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var guest = room.FindByGame(rig.Guest.Id)!;
            await rig.Round.HandleLifeReportedAsync(rig.Guest,
                new(RoundLifeEvent.Died, RoomRegistry.SceneEntity(guest.Slot)), default);
            Assert.Equal(room.FindByGame(rig.Host.Id)!.Slot, Assert.Single(room.DecidedWinnerSlots!));
            Assert.Equal(1, Assert.Single(eliminated.To(rig.Host)).SurvivorsRemaining);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Three live groups do not finish early; removing the last opponent awards both remaining teammates.</summary>
    [Fact]
    public async Task SurvivalTeamsEndOnLastGroupInsteadOfLastIndividual()
    {
        var rig = new Rig(configure: options => options.SurvivalTeamAssignments = true);
        rig.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
        try
        {
            var third = CaptainPlayer(33, "third");
            var room = await StartFinalZoneModeAsync(rig, GameModeId.FinalZone, third);
            Assert.Equal(new byte[] { 1, 2, 3 }, room.Seats.Select(seat => seat.Team));
            await rig.Round.SynchronizeFinalZoneAsync(room, default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            Assert.Empty(rig.Ended.To(rig.Host));
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            var partner = room.FindByGame(third.Id)!;
            partner.Team = host.Team;
            await rig.Round.HandleLifeReportedAsync(rig.Guest,
                new(RoundLifeEvent.Died, RoomRegistry.SceneEntity(guest.Slot)), default);
            Assert.Equal(new[] { host.Slot, partner.Slot }.Order(), room.DecidedWinnerSlots!.Order());
            Assert.Equal(RoundOutcome.Won, Assert.Single(rig.Ended.To(rig.Host)).Outcome);
            Assert.Equal(RoundOutcome.Won, Assert.Single(rig.Ended.To(third)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>An eliminated teammate still wins with their surviving partner after the last opposing group departs.</summary>
    [Fact]
    public async Task SurvivalTeamsAwardEliminatedPartnerAfterFinalOpponentLeaves()
    {
        var rig = new Rig(configure: options => options.SurvivalTeamAssignments = true);
        rig.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
        try
        {
            var third = CaptainPlayer(33, "partner");
            var room = await StartFinalZoneModeAsync(rig, GameModeId.FinalZone, third);
            var host = room.FindByGame(rig.Host.Id)!;
            var partner = room.FindByGame(third.Id)!;
            partner.Team = host.Team;
            await rig.Round.HandleLifeReportedAsync(third,
                new(RoundLifeEvent.Died, RoomRegistry.SceneEntity(partner.Slot)), default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            await rig.Rooms.HandleLeaveAsync(rig.Guest, new(), default);
            Assert.Equal(new[] { host.Slot, partner.Slot }.Order(), room.DecidedWinnerSlots!.Order());
            Assert.Equal(RoundOutcome.Won, Assert.Single(rig.Ended.To(third)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A same-group pop earns no kill and cannot consume the victim's still-rescuable bubble.</summary>
    [Fact]
    public async Task SurvivalTeamsRefuseFriendlyPopButAcceptOpposingPop()
    {
        var rig = new Rig(configure: options => options.SurvivalTeamAssignments = true);
        rig.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
        try
        {
            var third = CaptainPlayer(33, "opponent");
            var room = await StartFinalZoneModeAsync(rig, GameModeId.FinalZone, third);
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            var opponent = room.FindByGame(third.Id)!;
            guest.Team = host.Team;
            var actor = RoomRegistry.SceneEntity(guest.Slot);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Bubbled, actor), default);
            await rig.Round.HandleLifeReportedAsync(rig.Host,
                new(RoundLifeEvent.Finished, actor) { Initiator = RoomRegistry.SceneEntity(host.Slot) }, default);
            Assert.Equal(SeatLife.Bubble, guest.Life.State);
            Assert.Equal(0, room.Scoring.Stats(host.Slot).Kills);
            await rig.Round.HandleLifeReportedAsync(third,
                new(RoundLifeEvent.Finished, actor) { Initiator = RoomRegistry.SceneEntity(opponent.Slot) }, default);
            Assert.Equal(SeatLife.Dead, guest.Life.State);
            Assert.Equal(1, room.Scoring.Stats(opponent.Slot).Kills);
            Assert.Equal(RoomPhase.Playing, room.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Rescue credit follows group identity, while individual Area32 participants never share that identity.</summary>
    [Theory]
    [InlineData(GameModeId.Area32, false, 0)]
    [InlineData(GameModeId.FinalZone, false, 0)]
    [InlineData(GameModeId.FinalZone, true, 1)]
    public async Task SurvivalTeamsRescueCreditRequiresTeammates(GameModeId mode, bool pair, int expected)
    {
        var rig = new Rig(configure: options => options.SurvivalTeamAssignments = true);
        rig.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
        try
        {
            var third = CaptainPlayer(33, "opponent");
            var room = await StartFinalZoneModeAsync(rig, mode, third);
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            if (pair) guest.Team = host.Team;
            var actor = RoomRegistry.SceneEntity(guest.Slot);
            await rig.Round.HandleLifeReportedAsync(rig.Guest, new(RoundLifeEvent.Bubbled, actor), default);
            await rig.Round.HandleLifeReportedAsync(rig.Guest,
                new(RoundLifeEvent.Released, actor) { Initiator = RoomRegistry.SceneEntity(host.Slot) }, default);
            Assert.Equal(expected, room.Scoring.Stats(host.Slot).Rescues);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A malformed unassigned roster cannot manufacture a team win after one participant dies.</summary>
    [Fact]
    public async Task SurvivalTeamsUnassignedRosterCannotProduceFalseWinner()
    {
        var rig = new Rig(configure: options => options.SurvivalTeamAssignments = true);
        rig.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
        try
        {
            var room = await StartFinalZoneModeAsync(rig, GameModeId.FinalZone, CaptainPlayer(33, "third"));
            room.Seats[0].Team = room.Seats[1].Team = 0;
            room.Seats[2].Team = 1;
            room.Seats[2].Life.Die();
            room.FinalZone!.Synchronize(room.Seats);
            Assert.False(room.FinalZone.Finished);
            Assert.Empty(room.FinalZone.WinnerSlots);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
