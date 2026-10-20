using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Byte-length pins for plaza list / create / ready / host-start wire codecs on the
/// proven builds (kr-812, cn-717, id-400 MapMeta width).
/// </summary>
public sealed class PlazaRoomWireTests
{
    [Fact]
    public void EmptyListSessionReplyIsTwelveBytes()
    {
        var body = PlazaRoomWire.WriteRepListSessionEmpty(TestBuilds.Packets, "kr-812");
        Assert.Equal(12, body.Length);
        Assert.Equal(0, BitConverter.ToInt32(body, 0));
        Assert.Equal(0, BitConverter.ToInt32(body, 4));
        Assert.Equal(0, BitConverter.ToInt32(body, 8));
    }

    [Fact]
    public void ListSessionElementPinsId1809SessionSettingWidth()
    {
        // id-1809 Packet_gspRepListSession reader sub_623930 reads count SessionSetting
        // elements via sub_4173B0 — kr-812's outer field order (extra u16, trailing bool)
        // around GameSetting sub_4168C0 (id-400's plus a 24-byte tail). One element body:
        // 12-byte envelope + 232-byte SessionSetting here against id-400's 202 and
        // kr-812's 236.
        var room = new MainMenuRoomOpened(11, "host", 300001, [300001]);
        var kr812 = new ArrayBufferWriter<byte>();
        RoomCodec.WriteSessionSetting(new PacketWriter(kr812), room, TestBuilds.Of("kr-812").Schema, RoomCodec.PlayRoomKind);
        var id400 = new ArrayBufferWriter<byte>();
        RoomCodec.WriteSessionSetting(new PacketWriter(id400), room, TestBuilds.Of("id-400").Schema, RoomCodec.PlayRoomKind);
        var id1809 = new ArrayBufferWriter<byte>();
        RoomCodec.WriteSessionSetting(new PacketWriter(id1809), room, TestBuilds.Of("id-1809").Schema, RoomCodec.PlayRoomKind);
        Assert.Equal(236, kr812.WrittenCount);
        Assert.Equal(202, id400.WrittenCount);
        Assert.Equal(232, id1809.WrittenCount);
        // A one-element id-1809 reply is 12 (envelope) + 232.
        Assert.Equal(244, 12 + id1809.WrittenCount);
    }

    [Fact]
    public void ListSessionReadsTwoU16()
    {
        var (a, b) = PlazaRoomWire.ReadListSession(TestBuilds.Packets, "kr-812", [0x01, 0x00, 0x0A, 0x00]);
        Assert.Equal(1, a);
        Assert.Equal(10, b);
    }

    [Fact]
    public void ListSessionRejectsWrongLength()
    {
        Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ReadListSession(TestBuilds.Packets, "kr-812", [0, 0]));
        Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ReadListSession(TestBuilds.Packets, "kr-812", [0, 0, 0, 0, 0]));
    }

    [Fact]
    public void ReadyStatusReadsThreeBool32()
    {
        var body = new byte[12];
        BitConverter.TryWriteBytes(body.AsSpan(0), 1);
        BitConverter.TryWriteBytes(body.AsSpan(4), 0);
        BitConverter.TryWriteBytes(body.AsSpan(8), 0);
        var (a, b, c) = PlazaRoomWire.ReadReadyStatus(TestBuilds.Packets, "kr-812", body);
        Assert.True(a);
        Assert.False(b);
        Assert.False(c);
    }

    [Fact]
    public void ReadyStatusRejectsWrongLength()
    {
        Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ReadReadyStatus(TestBuilds.Packets, "kr-812", new byte[8]));
        Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ReadReadyStatus(TestBuilds.Packets, "kr-812", new byte[16]));
    }

    [Fact]
    public void UserHostStartRequiresEmptyBody()
    {
        PlazaRoomWire.ReadUserHostStart([]);
        Assert.Throws<PacketFormatException>(() => PlazaRoomWire.ReadUserHostStart([0]));
    }

    [Fact]
    public void CreateSessionRoundTripConsumesSchemaPlusPasswordAndFlag()
    {
        var room = new MainMenuRoomOpened(11, "host", 300001, [300001]);
        var body = PlazaRoomWire.WriteCreateSessionForTest(room, TestBuilds.Of("kr-812").Schema, "secret", unknown0: false);
        var parsed = PlazaRoomWire.ReadCreateSession(body, TestBuilds.Of("kr-812").Schema);
        Assert.Equal("", parsed.MapKey);
        Assert.Equal("secret", parsed.Password);
        Assert.False(parsed.Unknown0);
    }

    [Fact]
    public void SetSlotPinsSlotPrefixAndGrowsWithReady()
    {
        var room = new MainMenuRoomOpened(11, "host", 300001, [300001]);
        var idle = PlazaRoomWire.WriteSetSlot(room, TestBuilds.Packets, "kr-812", TestBuilds.Of("kr-812").Schema, slot: 0, ready: false);
        var ready = PlazaRoomWire.WriteSetSlot(room, TestBuilds.Packets, "kr-812", TestBuilds.Of("kr-812").Schema, slot: 0, ready: true);
        Assert.Equal(0, BitConverter.ToInt32(idle, 0));
        Assert.Equal(idle.Length, ready.Length);
        Assert.True(idle.Length > 4);
    }

    [Fact]
    public void SetSlotPinsId1809LongerUserSlot()
    {
        // id-1809 reader sub_7A5A30: u32 slot + UserSlot (sub_417880) + EquippedItemList
        // (sub_4D4960). The 240-byte UserSlot replaces id-400's u32,bool,u32,u32,u32 tail with
        // wstr+bool+bool+8 u32, growing the body 24 bytes over id-400's 176.
        var room = new MainMenuRoomOpened(11, "host", 300001, [300001]);
        var id400 = PlazaRoomWire.WriteSetSlot(room, TestBuilds.Packets, "id-400", TestBuilds.Of("id-400").Schema, slot: 0, ready: false);
        var body = PlazaRoomWire.WriteSetSlot(room, TestBuilds.Packets, "id-1809", TestBuilds.Of("id-1809").Schema, slot: 0, ready: false);
        Assert.Equal(176, id400.Length);
        Assert.Equal(200, body.Length);
        Assert.Equal(0, BitConverter.ToInt32(body, 0));
    }

    [Fact]
    public void PlazaEnterGameMatchesKr812TrailingBoolCount()
    {
        var body = TutorialWire.WriteRoomEnterGame(TestBuilds.Packets, "kr-812", 0, "rel_Village07_Easy02");
        // u32 + (u32 len + 2*chars) + u32*3 + bool32*2
        var expected = 4 + (4 + "rel_Village07_Easy02".Length * 2) + 12 + 8;
        Assert.Equal(expected, body.Length);
        Assert.Equal(0, BitConverter.ToInt32(body, 0));
    }

    [Fact]
    public void RoomReqEnterGameReadsMapKeyAndMapMetaFootprint()
    {
        // bool32 + wstr map + two empty wstr + u32 + MapMeta (13 u32 + 6 bool32) = kr-812 sub_5C8D90.
        using var ms = new System.IO.MemoryStream();
        void WriteBool32(int v) { ms.Write(BitConverter.GetBytes(v)); }
        void WriteWStr(string s)
        {
            ms.Write(BitConverter.GetBytes(s.Length));
            ms.Write(System.Text.Encoding.Unicode.GetBytes(s));
        }

        WriteBool32(0);
        WriteWStr("rel_Village07_Easy02");
        WriteWStr("");
        WriteWStr("");
        WriteBool32(1);
        for (var i = 0; i < 13; i++)
            WriteBool32(0);
        for (var i = 0; i < 6; i++)
            WriteBool32(0);

        var (mapKey, unknown1) = PlazaRoomWire.ReadRoomReqEnterGame(TestBuilds.Packets, "kr-812", ms.ToArray());
        Assert.Equal("rel_Village07_Easy02", mapKey);
        Assert.Equal(1, unknown1);
        // bool32 + map wstr + 2 empty wstr + u32 + 13 u32 + 6 bool32
        Assert.Equal(4 + (4 + 40) + 4 + 4 + 4 + (13 * 4) + (6 * 4), ms.Length);
    }

    [Fact]
    public void RoomReqEnterGameRejectsWrongMapMetaBoolCount()
    {
        var body = new byte[4 + 4 + 4 + 4 + 4 + (13 * 4) + (5 * 4)];
        Assert.Throws<PacketFormatException>(() =>
            PlazaRoomWire.ReadRoomReqEnterGame(TestBuilds.Packets, "kr-812", body));
    }

    [Fact]
    public void RoomReqEnterGameReadsId400FiveMapMetaBools()
    {
        // id-400 MapMetaData sub_567300: 13 u32 + 5 bool32 (kr-812 / cn-717 have six).
        using var ms = new System.IO.MemoryStream();
        void WriteBool32(int v) => ms.Write(BitConverter.GetBytes(v));
        void WriteWStr(string s)
        {
            ms.Write(BitConverter.GetBytes(s.Length));
            ms.Write(System.Text.Encoding.Unicode.GetBytes(s));
        }

        WriteBool32(0);
        WriteWStr("rel_Village07_Easy02");
        WriteWStr("");
        WriteWStr("");
        WriteBool32(1);
        for (var i = 0; i < 13; i++)
            WriteBool32(0);
        for (var i = 0; i < 5; i++)
            WriteBool32(0);

        var (mapKey, unknown1) = PlazaRoomWire.ReadRoomReqEnterGame(TestBuilds.Packets, "id-400", ms.ToArray());
        Assert.Equal("rel_Village07_Easy02", mapKey);
        Assert.Equal(1, unknown1);
        Assert.Equal(4 + (4 + 40) + 4 + 4 + 4 + (13 * 4) + (5 * 4), ms.Length);
    }

    [Fact]
    public void RoomReqEnterGameReadsId1809SixMapMetaBools()
    {
        // id-1809 reader sub_5816C0 / serializer sub_581670: bool32 + 3 wstr + u32 +
        // MapMetaData sub_416C60 (13 u32 + 6 bool32) — the six-bool width, not id-400's five.
        using var ms = new System.IO.MemoryStream();
        void WriteBool32(int v) => ms.Write(BitConverter.GetBytes(v));
        void WriteWStr(string s)
        {
            ms.Write(BitConverter.GetBytes(s.Length));
            ms.Write(System.Text.Encoding.Unicode.GetBytes(s));
        }

        WriteBool32(0);
        WriteWStr("rel_Village07_Easy02");
        WriteWStr("");
        WriteWStr("");
        WriteBool32(1);
        for (var i = 0; i < 13; i++)
            WriteBool32(0);
        for (var i = 0; i < 6; i++)
            WriteBool32(0);

        var (mapKey, unknown1) = PlazaRoomWire.ReadRoomReqEnterGame(TestBuilds.Packets, "id-1809", ms.ToArray());
        Assert.Equal("rel_Village07_Easy02", mapKey);
        Assert.Equal(1, unknown1);
        Assert.Equal(4 + (4 + 40) + 4 + 4 + 4 + (13 * 4) + (6 * 4), ms.Length);
        Assert.Equal(136, ms.Length);
    }

    [Fact]
    public void PlazaEnterGameMatchesCn717TrailingBoolCount()
    {
        var body = TutorialWire.WriteRoomEnterGame(TestBuilds.Packets, "cn-717", 0, "rel_Village07_Easy02");
        // u32 + (u32 len + 2*chars) + u32*3 + bool32*3 (cn-717 reader sub_88F570)
        var expected = 4 + (4 + "rel_Village07_Easy02".Length * 2) + 12 + 12;
        Assert.Equal(expected, body.Length);
    }

    [Fact]
    public void CreateSessionRoundTripPinsId1809Schema()
    {
        // id-1809 reader sub_4500D0 / serializer sub_450090: SessionSetting sub_4173B0
        // (232 wire bytes with empty strings) + password wstr + bool32; id-400's is 202.
        var room = new MainMenuRoomOpened(11, "host", 300001, [300001]);
        var id400 = PlazaRoomWire.WriteCreateSessionForTest(room, TestBuilds.Of("id-400").Schema, "pw", unknown0: true);
        var body = PlazaRoomWire.WriteCreateSessionForTest(room, TestBuilds.Of("id-1809").Schema, "pw", unknown0: true);
        Assert.Equal(202 + 8 + 4, id400.Length);
        Assert.Equal(232 + 8 + 4, body.Length);
        var parsed = PlazaRoomWire.ReadCreateSession(body, TestBuilds.Of("id-1809").Schema);
        Assert.Equal("", parsed.MapKey);
        Assert.Equal("pw", parsed.Password);
        Assert.True(parsed.Unknown0);
    }

    [Fact]
    public void CreateSessionRoundTripPinsCn717AndId400Schemas()
    {
        var room = new MainMenuRoomOpened(11, "host", 300001, [300001]);
        foreach (var schema in new[] { TestBuilds.Of("cn-717").Schema, TestBuilds.Of("id-400").Schema })
        {
            var body = PlazaRoomWire.WriteCreateSessionForTest(room, schema, "pw", unknown0: true);
            var parsed = PlazaRoomWire.ReadCreateSession(body, schema);
            Assert.Equal("pw", parsed.Password);
            Assert.True(parsed.Unknown0);
        }
    }

    [Fact]
    public void SetSlotPinsCn717SeatPrefix()
    {
        var room = new MainMenuRoomOpened(11, "host", 300001, [300001]);
        var body = PlazaRoomWire.WriteSetSlot(room, TestBuilds.Packets, "cn-717", TestBuilds.Of("cn-717").Schema, slot: 0, ready: true);
        Assert.Equal(0, BitConverter.ToInt32(body, 0));
        Assert.True(body.Length > 4);
    }
}
