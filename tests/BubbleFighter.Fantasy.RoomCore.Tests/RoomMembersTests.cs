using BubbleFighter.Fantasy.RoomCore;
using Xunit;

namespace BubbleFighter.Fantasy.RoomCore.Tests;

public sealed class RoomMembersTests
{
    [Fact]
    public void HostDepartureKeepsJoinOrderAndReusesVacatedSeat()
    {
        var members = new RoomMembers<Member>(3);
        var host = members.Add(slot => new Member(slot));
        var firstGuest = members.Add(slot => new Member(slot));
        var secondGuest = members.Add(slot => new Member(slot));

        Assert.Equal(0, members.MasterSlot);
        Assert.Equal(1, members.RemoveAll(member => member == host));
        Assert.Equal(1, members.MasterSlot);
        var next = members.Add(slot => new Member(slot));
        Assert.Equal(0, next.Slot);
        Assert.Equal(new[] { firstGuest, secondGuest, next }, members.Members);
        Assert.Equal(1, members.MasterSlot);
    }

    [Fact]
    public void ClosedSeatCannotBeOccupiedOrClosedWhileOccupied()
    {
        var members = new RoomMembers<Member>(2);
        members.Add(slot => new Member(slot));
        Assert.Throws<InvalidOperationException>(() => members.SetSlotClosed(0, true));
        Assert.Throws<InvalidOperationException>(() => members.SetSlotClosed(2, true));
        Assert.True(members.SetSlotClosed(1, true));
        Assert.True(members.IsFull);
        Assert.Throws<InvalidOperationException>(() => members.Add(slot => new Member(slot)));
        Assert.True(members.SetSlotClosed(1, false));
        Assert.Equal(1, members.Add(slot => new Member(slot)).Slot);
    }

    [Fact]
    public void StartRequiresEveryGuestReadyAndOutsideDialogs()
    {
        var members = new RoomMembers<Member>(2);
        Assert.False(members.CanStart);
        members.Add(slot => new Member(slot));
        Assert.True(members.CanStart);
        var guest = members.Add(slot => new Member(slot));
        Assert.False(members.CanStart);
        guest.Ready = true;
        Assert.True(members.CanStart);
        guest.InShop = true;
        Assert.False(members.CanStart);
        guest.InShop = false;
        guest.InInventory = true;
        Assert.False(members.CanStart);
    }

    [Fact]
    public void RejectsMemberWhoseSeatDiffersFromAllocation()
    {
        var members = new RoomMembers<Member>(1);
        Assert.Throws<InvalidOperationException>(() => members.Add(_ => new Member(1)));
        Assert.Empty(members.Members);
    }

    private sealed class Member(int slot) : IRoomMember
    {
        public int Slot { get; } = slot;
        public bool Ready { get; set; }
        public bool InShop { get; set; }
        public bool InInventory { get; set; }
    }
}
