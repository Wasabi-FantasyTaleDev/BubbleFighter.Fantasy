using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Byte-length pins for the kr-812 multi-player room packets. The expected lengths follow
/// the kr-812 readers for the same shape of content.
/// </summary>
public sealed class PlayRoomWireTests
{
    private static readonly RoomSchema Schema = TestBuilds.Of("kr-812").Schema;

    [Theory]
    [InlineData("cn-324", "rel_Stadium01_Pangpang", "rel_Stadium01_Pangpang")]
    [InlineData("kr-812", "rel_Stadium01_Pangpang", "rel_Stadium01_Pangpang")]
    [InlineData("cn-324", "", "rel_village08_daohouse")]
    [InlineData("kr-812", "", "rel_village08_daohouse")]
    public void CreateRequestPrefersTheChosenMapKeyAndRetainsAnUnchosenNativeDirectory(string build, string selected, string expected)
    {
        var schema = TestBuilds.Of(build).Schema;
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        RoomCodec.WriteSessionSetting(writer, schema, new RoomValues(0, [])
        {
            Play = true, Mode = 101, CurrentMap = "rel_village08_daohouse", MapName = selected,
        });
        writer.WriteWideStringPrefixed("");
        writer.WriteBool32(false);
        var request = PlazaRoomWire.ReadCreateRoom(buffer.WrittenSpan, schema);
        Assert.Equal(expected, request.MapKey);
    }

    // Twelve-character nickname and nineteen worn items.
    private static readonly RoomSlotValues Player = new(true, false, 282, "TwelveLetter", 300010,
        [.. Enumerable.Range(100000, 19)], false);

    private static byte[] Slot(int index, RoomSlotValues slot, bool items)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var w = new PacketWriter(buffer);
        w.WriteInt32(index);
        if (items)
            RoomCodec.WritePlaySlotAndItems(w, Schema, slot);
        else
            RoomCodec.WritePlayUserSlot(w, Schema, slot);
        return buffer.WrittenSpan.ToArray();
    }

    [Fact]
    public void RoomSnapshotWithOnePlayerAndElevenEmptySlotsHasTheRecordedLength()
    {
        // s2c#144: an eleven-character title, a 304-byte GameSetting, one seated player, slots 4-11 closed.
        var slots = Enumerable.Range(0, 12).Select(i => i == 0 ? Player : RoomSlotValues.Empty(i >= 4)).ToArray();
        var body = RoomCodec.WriteRoom(Schema, new RoomValues(RoomCodec.PlayRoomKind, slots)
        {
            Play = true, RoomId = 400, Title = "ElevenChars", SessionState = 2, OpenSeats = 4, GameSetting = new byte[304],
        });

        Assert.Equal(3230, body.Length);
        Assert.Equal(2, BitConverter.ToInt32(body, 0));
        Assert.Equal(400, BitConverter.ToUInt16(body, 4));
        Assert.Equal(11, BitConverter.ToInt32(body, 6));
        // After the title: seated players, an unnamed u16, open seats.
        Assert.Equal([1, 0, 4], new[] { 32, 34, 36 }.Select(offset => (int)BitConverter.ToUInt16(body, offset)));
        Assert.Equal(RoomCodec.PlayRoomKind, BitConverter.ToInt32(body, 46));
    }

    [Fact]
    public void SlotPacketsHaveTheRecordedLengths()
    {
        // s2c#152 gspRoomSetSlot for the seated player, s2c#2092 for a closed empty slot.
        Assert.Equal(400, Slot(0, Player, items: true).Length);
        var closed = Slot(3, RoomSlotValues.Empty(closed: true), items: true);
        Assert.Equal(224, closed.Length);
        Assert.Equal([0, 1, 0], new[] { 4, 8, 12 }.Select(offset => BitConverter.ToInt32(closed, offset)));
        // s2c#2112 gspRoomLeaveSlot: the leaver's record without the occupied marks and the equipment.
        var left = Slot(0, Player with { Occupied = false, Departed = true, EquippedItemIds = [] }, items: false);
        Assert.Equal(220, left.Length);
        Assert.Equal([0, 0, 0], new[] { 4, 8, 12 }.Select(offset => BitConverter.ToInt32(left, offset)));
        Assert.Equal(282, BitConverter.ToInt32(left, 17));
    }

    [Fact]
    public void CreateRequestGivesTheTitleModeMapAndTheGameSettingAsSent()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var w = new PacketWriter(buffer);
        RoomCodec.WriteSessionSetting(w, Schema, new RoomValues(0, [])
        {
            Play = true, Title = "my room", Mode = 101, CurrentMap = "rel_Village01_Byway", MapName = "rel_Village01_Byway",
        });
        var settingLength = buffer.WrittenCount;
        w.WriteWideStringPrefixed("pw");
        w.WriteBool32(true);
        var body = buffer.WrittenSpan.ToArray();

        var request = PlazaRoomWire.ReadCreateRoom(body, Schema);

        Assert.Equal(("my room", 101, "rel_Village01_Byway", "pw", true),
            (request.Title, request.Mode, request.MapKey, request.Password, request.Unknown0));
        // The GameSetting starts after u32, u16, the title, three u16, bool32, an empty wstr and the kind.
        var gameStart = 4 + 2 + (4 + 14) + 6 + 4 + 4 + 4;
        // After it: three u32, an empty wstr, four u32 and a bool32.
        var gameEnd = settingLength - (12 + 4 + 16 + 4);
        Assert.Equal(body[gameStart..gameEnd], request.GameSetting);
        Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ReadCreateRoom([.. body, 0], Schema));
    }

    [Fact]
    public void JoinRequestIsTwentyBytesPlusThePassword()
    {
        // c2s#2068: room 404 with an empty password is 20 bytes.
        var buffer = new ArrayBufferWriter<byte>();
        var w = new PacketWriter(buffer);
        w.WriteInt32(404);
        w.WriteInt32(1200);
        w.WriteInt32(1971401584);
        w.WriteWideStringPrefixed("");
        w.WriteInt32(0);
        Assert.Equal(20, buffer.WrittenCount);

        var request = PlazaRoomWire.ReadJoinSession(TestBuilds.Packets, "kr-812", buffer.WrittenSpan);

        Assert.Equal((404, 1200, 1971401584, "", 0),
            (request.RoomId, request.Unknown1, request.Unknown2, request.Password, request.Unknown4));
        Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ReadJoinSession(TestBuilds.Packets, "kr-812", buffer.WrittenSpan[..19]));
    }

    [Fact]
    public void Cn324ServerOpenedRoomCarriesTheValuesOfTheClientsOwnCreateRequest()
    {
        // A quick-match room has no client block; cn-324 sub_582C00 and sub_57D470 fill KillLimit 20,
        // TimeLimit 240000 x4, both bools 1, +68 = 1 (the horde difficulty) and +72 = -1.
        var block = PlazaRoomWire.ApplyGameSettingChange(TestBuilds.Of("cn-324").Schema, TestBuilds.Packets, "cn-324", default, default);
        var r = new PacketReader(block);
        // mode and sub-mode come from the master's settings change, not from the table
        r.ReadInt32();
        r.ReadInt32();
        for (var i = 0; i < 4; i++)
            r.ReadWideStringPrefixed();
        Assert.Equal([20, 240000, 240000, 240000, 240000], new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32() });
        Assert.True(r.ReadBool32());
        Assert.True(r.ReadBool32());
        Assert.Equal([0, 0, 0, 0, 1, -1], new[] { r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32() });
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void ASettingsChangeReplacesTheFourStringsTheBoolsAndTheModeAndKeepsTheRest()
    {
        // c2s#155 shape: wstr x4, bool32 x2, u32 x10, bool32; 172 bytes with the recorded strings.
        var buffer = new ArrayBufferWriter<byte>();
        var w = new PacketWriter(buffer);
        foreach (var text in new[] { "display!!", "rel_Village07_Easy02", "All", "npc_Village07_Easy01" })
            w.WriteWideStringPrefixed(text);
        w.WriteBool32(true);
        w.WriteBool32(true);
        foreach (var value in new[] { 240000, 20, 1, 0, 0, 7, 8, 1, 101, 5 })
            w.WriteInt32(value);
        w.WriteBool32(false);
        var change = buffer.WrittenSpan.ToArray();
        Assert.Equal(172, change.Length);

        var blank = PlazaRoomWire.ApplyGameSettingChange(Schema, TestBuilds.Packets, "kr-812", default, default);
        var applied = PlazaRoomWire.ApplyGameSettingChange(Schema, TestBuilds.Packets, "kr-812", blank, change);

        // Only the four strings grow the block; every other field keeps its width.
        Assert.Equal(blank.Length + 2 * (9 + 20 + 3 + 20), applied.Length);
        Assert.Equal((101, 5), (BitConverter.ToInt32(applied, 0), BitConverter.ToInt32(applied, 4)));
        var r = new PacketReader(applied.AsSpan(8));
        Assert.Equal(["display!!", "rel_Village07_Easy02", "All", "npc_Village07_Easy01"],
            new[] { r.ReadWideStringPrefixed(), r.ReadWideStringPrefixed(), r.ReadWideStringPrefixed(), r.ReadWideStringPrefixed() });
        // Applying the same change again changes nothing.
        Assert.Equal(applied, PlazaRoomWire.ApplyGameSettingChange(Schema, TestBuilds.Packets, "kr-812", applied, change));
        Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ReadChangeGameSetting(TestBuilds.Packets, "kr-812", change.AsSpan(0, 171)));
    }

    [Theory]
    [InlineData(RoomJoinRefusal.Started, 1)]
    [InlineData(RoomJoinRefusal.Full, 3)]
    [InlineData(RoomJoinRefusal.WrongPassword, 4)]
    [InlineData(RoomJoinRefusal.NoRoom, 5)]
    public void JoinRefusalsUseTheCodesTheLobbyShowsAMessageFor(RoomJoinRefusal reason, int code) =>
        Assert.Equal(code, PlazaRoomWire.JoinResultCode(reason));
}
