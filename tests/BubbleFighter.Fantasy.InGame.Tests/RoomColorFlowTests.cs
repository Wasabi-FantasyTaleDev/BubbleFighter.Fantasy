using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks color propagation through the shared room owner independently of allegiance.</summary>
public sealed class RoomColorFlowTests
{
    /// <summary>Joining peers see saved color; recoloring updates every member without changing team or equipment.</summary>
    [Fact]
    public async Task ColorSurvivesEntryAndIsPublishedToEveryRoomMember()
    {
        var rig = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(11, "host");
        var guest = RoomFlowTests.Rig.Player(22, "guest");
        host.Features.Get<IPlayerSession>().ChangeColorIndex(8);
        try
        {
            var roomId = await rig.CreateAsync(host);
            await rig.Flow.HandleJoinAsync(guest, new RoomJoinRequested(roomId, ""), default);
            var before = rig.Entered.To(guest).Last().Room.Seats.Single(seat => seat.UserNum == 11);
            Assert.Equal((ushort)8, before.ColorIndex);
            rig.SeatUpdated.Sent.Clear();
            await rig.Flow.OnColorChangedAsync(new PlayerColorChanged(11, 14), default);
            Assert.Equal(new[] { host.Id, guest.Id }, rig.SeatUpdated.Sent.Select(row => row.Session));
            foreach (var update in rig.SeatUpdated.Sent.Select(row => row.Message))
            {
                Assert.Equal(before with { ColorIndex = 14 }, update.Seat);
                Assert.Equal((ushort)14, update.Room!.Seats.Single(seat => seat.UserNum == 11).ColorIndex);
                Assert.Equal((ushort)0, update.Room.Seats.Single(seat => seat.UserNum == 22).ColorIndex);
            }
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A color event for an absent player cannot alter seats or publish a default appearance.</summary>
    [Fact]
    public async Task AbsentPlayerColorDoesNotBroadcast()
    {
        var rig = new RoomFlowTests.Rig();
        try
        {
            await rig.CreateAsync(RoomFlowTests.Rig.Player(11, "host"));
            rig.SeatUpdated.Sent.Clear();
            await rig.Flow.OnColorChangedAsync(new PlayerColorChanged(99, 14), default);
            Assert.Empty(rig.SeatUpdated.Sent);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
