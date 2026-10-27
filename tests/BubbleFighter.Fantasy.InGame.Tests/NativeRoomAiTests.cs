using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoomCommandsBindingTests
{
    /// <summary>Both native menu encodings publish a colored team when an empty team-room seat supplies zero.</summary>
    [Theory]
    [InlineData("cn-774", 2075)]
    [InlineData("kr-1256", 2076)]
    public async Task NativeAiMenuAssignsATeamBeforePublishingTheSeat(string build, int opcode)
    {
        using var harness = await Harness.CreateAsync(ai: true, build: build);
        var create = build == "kr-1256" ? Raw(writer =>
        {
            (Source() with { Map = Source().Map with { Unknown4 = 38 } }).Write(writer);
            writer.WriteWideStringPrefixed("");
            writer.WriteByte(0xBA);
        }) : PacketCodec.Write(Tables, build, "RoomCreateRequest", new Dictionary<string, object?>
        {
            ["session_setting"] = new Dictionary<string, object?>
            {
                ["title"] = "AI", ["open_seats"] = (ushort)4,
                ["game_setting"] = new Dictionary<string, object?> { ["unknown_4"] = 38 },
            },
            ["password"] = "", ["unknown_n2"] = (byte)0xBA,
        });
        await harness.Dispatch(harness.Host, 402, create);
        var fields = new Dictionary<string, object?>
        {
            ["team"] = build == "cn-774" ? (byte)0 : new Dictionary<string, object?> { ["team"] = (byte)0 },
            ["slot"] = 1, ["difficulty"] = 1, ["preset"] = 2,
        };
        if (build == "cn-774")
            fields["selection_mode"] = 2;
        await harness.Dispatch(harness.Host, (ushort)opcode, PacketCodec.Write(Tables, build, "RoomAiAddRequest", fields));
        Assert.Equal(2, Assert.Single(harness.Room.Bots).Appearance.Team);
        var update = PacketCodec.Read(Tables, build, "RoomSeatUpdate", harness.Host.Body(503));
        Assert.Equal((byte)2, Assert.IsType<Dictionary<string, object?>>(update["user"])["team"]);
    }

    /// <summary>The direct native AI request reaches shared ownership and preserves independent bot equipment and controller identity.</summary>
    [Fact]
    public async Task NativeAiPopupUsesItsOwnRequestAndSeatMetadata()
    {
        using var harness = await Harness.CreateAsync(ai: true, build: "cn-774");
        byte[] Write(string name, Dictionary<string, object?> fields) => PacketCodec.Write(Tables, "cn-774", name, fields);
        await harness.Dispatch(harness.Host, 402, Write("RoomCreateRequest", new()
        {
            ["session_setting"] = new Dictionary<string, object?>
            {
                ["title"] = "AI", ["open_seats"] = (ushort)4,
                ["game_setting"] = new Dictionary<string, object?> { ["unknown_4"] = 38 },
            },
            ["password"] = "", ["unknown_n2"] = (byte)0xBA,
        }));
        var add = Write("RoomAiAddRequest", new()
        {
            ["team"] = (byte)2, ["selection_mode"] = 2, ["slot"] = 1, ["difficulty"] = 1, ["preset"] = 2,
        });
        Assert.Equal(new byte[] { 2, 2, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0 }, add);
        await harness.Dispatch(harness.Host, 2075, add);
        var bot = Assert.Single(harness.Room.Bots).Appearance;
        Assert.Equal(new RoomAiIdentity(0, 11, 1, 2), bot.Ai);
        var frame = PacketCodec.Read(Tables, "cn-774", "RoomSeatUpdate", harness.Host.Body(503));
        var user = Assert.IsType<Dictionary<string, object?>>(frame["user"]);
        Assert.Equal((byte)0, user["slot"]);
        Assert.Equal(true, user["is_ai"]);
        Assert.Equal(11, user["ai_controller_user_number"]);
        Assert.Equal(1, user["ai_difficulty"]);
        Assert.Equal(2, user["ai_preset"]);
        var equipment = Assert.IsType<Dictionary<string, object?>>(frame["equipment"]);
        Assert.Equal(1, equipment["unknown_16"]);
        Assert.Equal(300025, equipment["character_id"]);
        Assert.Equal(new object?[] { 203108 }, Assert.IsType<object?[]>(equipment["equipped_items"]));

        await harness.Dispatch(harness.Guest, 403, Write("RoomJoinRequest", new() { ["room_id"] = harness.Room.Id, ["password"] = "" }));
        var install = PacketCodec.Read(Tables, "cn-774", "gspGotoRoom", harness.Guest.Body(117));
        var users = Assert.IsType<object?[]>(install["user_slots"]);
        Assert.Equal(true, Assert.IsType<Dictionary<string, object?>>(users[1])["is_ai"]);
        var appearances = Assert.IsType<object?[]>(install["equipped_objects"]);
        Assert.Equal(new[] { -1, -1, 1 }, appearances.Select(row =>
            (int)Assert.IsType<Dictionary<string, object?>>(row)["unknown_16"]!));
        await harness.Dispatch(harness.Guest, 2076, Write("RoomAiRemoveRequest", new() { ["slot"] = 1 }));
        Assert.Single(harness.Room.Bots);
        await harness.Dispatch(harness.Host, 508, Write("RoomMasterChangeRequest", new() { ["nickname"] = "guest" }));
        Assert.Equal(new RoomAiIdentity(2, 22, 1, 2), Assert.Single(harness.Room.Bots).Appearance.Ai);
        Assert.Equal(new byte[] { 11, 0, 0, 0, 2, 0, 0, 0 }, harness.Host.Body(1546));
        await harness.Dispatch(harness.Guest, 2076, Write("RoomAiRemoveRequest", new() { ["slot"] = 1 }));
        Assert.Empty(harness.Room.Bots);
    }

    /// <summary>The automatic add button fills only available seats and never displaces humans or closed slots.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task NativeAiAutomaticSelectionUsesOnlyAvailableSeats(int operation)
    {
        using var harness = await Harness.CreateAsync(ai: true, build: "cn-774");
        await harness.Dispatch(harness.Host, 402, PacketCodec.Write(Tables, "cn-774", "RoomCreateRequest", new Dictionary<string, object?>
        {
            ["session_setting"] = new Dictionary<string, object?>
            {
                ["title"] = "AI", ["open_seats"] = (ushort)4,
                ["game_setting"] = new Dictionary<string, object?> { ["unknown_4"] = 38 },
            },
            ["password"] = "", ["unknown_n2"] = (byte)0xBA,
        }));
        Assert.True(harness.Room.SetSlotClosed(1, true));
        var add = PacketCodec.Write(Tables, "cn-774", "RoomAiAddRequest", new Dictionary<string, object?>
        {
            ["team"] = (byte)2, ["selection_mode"] = operation, ["slot"] = -1, ["difficulty"] = -1, ["preset"] = -1,
        });
        Assert.Equal(17, add.Length);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Dispatch(harness.Guest, 2075, add));
        Assert.Empty(harness.Room.Bots);
        await harness.Dispatch(harness.Host, 2075, add);
        var bot = Assert.Single(harness.Room.Bots).Appearance;
        Assert.Equal(2, bot.Slot);
        Assert.Equal(2, bot.Team);
        if (operation == 1)
            Assert.Equal("host", bot.Nickname);
        await harness.Dispatch(harness.Host, 2075, add);
        await harness.Dispatch(harness.Host, 2075, add);
        Assert.Equal(new[] { 2, 3 }, harness.Room.Bots.Select(item => item.Appearance.Slot));
        Assert.Single(harness.Room.Seats);
    }

    /// <summary>Only audited operation codes and complete selector forms are accepted by the direct decoder.</summary>
    [Fact]
    public void NativeAiDirectDecoderRejectsMalformedOperations()
    {
        var table = BubbleFighter.Fantasy.Protocol.OpcodeTableLoader.Load(Path.Combine(Root, "profiles", "cn-774", "opcodes.yaml"),
            TestWireDialects.Get("cn-774").Profile);
        var binding = new RoomAiBinding("cn-774/game", table, Tables, "cn-774", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var body = PacketCodec.Write(Tables, "cn-774", "RoomAiAddRequest", new Dictionary<string, object?>
        {
            ["team"] = (byte)1, ["selection_mode"] = 0, ["slot"] = -1, ["difficulty"] = -1, ["preset"] = -1,
        });
        Assert.Equal(RoomAiSelection.Random, binding.ReadAdd(body).Selection);
        foreach (var invalid in new[] { body[..^1], body.Concat(new byte[] { 0 }).ToArray() })
            Assert.Throws<BubbleFighter.Fantasy.Abstractions.Protocol.PacketFormatException>(() => binding.ReadAdd(invalid));
        body[1] = 3;
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.Protocol.PacketFormatException>(() => binding.ReadAdd(body));
        body[1] = 0;
        body[5] = 0;
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.Protocol.PacketFormatException>(() => binding.ReadAdd(body));
    }
}
