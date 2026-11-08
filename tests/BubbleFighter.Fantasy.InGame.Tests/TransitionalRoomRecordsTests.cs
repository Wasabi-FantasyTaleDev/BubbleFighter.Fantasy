using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Exercises shared room projections with the native separate seat and equipment vectors.</summary>
public sealed class TransitionalRoomRecordsTests
{
    private static readonly PacketTables Tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());

    [Theory]
    [InlineData("cn-735", 188)]
    [InlineData("cn-738", 188)]
    [InlineData("cn-742", 192)]
    public void InstallKeepsSeatIndexesEquipmentAndPartyTransition(string build, int seatLength)
    {
        RoomMemberView[] seats =
        [
            new(2, 22, "guest", 17, 2, true, 300002, [300002, 100003]) { ColorIndex = 5 },
            new(0, 11, "host", 9, 1, false, 300001, [300001, 100002]) { ColorIndex = 3 },
        ];
        foreach (var transition in new[] { false, true })
        {
            var body = RoomRecords.Install(Tables, build,
                new Dictionary<string, object?> { ["kind"] = 2, ["session_state"] = 2 }, seats, 0, 2, transition);
            var fields = PacketCodec.Read(Tables, build, "gspGotoRoom", body);
            Assert.Equal(0, fields["master_slot"]);
            Assert.Equal(2, fields["local_slot"]);
            Assert.Equal(transition, fields["party_transition"]);
            var users = Assert.IsType<object?[]>(fields["user_slots"]);
            var equipment = Assert.IsType<object?[]>(fields["equipped_item_lists"]);
            Assert.Equal(3, users.Length);
            Assert.Equal(3, equipment.Length);
            for (var index = 0; index < 3; index++)
            {
                var user = Assert.IsType<Dictionary<string, object?>>(users[index]);
                var items = Assert.IsType<Dictionary<string, object?>>(equipment[index]);
                var member = seats.SingleOrDefault(seat => seat.Slot == index);
                Assert.Equal(member is not null, user["occupied"]);
                Assert.Equal(member?.UserNum ?? 0, user["user_number"]);
                Assert.Equal(member?.UserNum ?? 0, items["user_number"]);
                Assert.Equal(member?.CharacterItemId ?? 300000, items["character_item"]);
                Assert.Equal(member?.Nickname ?? "", user["nickname"]);
                Assert.Equal(checked((byte)(member?.Team ?? 0)), user["team"]);
                Assert.Equal(member?.Ready ?? false, user["ready"]);
                Assert.Equal(member is not null ? member.ColorIndex : (ushort)0, user["color_index"]);
                Assert.Equal(member?.Level ?? 0, user["level"]);
                Assert.Equal(member?.EquippedItemIds ?? [], Assert.IsType<object?[]>(items["equipped_items"]).Select(Convert.ToInt32));
                Assert.Equal(seatLength + 2 * (member?.Nickname.Length ?? 0) + 4 * (member?.EquippedItemIds.Count ?? 0),
                    PacketCodec.Write(Tables, build, "UserSlot", user).Length);
                Assert.Equal(member is null ? (byte)255 : checked((byte)index), user["controller_slot"]);
                Assert.Equal(0, user["unknown_152"]);
            }
            Assert.Equal(body, PacketCodec.Write(Tables, build, "gspGotoRoom", fields));
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, build, "gspGotoRoom", body[..^1]));
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, build, "gspGotoRoom", [.. body, 0]));
        }
    }

    [Theory]
    [InlineData("cn-735", 244)]
    [InlineData("cn-738", 244)]
    [InlineData("cn-742", 248)]
    public void SharedSeatUpdateCarriesTheCompleteEquipmentAndLeaveHasNoEquipment(string build, int updateLength)
    {
        var seat = new RoomMemberView(3, 17, "", 8, 2, true, 300007, [300007, 100011, 100012]) { ColorIndex = 4 };
        var update = RoomRecords.SeatUpdate(Tables, build, seat, [seat]);
        Assert.Equal(updateLength, update.Length);
        var fields = PacketCodec.Read(Tables, build, "RoomSeatUpdate", update);
        Assert.Equal(3, fields["slot"]);
        var user = Assert.IsType<Dictionary<string, object?>>(fields["user"]);
        var equipment = Assert.IsType<Dictionary<string, object?>>(fields["equipment"]);
        Assert.Equal(17, user["user_number"]);
        Assert.Equal((byte)3, user["controller_slot"]);
        Assert.Equal((ushort)7, user["character_type"]);
        Assert.Equal((ushort)4, user["color_index"]);
        Assert.Equal(8, user["level"]);
        Assert.Equal(true, user["ready"]);
        Assert.Equal(17, equipment["user_number"]);
        Assert.Equal(300007, equipment["character_item"]);
        Assert.Equal(new[] { 300007, 100011, 100012 }, Assert.IsType<object?[]>(equipment["equipped_items"]).Select(Convert.ToInt32));
        foreach (var closed in new[] { false, true })
        {
            var leave = RoomRecords.SeatLeave(Tables, build, 3, closed);
            Assert.Equal(updateLength - 52, leave.Length);
            var vacated = PacketCodec.Read(Tables, build, "RoomSeatLeave", leave);
            Assert.Equal(3, vacated["slot"]);
            var vacant = Assert.IsType<Dictionary<string, object?>>(vacated["user"]);
            Assert.Equal(false, vacant["occupied"]);
            Assert.Equal(closed, vacant["closed"]);
            Assert.Equal(0, vacant["user_number"]);
            Assert.Equal((byte)255, vacant["controller_slot"]);
            Assert.False(vacated.ContainsKey("equipment"));
            Assert.Throws<PacketFormatException>(() => PacketCodec.Read(Tables, build, "RoomSeatLeave", [.. leave, 0]));
        }
    }

    [Theory]
    [InlineData("cn-735")]
    [InlineData("cn-738")]
    [InlineData("cn-742")]
    public void InstallRetainsCompleteNativeSettingsAndBoundsTheSeatVector(string build)
    {
        var game = new Dictionary<string, object?>
        {
            ["mode"] = 3,
            ["unknown_365"] = new[] { new Dictionary<string, object?> { ["unknown_0"] = 17, ["unknown_4"] = 23 } },
            ["unknown_382"] = new[] { new Dictionary<string, object?> { ["unknown_0"] = 29, ["unknown_4"] = "option" } },
            ["unknown_420"] = new[] { "first", "second" },
        };
        var setting = new Dictionary<string, object?>
        {
            ["session_state"] = 2, ["kind"] = 2, ["room_id"] = (ushort)91, ["title"] = "room", ["game_setting"] = game,
        };
        var member = new RoomMemberView(35, 47, "last", 1, 2, false, 300001, [300001]);
        var body = RoomRecords.Install(Tables, build, setting, [member], 35, 35);
        var decoded = PacketCodec.Read(Tables, build, "gspGotoRoom", body);
        Assert.Equal(PacketCodec.Write(Tables, build, "SessionSetting", setting),
            PacketCodec.Write(Tables, build, "SessionSetting", Assert.IsType<Dictionary<string, object?>>(decoded["session_setting"])));
        var users = Assert.IsType<object?[]>(decoded["user_slots"]);
        Assert.Equal(36, users.Length);
        Assert.All(users.Take(35), value => Assert.Equal(false, Assert.IsType<Dictionary<string, object?>>(value)["occupied"]));
        Assert.Equal(47, Assert.IsType<Dictionary<string, object?>>(users[35])["user_number"]);
        Assert.Equal(36, Assert.IsType<object?[]>(decoded["equipped_item_lists"]).Length);
        var empty = PacketCodec.Read(Tables, build, "gspGotoRoom", RoomRecords.Install(Tables, build, setting, [], -1, -1));
        Assert.Empty(Assert.IsType<object?[]>(empty["user_slots"]));
        Assert.Empty(Assert.IsType<object?[]>(empty["equipped_item_lists"]));
        Assert.Throws<ArgumentException>(() => RoomRecords.Install(Tables, build, setting, [member, member], 35, 35));
        foreach (var index in new[] { -1, 36 })
        {
            var invalid = member with { Slot = index };
            Assert.Throws<ArgumentException>(() => RoomRecords.Install(Tables, build, setting, [invalid], 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => RoomRecords.SeatUpdate(Tables, build, invalid, [invalid]));
            Assert.Throws<ArgumentOutOfRangeException>(() => RoomRecords.SeatLeave(Tables, build, index));
        }
        var bot = member with { Ai = new RoomAiIdentity(0, 11, 1, 1) };
        Assert.Throws<ArgumentException>(() => RoomRecords.Install(Tables, build, setting, [bot], 0, 0));
        Assert.Throws<ArgumentException>(() => RoomRecords.SeatUpdate(Tables, build, bot, [bot]));
    }
}
