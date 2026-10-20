using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Byte-length pins for the id-400 play-room packets: readers <c>sub_5DB3C0</c>
/// (SessionSetting), <c>sub_5DAE00</c> (GameSetting), <c>sub_5F1FF0</c> (UserSlot),
/// <c>sub_782970</c> (RoomEnterGame with its 2252-byte ScoreData) and <c>sub_782DD0</c>
/// (two-u32 StartGame / EndGame). The expected lengths
/// are computed field by field from the readers.
/// </summary>
public sealed class PlayRoomWireId400Tests
{
    private static readonly RoomSchema Schema = TestBuilds.Of("id-400").Schema;

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
    public void RoomSnapshotWithOnePlayerAndSevenEmptySlotsHasTheComputedLength()
    {
        var slots = Enumerable.Range(0, 8).Select(i => i == 0 ? Player : RoomSlotValues.Empty(closed: true)).ToArray();
        var body = RoomCodec.WriteRoom(Schema, new RoomValues(RoomCodec.PlayRoomKind, slots)
        {
            Play = true, RoomId = 400, Title = "ElevenChars", SessionState = 2,
        });

        Assert.Equal(1672, body.Length);
        Assert.Equal(2, BitConverter.ToInt32(body, 0));
        Assert.Equal(400, BitConverter.ToUInt16(body, 4));
        Assert.Equal(11, BitConverter.ToInt32(body, 6));
        // After the title: occupants and an unnamed u16; this build has no open-seats u16.
        Assert.Equal([1, 0], new[] { 32, 34 }.Select(offset => (int)BitConverter.ToUInt16(body, offset)));
        Assert.Equal(RoomCodec.PlayRoomKind, BitConverter.ToInt32(body, 44));
    }

    [Fact]
    public void SlotPacketsHaveTheComputedLengths()
    {
        // gspRoomSetSlot (reader sub_782540): u32 index, UserSlot, EquippedItemList.
        Assert.Equal(336, Slot(0, Player, items: true).Length);
        var closed = Slot(3, RoomSlotValues.Empty(closed: true), items: true);
        Assert.Equal(160, closed.Length);
        Assert.Equal([0, 1, 0], new[] { 4, 8, 12 }.Select(offset => BitConverter.ToInt32(closed, offset)));
        // gspRoomLeaveSlot (handler sub_785000): the leaver's record without the occupied marks.
        var left = Slot(0, Player with { Occupied = false, Departed = true, EquippedItemIds = [] }, items: false);
        Assert.Equal(156, left.Length);
        Assert.Equal([0, 0, 0], new[] { 4, 8, 12 }.Select(offset => BitConverter.ToInt32(left, offset)));
        Assert.Equal(282, BitConverter.ToInt32(left, 17));
    }

    [Fact]
    public void RoomSetSessionIsTheBareSessionSetting()
    {
        // gspRoomSetSession (reader sub_5DB3C0 via packet sub_784590): the SessionSetting alone.
        var buffer = new ArrayBufferWriter<byte>();
        RoomCodec.WriteSessionSetting(new PacketWriter(buffer), Schema,
            new RoomValues(RoomCodec.MainMenuKind, [Player]) { Play = true });
        Assert.Equal(202, buffer.WrittenCount);
    }

    [Fact]
    public void CreateRequestGivesTheTitleModeMapAndTheGameSettingAsSent()
    {
        // gcpReqCreateSession keeps the SessionSetting, password wstr and bool32 tail on this build.
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
        // The GameSetting starts after u32, u16, the title, two u16, bool32, an empty wstr and the kind.
        var gameStart = 4 + 2 + (4 + 14) + 4 + 4 + 4 + 4;
        // After it: three u32, an empty wstr and four u32.
        var gameEnd = settingLength - (12 + 4 + 16);
        Assert.Equal(body[gameStart..gameEnd], request.GameSetting);
        Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ReadCreateRoom([.. body, 0], Schema));
    }

    [Fact]
    public void JoinRequestEndsInAU16()
    {
        // serialize sub_5DB910: u32 x3, wstr, u16 — kr-812's tail is a u32.
        var buffer = new ArrayBufferWriter<byte>();
        var w = new PacketWriter(buffer);
        w.WriteInt32(404);
        w.WriteInt32(1200);
        w.WriteInt32(1971401584);
        w.WriteWideStringPrefixed("");
        w.WriteUInt16(0);
        Assert.Equal(18, buffer.WrittenCount);

        var request = PlazaRoomWire.ReadJoinSession(TestBuilds.Packets, "id-400", buffer.WrittenSpan);

        Assert.Equal((404, 1200, 1971401584, "", 0),
            (request.RoomId, request.Unknown1, request.Unknown2, request.Password, request.Unknown4));
        Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ReadJoinSession(TestBuilds.Packets, "id-400", buffer.WrittenSpan[..17]));
        // The kr-812 u32 tail leaves two trailing bytes under this build's shape.
        Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ReadJoinSession(TestBuilds.Packets, "id-400", [.. buffer.WrittenSpan, 0, 0]));
    }

    [Fact]
    public void ASettingsChangeHasNoTrailingBool()
    {
        // serialize sub_5EAB40: wstr x4, bool32 x2, u32 x10 — kr-812 appends one more bool32.
        var buffer = new ArrayBufferWriter<byte>();
        var w = new PacketWriter(buffer);
        foreach (var text in new[] { "display!!", "rel_Village07_Easy02", "All", "npc_Village07_Easy01" })
            w.WriteWideStringPrefixed(text);
        w.WriteBool32(true);
        w.WriteBool32(true);
        foreach (var value in new[] { 240000, 20, 1, 0, 0, 7, 8, 1, 101, 5 })
            w.WriteInt32(value);
        var change = buffer.WrittenSpan.ToArray();
        Assert.Equal(168, change.Length);

        var blank = PlazaRoomWire.ApplyGameSettingChange(Schema, TestBuilds.Packets, "id-400", default, default);
        var applied = PlazaRoomWire.ApplyGameSettingChange(Schema, TestBuilds.Packets, "id-400", blank, change);

        // Only the four strings grow the block; every other field keeps its width.
        Assert.Equal(blank.Length + 2 * (9 + 20 + 3 + 20), applied.Length);
        Assert.Equal((101, 5), (BitConverter.ToInt32(applied, 0), BitConverter.ToInt32(applied, 4)));
        var r = new PacketReader(applied.AsSpan(8));
        Assert.Equal(["display!!", "rel_Village07_Easy02", "All", "npc_Village07_Easy01"],
            new[] { r.ReadWideStringPrefixed(), r.ReadWideStringPrefixed(), r.ReadWideStringPrefixed(), r.ReadWideStringPrefixed() });
        Assert.Equal(applied, PlazaRoomWire.ApplyGameSettingChange(Schema, TestBuilds.Packets, "id-400", applied, change));
        Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ReadChangeGameSetting(TestBuilds.Packets, "id-400", change.AsSpan(0, 167)));
    }

    [Fact]
    public void EnterGameCarriesTheScoreDataBlock()
    {
        // reader sub_782970: u32, wstr, ScoreData (2252 bytes, reader sub_781180), u32 x3 — no trailing bools.
        var body = TutorialWire.WriteRoomEnterGame(TestBuilds.Packets, "id-400", 0, "rel_Village01_Byway", unknown4: 3);
        Assert.Equal(2310, body.Length);
        // The ScoreData count field (17) opens the block after the map key.
        var score = 4 + 4 + 2 * 19;
        Assert.Equal(17, BitConverter.ToInt32(body, score));
        // id-400 sub_780A20 resets each slot and unknown_308 to all-one bits.
        Assert.Equal(-1, BitConverter.ToInt32(body, score + 292));
        // The second element starts one element (241 bytes) later; a shorter element would misplace it.
        Assert.Equal(-1, BitConverter.ToInt32(body, score + 292 + 241));
        Assert.Equal(-1, BitConverter.ToInt32(body, score + 292 + 193));
        Assert.Equal(3, BitConverter.ToInt32(body, body.Length - 4));
        Assert.Equal(2272, TutorialWire.WriteRoomEnterGame(TestBuilds.Packets, "id-400", 0, "", unknown4: 3).Length);
    }

    [Fact]
    public void RoundPacketsUseTheTwoFieldShapes()
    {
        // sub_93D120 one u32; sub_789C60 two bool32; sub_782DD0 two u32 for StartGame and EndGame.
        Assert.Equal(4, TutorialWire.WriteMapLoadingComplete(TestBuilds.Packets, "id-400", 5).Length);
        Assert.Equal(8, TutorialWire.WriteEverybodyLoadingComplete(TestBuilds.Packets, "id-400", false, false, false).Length);
        Assert.Equal(4, TutorialWire.WriteGameCountDown(TestBuilds.Packets, "id-400", 6500).Length);
        Assert.Equal(8, TutorialWire.WriteGameStartGame(TestBuilds.Packets, "id-400", 300000, 0, 0).Length);
    }
}
