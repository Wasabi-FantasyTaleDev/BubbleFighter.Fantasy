using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.RoomCore;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Ownership delegation shares room serialization and keeps member order independent from host selection.</summary>
public sealed class RoomMasterTransferTests
{
    /// <summary>Delegation changes readiness and authority while retaining seat and profile insertion order.</summary>
    [Fact]
    public async Task MasterDelegationPreservesMemberOrderAndTransfersStartAuthority()
    {
        var rig = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(71, "host");
        var guest = RoomFlowTests.Rig.Player(72, "guest");
        try
        {
            var id = await rig.CreateAsync(host);
            await rig.Flow.HandleJoinAsync(guest, new(id, ""), default);
            await rig.Flow.HandleReadyAsync(guest, new(true), default);
            var room = rig.Registry.Find(id)!;
            var before = room.Seats.Select(seat => seat.Slot).ToArray();
            await rig.Flow.HandleMasterChangeAsync(host, new("guest"), default);
            Assert.Equal(new RoomMasterChangeReplied("guest", 0), Assert.Single(rig.MasterReplies.To(host)));
            Assert.Equal(1, room.MasterSlot);
            Assert.False(room.Seats.Single(seat => seat.Slot == 1).Ready);
            Assert.False(room.CanStart);
            Assert.Equal(before, room.Seats.Select(seat => seat.Slot));
            Assert.Equal(1, Assert.Single(rig.MasterChanged.To(host)).MasterSlot);
            Assert.Equal(1, Assert.Single(rig.MasterChanged.To(guest)).MasterSlot);

            await rig.Flow.HandleMasterChangeAsync(host, new("host"), default);
            Assert.Equal(2, rig.MasterReplies.To(host)[^1].Result);
            Assert.Equal(1, room.MasterSlot);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Native refusal results leave membership and ownership untouched.</summary>
    [Theory]
    [InlineData(false, "missing", 1)]
    [InlineData(false, "host", 3)]
    [InlineData(true, "host", 2)]
    public async Task InvalidDelegationEchoesItsNativeRefusal(bool fromGuest, string target, int result)
    {
        var rig = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(81, "host");
        var guest = RoomFlowTests.Rig.Player(82, "guest");
        try
        {
            var id = await rig.CreateAsync(host);
            await rig.Flow.HandleJoinAsync(guest, new(id, ""), default);
            var requester = fromGuest ? guest : host;
            await rig.Flow.HandleMasterChangeAsync(requester, new(target), default);
            Assert.Equal(new RoomMasterChangeReplied(target, result), Assert.Single(rig.MasterReplies.To(requester)));
            Assert.Equal(0, rig.Registry.Find(id)!.MasterSlot);
            Assert.Empty(rig.MasterChanged.To(host));
            Assert.Empty(rig.MasterChanged.To(guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>Loading a round freezes its master even while both seats remain in the roster.</summary>
    [Fact]
    public async Task StartedRoomCannotDelegateOwnership()
    {
        var rig = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(91, "host");
        var guest = RoomFlowTests.Rig.Player(92, "guest");
        try
        {
            var id = await rig.CreateAsync(host);
            await rig.Flow.HandleJoinAsync(guest, new(id, ""), default);
            await rig.Flow.HandleReadyAsync(guest, new(true), default);
            await rig.Flow.HandleStartAsync(host, new(), default);
            Assert.Single(rig.Starting.To(host));
            await rig.Flow.HandleMasterChangeAsync(host, new("guest"), default);
            Assert.Equal(1, Assert.Single(rig.MasterReplies.To(host)).Result);
            Assert.Equal(0, rig.Registry.Find(id)!.MasterSlot);
            Assert.Empty(rig.MasterChanged.To(guest));
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A failed acknowledgement cannot strand the surviving member behind the committed authority.</summary>
    [Fact]
    public async Task BrokenRequesterConnectionDoesNotSuppressTheNewMasterAnnouncement()
    {
        var rig = new RoomFlowTests.Rig();
        var host = RoomFlowTests.Rig.Player(101, "host");
        var guest = RoomFlowTests.Rig.Player(102, "guest");
        try
        {
            var id = await rig.CreateAsync(host);
            await rig.Flow.HandleJoinAsync(guest, new(id, ""), default);
            rig.MasterReplies.Failure = new IOException("The requester disconnected during its acknowledgement.");
            await rig.Flow.HandleMasterChangeAsync(host, new("guest"), default);
            Assert.Equal(1, rig.Registry.Find(id)!.MasterSlot);
            Assert.Equal(1, Assert.Single(rig.MasterChanged.To(guest)).MasterSlot);
            Assert.True(host.Connection.IsClosed);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }

    /// <summary>A delegated master's departure falls back to the oldest surviving member.</summary>
    [Fact]
    public void DelegationDoesNotReorderSurvivorsOrPersistAcrossEmptyRooms()
    {
        var members = new RoomMembers<Member>(4);
        members.Add(slot => new(slot));
        members.Add(slot => new(slot));
        members.Add(slot => new(slot));
        Assert.True(members.TransferMaster(2));
        Assert.Equal([0, 1, 2], members.Members.Select(member => member.Slot));
        Assert.False(members.TransferMaster(3));
        Assert.False(members.TransferMaster(2));
        Assert.Equal(1, members.RemoveAll(member => member.Slot == 1));
        Assert.Equal(2, members.MasterSlot);
        Assert.Equal(1, members.RemoveAll(member => member.Slot == 2));
        Assert.Equal(0, members.MasterSlot);
        Assert.Equal(1, members.RemoveAll(_ => true));
        Assert.Equal(-1, members.MasterSlot);
        members.Add(slot => new(slot));
        Assert.Equal(0, members.MasterSlot);
    }

    private sealed record Member(int Slot) : IRoomMember
    {
        public bool Ready => true;
        public bool InShop => false;
        public bool InInventory => false;
    }
}
