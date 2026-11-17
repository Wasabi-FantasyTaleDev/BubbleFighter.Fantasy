using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>The new group policy does not change mode25's existing outcome after an observer leaves.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SurvivalTeamsPreserveSuddenSurvivalObserverDepartureOutcome(bool enabled)
    {
        var rig = new Rig(configure: options => options.SurvivalTeamAssignments = enabled);
        rig.Round.FinalZoneEliminated = new Sink<RoundFinalZonePlayerEliminated>();
        try
        {
            var observer = CaptainPlayer(33, "observer");
            await rig.Rooms.HandleCreateAsync(rig.Host, new("room", "", 25, "map"), default);
            var room = rig.Registry.TryGet(rig.Host.Id)!;
            foreach (var member in new[] { rig.Guest, observer })
            {
                await rig.Rooms.HandleJoinAsync(member, new(room.Id, ""), default);
                await rig.Rooms.HandleReadyAsync(member, new(true), default);
            }
            var host = room.FindByGame(rig.Host.Id)!;
            var guest = room.FindByGame(rig.Guest.Id)!;
            // kr-1256 sub_C80220 excludes native observer36; preserve this existing seat-state input to mode25.
            room.FindByGame(observer.Id)!.Team = 36;
            await rig.Rooms.HandleStartAsync(rig.Host, new("map"), default);
            foreach (var member in new[] { rig.Host, rig.Guest, observer })
                await rig.Round.HandleLoadReportedAsync(member, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await rig.Round.HandleLifeReportedAsync(rig.Guest,
                new(RoundLifeEvent.Died, RoomRegistry.SceneEntity(guest.Slot)), default);
            Assert.Equal(RoomPhase.Playing, room.Phase);
            await rig.Rooms.HandleLeaveAsync(observer, new(), default);
            Assert.Equal(host.Slot, Assert.Single(room.DecidedWinnerSlots!));
            Assert.Equal(RoundOutcome.Won, Assert.Single(rig.Ended.To(rig.Host)).Outcome);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
