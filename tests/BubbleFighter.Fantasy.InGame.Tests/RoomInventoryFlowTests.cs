using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks caller-owned temporary selection and preservation independently of persistent bag equipment.</summary>
public sealed class RoomInventoryFlowTests
{
    /// <summary>The caller cannot choose another seat; existing members and later joiners see a copied selection.</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task SelectionIsRetainedAndPublishedWithoutGrantingItems(int count)
    {
        var rig = new RoomFlowTests.Rig();
        var changes = new RoomFlowTests.Sink<RoomInventoryChanged>();
        rig.Flow.InventoryChanged = changes;
        var host = RoomFlowTests.Rig.Player(11, "host");
        var guest = RoomFlowTests.Rig.Player(22, "guest");
        var later = RoomFlowTests.Rig.Player(33, "later");
        try
        {
            var id = await rig.CreateAsync(host);
            await rig.Flow.HandleJoinAsync(guest, new(id, ""), default);
            var room = rig.Registry.TryGet(host.Id)!;
            var seat = room.FindByGame(guest.Id)!;
            var character = seat.CharacterId;
            var worn = seat.EquippedItemIds;
            var selectors = count == 4 ? new[] { 2001, 1001, 7001, 5001 } : [2001, 1001, 7001, 5001, -1];
            await rig.Flow.HandleInventoryChangeAsync(guest, new(new(2, selectors)), default);
            selectors[0] = 999;
            Assert.Equal(new[] { host.Id, guest.Id }, changes.Sent.Select(row => row.Session));
            Assert.All(changes.Sent, change => Assert.Equal(1, change.Message.Slot));
            Assert.Equal(2001, seat.InventorySelection!.WeaponSelections[0]);
            Assert.Equal(character, seat.CharacterId);
            Assert.Same(worn, seat.EquippedItemIds);
            await rig.Flow.HandleJoinAsync(later, new(id, ""), default);
            var snapshot = rig.Entered.To(later)[^1].Room.Seats.Single(member => member.UserNum == 22);
            Assert.Equal(seat.InventorySelection, snapshot.InventorySelection);
            await rig.Flow.OnEquipmentSelectionCommittedAsync(new(guest, 22, 300001, [300001, 202001]), default);
            Assert.Null(seat.InventorySelection);
            await rig.Flow.HandleLeaveAsync(guest, new(), default);
            await rig.Flow.HandleJoinAsync(guest, new(id, ""), default);
            Assert.Null(room.FindByGame(guest.Id)!.InventorySelection);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A ready player or an active round cannot change native selectors after the loadout locks.</summary>
    [Fact]
    public async Task ReadyAndPlayingSeatsKeepTheirSelection()
    {
        var rig = new RoomFlowTests.Rig();
        var changes = new RoomFlowTests.Sink<RoomInventoryChanged>();
        rig.Flow.InventoryChanged = changes;
        var host = RoomFlowTests.Rig.Player(11, "host");
        try
        {
            await rig.CreateAsync(host);
            var room = rig.Registry.TryGet(host.Id)!;
            var seat = room.FindByGame(host.Id)!;
            seat.Ready = true;
            await rig.Flow.HandleInventoryChangeAsync(host, new(new(1, [2001, 1001, 7001, 5001])), default);
            seat.Ready = false;
            room.MarkPlaying();
            await rig.Flow.HandleInventoryChangeAsync(host, new(new(1, [2001, 1001, 7001, 5001])), default);
            Assert.Null(seat.InventorySelection);
            Assert.Empty(changes.Sent);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
