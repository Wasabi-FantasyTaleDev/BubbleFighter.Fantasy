using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class NativeMatchRoundTests
{
    /// <summary>Every seat's instance words are frozen before validators run and shared through the same opening roster.</summary>
    [Fact]
    public async Task OpeningSeedsFreezeEverySeatsInstanceModifiersBeforeValidation()
    {
        uint[] hostWords = [1, 2, 3, 4];
        uint[] guestWords = [5, 6, 7, 8];
        var validator = new SeedValidator { Inspect = _ => { hostWords[0] = 99; guestWords[0] = 99; } };
        await using var rig = new Rig("cn-324", 18, seedInputs: validator);
        await rig.StartAsync();
        rig.Room.Seats[0].EquippedItemIds = [300001, 203103, 190100];
        rig.Room.Seats[0].ItemAttributes = [new RoomItemAttribute(190100, 101, hostWords, 1, "host")];
        rig.Room.Seats[1].EquippedItemIds = [300002, 202001, 190101];
        rig.Room.Seats[1].ItemAttributes = [new RoomItemAttribute(190101, 102, guestWords, 2, "guest")];
        await ReadySeedRelaysAsync(rig);
        await rig.Round.HandleMatchConnectionReportedAsync(rig.Guest, new(true), default);
        Assert.Equal(2, rig.Seeds.Sent.Count);
        var first = rig.Seeds.Sent[0].Message;
        var second = rig.Seeds.Sent[1].Message;
        Assert.Same(first.Roster, second.Roster);
        Assert.Same(first.Equipment, first.Roster!.Occupants[0].Equipment);
        Assert.Same(second.Equipment, first.Roster.Occupants[1].Equipment);
        Assert.Equal(1u, first.Equipment!.ItemAttributes[0].Values[0]);
        Assert.Equal(5u, second.Equipment!.ItemAttributes[0].Values[0]);
        Assert.Equal("host", first.Equipment.ItemAttributes[0].Unknown32);
        Assert.Equal(2u, second.Equipment.ItemAttributes[0].Unknown28);
    }

    /// <summary>A completed equipment view retains its modifier words, and an equivalent repeat does not create a new revision.</summary>
    [Fact]
    public async Task PublishedInstanceModifiersAreCopiedIntoTheCriticalSnapshot()
    {
        var authority = new CriticalAuthority();
        await using var rig = new Rig("cn-324", 18, seedInputs: new SeedValidator(), criticalAuthority: authority);
        await StartCriticalRoundAsync(rig);
        uint[] words = [1, 2, 3, 4];
        var view = new LocalEquipmentViewUpdated(11, 300001, [300001, 202001, 201001, 190100], 0, 202001, 0)
        {
            ItemAttributes = [new RoomItemAttribute(190100, 101, words, 1, "pet")],
        };
        await rig.Round.OnEquipmentViewPublishedAsync(new("cn-324/game", rig.Host, view, true), default);
        var update = Assert.Single(authority.Sent).Message;
        words[0] = 99;
        Assert.Equal(1u, update.Equipment.ItemAttributes[0].Values[0]);
        Assert.Same(update.Equipment.ItemAttributes, rig.Room.Seats[0].ItemAttributes);
        var duplicate = view with { ItemAttributes = [new RoomItemAttribute(190100, 101, [1, 2, 3, 4], 1, "pet")] };
        await rig.Round.OnEquipmentViewPublishedAsync(new("cn-324/game", rig.Host, duplicate, true), default);
        Assert.Single(authority.Sent);
        Assert.Equal(1, rig.Room.Seats[0].Critical!.Current.Revision);
    }
}
