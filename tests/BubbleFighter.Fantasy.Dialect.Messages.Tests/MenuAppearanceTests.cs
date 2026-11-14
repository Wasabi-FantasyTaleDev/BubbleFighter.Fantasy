using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins captured instance modifiers in existing menu layouts and immutable appearance contracts.</summary>
public sealed class MenuAppearanceTests
{
    /// <summary>All modern menu equipment maps receive the supplied worn IDs and exact native modifier record.</summary>
    [Theory]
    [InlineData("kr-1256", "profile_entries", "RoomProfileEntry", 2)]
    [InlineData("cn-774", "equipped_objects", "EquippedObjectEntry", 1)]
    [InlineData("tw-454", "equipped_objects", "EquippedObjectEntry", 1)]
    public void ModernMenuWritesTheCapturedAttributeVector(string build, string field, string structure, int copies)
    {
        var tables = TestBuilds.Packets;
        int[] ids = [300001, 301001];
        RoomItemAttribute[] attributes = [new(301001, 7, new uint[] { 1, 37, 0, 0 }, 2, "pet")];
        var empty = MainMenuInstall.Write(tables, build, 42, "owner", 9, 300001, ids, []);
        var body = MainMenuInstall.Write(tables, build, 42, "owner", 9, 300001, ids, ItemAttributeWire.Values(attributes));
        Assert.Equal(empty.Length + 42 * copies, body.Length);
        var decoded = PacketCodec.Read(tables, build, "gspGotoRoom", body);
        var profile = Record(Assert.Single(List(decoded[field])));
        Assert.Equal(ids, List(profile["equipped_items"]).Select(Convert.ToInt32));
        var attributeField = Assert.Single(tables.Resolve(build, structure), entry => entry.Type == "list<ItemAttr>").Name;
        CheckAttribute(Record(Assert.Single(List(profile[attributeField]))));
        if (copies == 2)
            CheckAttribute(Record(Assert.Single(List(Record(Assert.Single(List(decoded["unknown_588"])))[attributeField]))));
        Assert.Equal(body, PacketCodec.Write(tables, build, "gspGotoRoom", decoded));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(tables, build, "gspGotoRoom", body.AsSpan(0, body.Length - 1)));
        var cleared = PacketCodec.Read(tables, build, "gspGotoRoom", empty);
        Assert.Empty(List(Record(Assert.Single(List(cleared[field])))[attributeField]));
    }

    /// <summary>Legacy menu and tutorial selection use the same supplied ItemAttr values as their equipped record.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    public void LegacyMenuAndTutorialSelectionRetainModifiers(string build)
    {
        var room = Menu();
        var (schema, _) = TestBuilds.Of(build);
        foreach (var kind in new[] { RoomCodec.MainMenuKind, RoomCodec.TutorialSelectKind })
        {
            var body = RoomCodec.WriteSingleRoomEnvelope(room, TestBuilds.Packets, build, schema, kind);
            var cleared = RoomCodec.WriteSingleRoomEnvelope(room with { ItemAttributes = [] }, TestBuilds.Packets, build, schema, kind);
            Assert.Equal(cleared.Length + 42, body.Length);
            var decoded = PacketCodec.Read(TestBuilds.Packets, build, "gspGotoRoom", body);
            var equipped = Record(Assert.Single(List(decoded["equipped_item_lists"])));
            CheckAttribute(Record(Assert.Single(List(equipped["unknown_48"]))));
        }
    }

    /// <summary>The two-frame menu writer fills the existing attribute vector without changing its scene transition.</summary>
    [Fact]
    public void TwoFrameMenuRetainsModifiers()
    {
        const string build = "cn-735";
        var body = GotoRoomGotoMenuCodec.WriteMainMenu(TestBuilds.Packets, build, Menu());
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, "gspGotoRoom", body);
        var field = Assert.Single(TestBuilds.Packets.Resolve(build, "EquippedItemList"), entry => entry.Type == "list<ItemAttr>").Name;
        CheckAttribute(Record(Assert.Single(List(Record(Assert.Single(List(decoded["equipped_item_lists"])))[field]))));
    }

    /// <summary>Menu, tutorial and inspection snapshots own their identity and nested modifier arrays.</summary>
    [Fact]
    public void AppearanceContractsCopyNestedValuesAndRejectUnrelatedModifiers()
    {
        int[] ids = [300001, 301001];
        uint[] words = [1, 37, 0, 0];
        RoomItemAttribute[] attributes = [new(301001, 7, words, 2, "pet")];
        var menu = new MainMenuRoomOpened(42, "owner", 300001, ids) { ItemAttributes = attributes };
        var tutorial = new TutorialRoundOpened(101, "map", 300001, ids, 1, 0, 1, true) { ItemAttributes = attributes };
        var fighter = new FighterAppearance(300001, ids, attributes);
        ids[1] = 200001;
        words[0] = 99;
        attributes[0] = new(200001, 9, [], 0, "");
        Assert.Equal(301001, menu.EquippedItemIds[1]);
        Assert.Equal(301001, tutorial.EquippedItemIds[1]);
        Assert.Equal(301001, fighter.EquippedItemIds[1]);
        Assert.Equal(1U, menu.ItemAttributes[0].Values[0]);
        Assert.Equal(1U, tutorial.ItemAttributes[0].Values[0]);
        Assert.Equal(1U, fighter.ItemAttributes[0].Values[0]);
        Assert.Throws<InvalidDataException>(() => menu with { ItemAttributes = attributes });
        Assert.Throws<InvalidDataException>(() => tutorial with { ItemAttributes = attributes });
        Assert.Throws<InvalidDataException>(() => new FighterAppearance(300001, [300001], attributes));
        var tooMany = Enumerable.Repeat(menu.ItemAttributes[0], InventoryLimits.MaxInventorySlotsPerBag + 1).ToArray();
        Assert.Throws<InvalidDataException>(() => menu with { ItemAttributes = tooMany });
        Assert.Empty((menu with { ItemAttributes = [] }).ItemAttributes);
        Assert.Empty((tutorial with { ItemAttributes = [] }).ItemAttributes);
    }

    private static MainMenuRoomOpened Menu() => new(42, "owner", 300001, [300001, 301001])
        { ItemAttributes = [new(301001, 7, new uint[] { 1, 37, 0, 0 }, 2, "pet")] };
    private static IReadOnlyList<object?> List(object? value) => Assert.IsAssignableFrom<IReadOnlyList<object?>>(value);
    private static IReadOnlyDictionary<string, object?> Record(object? value) => Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(value);
    private static void CheckAttribute(IReadOnlyDictionary<string, object?> attribute)
    {
        Assert.Equal(301001, attribute["unknown_4"]);
        Assert.Equal(7, attribute["unknown_8"]);
        Assert.Equal(new[] { 1, 37, 0, 0 }, List(attribute["unknown_12"]).Select(Convert.ToInt32));
        Assert.Equal(2, attribute["unknown_28"]);
        Assert.Equal("pet", attribute["unknown_32"]);
    }
}
