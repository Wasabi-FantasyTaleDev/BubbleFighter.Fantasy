using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>Contact authorization names the authenticated seat for every player, including its local owner.</summary>
    [Fact]
    public async Task MapPortalContactReachesOwnerAndPeersWithAuthenticatedSeat()
    {
        var rig = new Rig();
        var moved = new Sink<RoundPortalMoved>();
        try
        {
            await rig.StartAsync();
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            var room = rig.Registry.Find(rig.RoomId)!;
            var seat = room.FindByGame(rig.Guest.Id)!.Slot;
            await rig.Round.HandlePortalMoveAsync(rig.Guest, new(0x1234, 1, -2.5f, 100), moved, default);
            Assert.Equal(new RoundPortalMoved(0x1234, seat), Assert.Single(moved.To(rig.Guest)));
            Assert.Equal(new RoundPortalMoved(0x1234, seat), Assert.Single(moved.To(rig.Host)));
            Assert.Equal(RoomPhase.Playing, room.Phase);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Waiting/loading rooms, foreign or replaced connections, cancelled rounds and invalid vectors cannot authorize motion.</summary>
    [Fact]
    public async Task MapPortalContactRejectsInvalidRoundOrConnection()
    {
        var rig = new Rig();
        var moved = new Sink<RoundPortalMoved>();
        var request = new RoundPortalMoveRequested(3, 1, 2, 3);
        try
        {
            await rig.Round.HandlePortalMoveAsync(rig.Guest, request, moved, default);
            await rig.StartAsync();
            await rig.Round.HandlePortalMoveAsync(rig.Guest, request, moved, default);
            Assert.Empty(moved.To(rig.Host));
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            await rig.Round.HandlePortalMoveAsync(new RecordingSession(rig.Guest.Id), request, moved, default);
            await rig.Round.HandlePortalMoveAsync(new RecordingSession(998), request, moved, default);
            foreach (var bad in new[] { request with { X = float.NaN }, request with { Y = float.PositiveInfinity },
                request with { Z = float.NegativeInfinity } })
                await rig.Round.HandlePortalMoveAsync(rig.Guest, bad, moved, default);
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate) room.RoundCancelled = true;
            await rig.Round.HandlePortalMoveAsync(rig.Guest, request, moved, default);
            Assert.Empty(moved.To(rig.Host));
            Assert.Empty(moved.To(rig.Guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
