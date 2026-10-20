using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>The kr-812 room structures, pinned byte for byte.</summary>
public sealed class RoomCodecTests
{
    /// <summary>CN2014 slots retain native tail defaults for both occupied and vacant seats, with prior menu zeros.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cn706UserSlotKeepsNativeResetTail(bool occupied)
    {
        var schema = TestBuilds.Of("cn-706").Schema;
        var slot = occupied ? new RoomSlotValues(true, false, 42, "", 300001, [], false) : RoomSlotValues.Empty(false);
        var buffer = new ArrayBufferWriter<byte>();
        RoomCodec.WritePlayUserSlot(new PacketWriter(buffer), schema, slot);
        Assert.Equal(148, buffer.WrittenCount);
        Assert.Equal(1, BitConverter.ToInt32(buffer.WrittenSpan[96..]));
        Assert.Equal(1000, BitConverter.ToInt32(buffer.WrittenSpan[120..]));
        Assert.Equal(1, BitConverter.ToInt32(buffer.WrittenSpan[128..]));
        Assert.Equal(-1, BitConverter.ToInt32(buffer.WrittenSpan[144..]));
        foreach (var name in new[] { "unknown_160", "unknown_212", "unknown_216", "unknown_232" })
            Assert.Equal(0, schema.UserSlot.Single(field => field.Name == name).Constant);
    }

    /// <summary>Native room reset sentinels retain their raw bits without changing menu presentation.</summary>
    [Theory]
    [InlineData("kr-812", 36)]
    [InlineData("cn-717", 36)]
    [InlineData("id-400", 32)]
    [InlineData("id-1809", 36)]
    [InlineData("cn-706", 36)]
    [InlineData("cn-324", 16)]
    [InlineData("cn-279", 16)]
    public void SessionResetSentinelsPreserveAllThreeWords(string build, int tailLength)
    {
        var schema = TestBuilds.Of(build).Schema;
        var buffer = new ArrayBufferWriter<byte>();
        RoomCodec.WriteSessionSetting(new PacketWriter(buffer), schema,
            new RoomValues(RoomCodec.PlayRoomKind, []) { Play = true });
        var sentinels = buffer.WrittenSpan.Slice(buffer.WrittenCount - tailLength, 12);
        Assert.Equal(Convert.FromHexString("FFFFFFFFFFFFFFFFFFFFFFFF"), sentinels.ToArray());

        buffer = new ArrayBufferWriter<byte>();
        RoomCodec.WriteSessionSetting(new PacketWriter(buffer), schema,
            new RoomValues(RoomCodec.MainMenuKind, []));
        var menu = buffer.WrittenSpan.Slice(buffer.WrittenCount - tailLength, 12);
        Assert.Equal(build == "cn-279" ? -1 : 0, BitConverter.ToInt32(menu));
        Assert.Equal(0, BitConverter.ToInt32(menu[4..]));
        Assert.Equal(0, BitConverter.ToInt32(menu[8..]));
    }

    /// <summary>Server-created settings keep the native reset flags and sentinel while login keeps its prior defaults.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void LaterLegacyGameSettingsKeepAuditedResetDefaults(string build)
    {
        var fields = TestBuilds.All.Tables.Resolve(build, "GameSetting");
        Assert.Equal(1, fields.Single(field => field.Name == "mode").Default);
        Assert.Equal(1, fields.Single(field => field.Name == "difficulty").Default);
        Assert.Equal(1, fields.Single(field => field.Name == "unknown_4").Default);
        Assert.Equal(1, fields.Single(field => field.Name == "unknown_5").Default);
        Assert.Equal(-1, fields.Single(field => field.Name == "unknown_168").Default);
        var schema = TestBuilds.Of(build).Schema;
        foreach (var name in new[] { "unknown_4", "unknown_5", "unknown_168" })
        {
            var menu = schema.MenuGameSetting!.Single(field => field.Name == name);
            Assert.Equal(RoomFieldValue.Constant, menu.Value);
            Assert.Equal(0, menu.Constant);
        }
        var body = RoomCodec.WriteGameSettingChange(schema, new RoomValues(RoomCodec.PlayRoomKind, [])
        {
            Mode = 3,
        });
        // Empty strings leave the five option words followed by the two reset bool32 values at wire +44/+48.
        Assert.Equal(3, BitConverter.ToInt32(body));
        Assert.Equal(1, BitConverter.ToInt32(body, 44));
        Assert.Equal(1, BitConverter.ToInt32(body, 48));
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-324")]
    [InlineData("cn-279")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-706")]
    public void NativeAutoStartPreservesTheFourByteEnabledSwitch(string build)
    {
        Assert.Equal(Convert.FromHexString("01000000"), AutoStartCodec.Encode(TestBuilds.Packets, build, true));
        Assert.Equal(Convert.FromHexString("00000000"), AutoStartCodec.Encode(TestBuilds.Packets, build, false));
    }
    // part01 s2c 503 gspRoomSetSlot: u32 slot 0, then a UserSlot and an EquippedItemList.
    private static readonly byte[] RecordedSetSlot = Convert.FromHexString(
        "00000000010000000100000001000000FF1A0100000C000000540065007300740050006C0061" +
        "00790065007200300031000A0000000008000000000000000100000001000000010000000000" +
        "00000000000013000000EE8601000F8701002F870100808701009187010092870100D6870100" +
        "D7870100C0890100C1890100C2890100C3890100C4890100B68A0100E9200300EA9304003611" +
        "030010190300992803000000000000000000FFFFFFFF000000000000000000000000FFFFFFFF" +
        "FFFFFFFFFFFFFFFFFFFF000000000000000000000000FFFFFFFF000000000000000000000000" +
        "0000000001000000000000000000FA4300000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000000000000000FFFFFFFF000000001A010000EA930400" +
        "0000000013000000EE8601000F8701002F870100808701009187010092870100D6870100D787" +
        "0100C0890100C1890100C2890100C3890100C4890100B68A0100E9200300EA93040036110300" +
        "1019030099280300000000000000000000000000");

    private static readonly int[] RecordedEquipped =
    [
        100078, 100111, 100143, 100224, 100241, 100242, 100310, 100311, 100800, 100801,
        100802, 100803, 100804, 101046, 205033, 300010, 201014, 203024, 207001,
    ];

    [Fact]
    public void SlotAndEquippedListMatchTheRecordedSetSlot()
    {
        var room = new MainMenuRoomOpened(0x11A, "TestPlayer01", 300010, RecordedEquipped);
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteInt32(0);
        RoomCodec.WriteSlotAndItems(writer, room, TestBuilds.Of("kr-812").Schema);
        Assert.Equal(400, buffer.WrittenCount);
        // The character type of 300010 is the u16 at +40; the team byte at +124 is 0 on a main-menu slot.
        Assert.Equal(RecordedSetSlot, buffer.WrittenSpan.ToArray());
        var envelope = PlazaRoomWire.WriteSetSlot(room, TestBuilds.Packets, "kr-812",
            TestBuilds.Of("kr-812").Schema, slot: 0, ready: false);
        Assert.Equal(400, envelope.Length);
        Assert.Equal(RecordedSetSlot, envelope);
    }

    [Fact]
    public void MainMenuRoomCarriesKindTwoAndOnePlayer()
    {
        var body = RoomCodec.WriteMainMenu(new MainMenuRoomOpened(11, "test812", 300001, [300001, 202001]), TestBuilds.Of("kr-812").Schema);
        // SessionSetting: u32, u16, wstr "", u16 x3, bool, wstr "" and then the kind.
        Assert.Equal(RoomCodec.MainMenuKind, BitConverter.ToInt32(body, 24));
        // GameSetting written empty is 172 bytes; SessionSetting is 24 + 4 + 172 + 40.
        const int sessionSetting = 24 + 4 + 172 + 4 + 8 + 4 + 16 + 4;
        Assert.Equal(1, BitConverter.ToInt32(body, sessionSetting + 8));
        Assert.Equal(11, BitConverter.ToInt32(body, sessionSetting + 12 + 4 * 3 + 1));
    }

    [Fact]
    public void Id400RoomDropsTheKr812Fields()
    {
        var room = new MainMenuRoomOpened(11, "admin", 300001, [300001, 202001]);
        var kr = RoomCodec.WriteMainMenu(room, TestBuilds.Of("kr-812").Schema);
        var id = RoomCodec.WriteMainMenu(room, TestBuilds.Of("id-400").Schema);
        // SessionSetting: one u16 and the trailing bool fewer. GameSetting: the wstr vector
        // and the 6-field tail (u32, u32, wstr, wstr, bool, u32) fewer. UserSlot: one flag
        // fewer, and a 20-byte tail in place of kr-812's 80-byte one (wstr "" 4, bool 4,
        // bool 4, u32 4, u32 4, {u32} 4, rating 4, social 32, u32 x5 20).
        Assert.Equal(kr.Length - (2 + 4) - (4 + 4 + 4 + 4 + 4 + 4 + 4) - 4 - (80 - 20), id.Length);
        Assert.Equal(RoomCodec.MainMenuKind, BitConverter.ToInt32(id, 22));
    }

    [Fact]
    public void Id1809RoomAddsTheLongerSessionGameSettingAndUserSlotTails()
    {
        var room = new MainMenuRoomOpened(11, "admin", 300001, [300001, 202001]);
        var id400 = RoomCodec.WriteMainMenu(room, TestBuilds.Of("id-400").Schema);
        var id1809 = RoomCodec.WriteMainMenu(room, TestBuilds.Of("id-1809").Schema);
        // SessionSetting sub_4173B0: one u16 (+44) and the trailing bool (+652) more than id-400.
        // GameSetting sub_4168C0: u32, u32, wstr, wstr, bool, u32 more. UserSlot sub_417880: a
        // 44-byte tail (wstr "", bool, bool, u32 x8) in place of id-400's 20-byte one.
        Assert.Equal(id400.Length + (2 + 4) + (4 + 4 + 4 + 4 + 4 + 4) + (44 - 20), id1809.Length);
        Assert.Equal(RoomCodec.MainMenuKind, BitConverter.ToInt32(id1809, 24));
    }

    [Fact]
    public void Cn706RoomAddsTheSessionSettingBoolGameSettingTailAndLongerUserSlot()
    {
        var room = new MainMenuRoomOpened(11, "admin", 300001, [300001, 202001]);
        var id400 = RoomCodec.WriteMainMenu(room, TestBuilds.Of("id-400").Schema);
        var cn2014 = RoomCodec.WriteMainMenu(room, TestBuilds.Of("cn-706").Schema);
        // SessionSetting sub_43A410: one trailing bool (+4). GameSetting sub_439F50: u32, u32,
        // wstr, wstr more (+16 with the strings empty). UserSlot sub_61B950: three u32 after the
        // equipped vector, as id-400 (+84, +88, +108), then a 40-byte tail (wstr "", bool, bool,
        // u32, bool, u32 x5) in place of id-400's 20-byte one (+20).
        Assert.Equal(id400.Length + 4 + 16 + 20, cn2014.Length);
        Assert.Equal(RoomCodec.MainMenuKind, BitConverter.ToInt32(cn2014, 22));
    }

    [Fact]
    public void Id400MainMenuIsOccupiedButNotReadySoShopAndInventoryCanOpen()
    {
        var room = new MainMenuRoomOpened(11, "ab", 300001, [300001]);
        var buffer = new ArrayBufferWriter<byte>();
        RoomCodec.WriteSlotAndItems(new PacketWriter(buffer), room, TestBuilds.Of("id-400").Schema);
        var reader = new PacketReader(buffer.WrittenSpan);
        Assert.True(reader.ReadBool32());
        reader.ReadBytes(8 + 1 + 4);
        Assert.Equal("ab", reader.ReadWideStringPrefixed());
        reader.ReadBytes(2 + 2 + 1 + 4);
        // 0x5F1FF0's bool at object +125 is the shop-entry guard in 0x9C3A60.
        Assert.False(reader.ReadBool32());
        Assert.True(reader.ReadBool32());
        Assert.True(reader.ReadBool32());
    }

    [Fact]
    public void Cn324CharacterAndLevelOccupyDifferentNativeFields()
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(output);
        RoomCodec.WritePlaySlotAndItems(writer, TestBuilds.Of("cn-324").Schema,
            new RoomSlotValues(true, false, 42, "", 300002, [300002, 202001], false) { Level = 37, Team = 2 });
        var r = new PacketReader(output.WrittenSpan);
        r.ReadBytes(12); r.ReadByte(); r.ReadUInt32(); r.ReadWideStringPrefixed();
        Assert.Equal(2, r.ReadUInt16());
        r.ReadUInt16();
        Assert.Equal(2, r.ReadByte());
        Assert.Equal(37u, r.ReadUInt32());
        r.ReadBytes(12);
        Assert.Equal(0u, r.ReadUInt32());
    }

    [Fact]
    public void Cn324RoomFollowsTheVerifiedEmptyStringLength()
    {
        // With every string empty the cn-324 body is 322 bytes: SessionSetting 118 (with its
        // nested GameSetting), the standalone GameSetting 76, u32 x2, one UserSlot of 108
        // with its count, the empty item list and the bool.
        var body = RoomCodec.WriteMainMenu(new MainMenuRoomOpened(1, "", 300001, [300001]), TestBuilds.Of("cn-324").Schema);
        Assert.Equal(322, body.Length);
        // UserSlot: count at 118 + 76 + 8; the character type follows bool x3, u8, u32, wstr,
        // u16 x2, u8.
        Assert.Equal(1, BitConverter.ToUInt16(body, 118 + 76 + 8 + 4 + 12 + 1 + 4 + 4));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    public void Cn324SoloMenuDoesNotAdvertisePartyMembership(string nickname)
    {
        var body = RoomCodec.WriteMainMenu(
            new MainMenuRoomOpened(19, nickname, 300003, [300003]), TestBuilds.Of("cn-324").Schema);
        // cn-324 sub_58A4D0 reads the marker after 78 fixed bytes and the nickname payload.
        // cn-324 sub_5B6270 draws PartyMark only for nonnegative indices; solo slots need -1.
        var slotStart = 118 + 76 + 8 + 4;
        Assert.Equal(-1, BitConverter.ToInt32(body, slotStart + 78 + nickname.Length * 2));
        Assert.Equal(322 + nickname.Length * 2, body.Length);
    }

    [Fact]
    public void Cn279RoomPinsTheNativeSlotAndCompactEquipmentBoundaries()
    {
        var body = RoomCodec.WriteMainMenu(new MainMenuRoomOpened(19, "ab", 300003, [300003, 201001]), TestBuilds.Of("cn-279").Schema);
        // cn-279 sub_4496E0 reads six u16 through cn-279 sub_4EBFF0, not u32.
        // Following the native reads keeps the equipment count at the next byte boundary.
        Assert.Equal(328, body.Length);
        Assert.Equal(1, BitConverter.ToInt32(body, 194));
        // cn-279 sub_44FD50 delegates its slot list to cn-279 sub_453640; this two-character
        // slot consumes 102 bytes before the equipment list count.
        var r = new PacketReader(body.AsSpan(198));
        Assert.True(r.ReadBool32());
        Assert.False(r.ReadBool32());
        Assert.False(r.ReadBool32());
        Assert.Equal(0xFF, r.ReadByte());
        Assert.Equal(19u, r.ReadUInt32());
        Assert.Equal("ab", r.ReadWideStringPrefixed());
        Assert.Equal(0, r.ReadUInt16());
        Assert.Equal(0, r.ReadUInt16());
        Assert.Equal(0, r.ReadByte());
        Assert.Equal(3, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(-1, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(-1, r.ReadInt32());
        Assert.Equal(65535, r.ReadUInt16());
        Assert.Equal(65535, r.ReadUInt16());
        Assert.Equal(65535, r.ReadUInt16());
        Assert.Equal(65535, r.ReadUInt16());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        // cn-279 sub_448840 reads who, character and items. GotoRoom's trailing bool32
        // follows the sole equipment entry (cn-279 sub_44FD50).
        Assert.Equal(1, r.ReadInt32());
        Assert.Equal(19, r.ReadInt32());
        Assert.Equal(300003, r.ReadInt32());
        Assert.Equal(2, r.ReadInt32());
        Assert.Equal(300003, r.ReadInt32());
        Assert.Equal(201001, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void Cn279SlotKeepsTheScalarBeforeTheCountedVector()
    {
        // cn-279 sub_4496E0 reads +32 and +36 before the +40 vector. Nonzero values
        // expose a swapped scalar/count even when the normal menu sends only zeroes.
        var schema = TestBuilds.Of("cn-279").Schema;
        schema = schema with
        {
            UserSlot = schema.UserSlot.Select(field => field.Name switch
            {
                "level" => field with { Value = RoomFieldValue.Constant, Constant = 7 },
                "unknown_36" => field with { Constant = 0x11223344 },
                _ => field,
            }).ToArray(),
        };
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        RoomCodec.WriteUserSlot(new PacketWriter(buffer),
            new MainMenuRoomOpened(19, "", 300003, [300003]), schema, ready: false);
        var r = new PacketReader(buffer.WrittenSpan[42..]);
        Assert.Equal(7, r.ReadInt32());
        Assert.Equal(0x11223344, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(98, buffer.WrittenCount);
    }

    [Fact]
    public void Cn279SeatPushContainsUserSlotAndItsEquipment()
    {
        // cn-279 sub_4499F0 constructs a UserSlot at +8 and EquippedItemList at +136;
        // cn-279 sub_4499B0 reads the index, then those objects through their vtables.
        var body = RoomCodec.WritePlaySlot(
            new MainMenuRoomOpened(19, "ab", 300003, [300003, 201001]),
            TestBuilds.Packets, "cn-279", TestBuilds.Of("cn-279").Schema, 0);
        Assert.Equal(126, body.Length);
        var r = new PacketReader(body);
        Assert.Equal(0, r.ReadInt32());
        Assert.True(r.ReadBool32());
        Assert.False(r.ReadBool32());
        Assert.True(r.ReadBool32());
        Assert.Equal(0, r.ReadByte());
        Assert.Equal(19, r.ReadInt32());
        Assert.Equal("ab", r.ReadWideStringPrefixed());
        r = new PacketReader(body.AsSpan(106));
        Assert.Equal(19, r.ReadInt32());
        Assert.Equal(300003, r.ReadInt32());
        Assert.Equal(2, r.ReadInt32());
        Assert.Equal(300003, r.ReadInt32());
        Assert.Equal(201001, r.ReadInt32());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void Cn279OldTutorialKeepsTheNativeMapKeyInBothSettings()
    {
        // cn-279 sub_401730 means NOT equal. cn-279 sub_7091E0 requires room+68
        // != "Random" before sub_706750 tests isTutorial and selects stage 6.
        var room = new MainMenuRoomOpened(19, "ab", 300003, [300003]);
        var schema = TestBuilds.Of("cn-279").Schema;
        var body = RoomCodec.WriteRoom(room, schema, RoomCodec.PlayRoomKind,
            mode: 1, mapName: "rel_Tutorial_01");
        var r = new PacketReader(body);
        r.ReadBytes(22);
        Assert.Equal(RoomCodec.PlayRoomKind, r.ReadInt32());
        Assert.Equal(1, r.ReadInt32());
        r.ReadInt32();
        Assert.Equal("", r.ReadWideStringPrefixed());
        Assert.Equal("", r.ReadWideStringPrefixed());
        Assert.Equal("", r.ReadWideStringPrefixed());
        Assert.Equal("rel_Tutorial_01", r.ReadWideStringPrefixed());
        // Native GameSetting tail: five scalars, two bool32, five scalars;
        // native SessionSetting tail: one scalar, f64, empty wstr.
        r.ReadBytes(48 + 16);
        Assert.Equal(1, r.ReadInt32());
        r.ReadInt32();
        Assert.Equal("", r.ReadWideStringPrefixed());
        Assert.Equal("", r.ReadWideStringPrefixed());
        Assert.Equal("", r.ReadWideStringPrefixed());
        Assert.Equal("rel_Tutorial_01", r.ReadWideStringPrefixed());
    }

    /// <summary>Play rooms carry the chosen mode rather than the early constructor's constant one.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(101)]
    public void Cn279PlayRoomRetainsItsSelectedMode(int mode)
    {
        var schema = TestBuilds.Of("cn-279").Schema;
        var player = new MainMenuRoomOpened(19, "ab", 300003, [300003]);
        var body = RoomCodec.WriteRoom(player, schema, RoomCodec.PlayRoomKind, mode: mode);
        Assert.Equal(mode, BitConverter.ToInt32(body, 26));
        var menu = RoomCodec.WriteMainMenu(player, schema);
        Assert.Equal(1, BitConverter.ToInt32(menu, 26));
    }

    [Fact]
    public void Cn717RoomIsKr812WithoutTheGameSettingVectorsAndArenaRating()
    {
        var room = new MainMenuRoomOpened(11, "admin", 300001, [300001, 202001]);
        var kr = RoomCodec.WriteMainMenu(room, TestBuilds.Of("kr-812").Schema);
        var cn = RoomCodec.WriteMainMenu(room, TestBuilds.Of("cn-717").Schema);
        // GameSetting: wstr "" and three empty vectors (16 bytes). UserSlot: ArenaRating, one
        // f32 (4 bytes).
        Assert.Equal(kr.Length - 16 - 4, cn.Length);
    }

    [Fact]
    public void MainMenuGotoRoomKeepsGameSettingModeZeroAndEmptyMapKey()
    {
        // SessionSetting header through kind, then GameSetting first u32 is mode (kr-812 /
        // cn-717 sub_4188F0 / sub_41A510 at +8). Login must not seed tutorial mode 103.
        var room = new MainMenuRoomOpened(11, "admin", 300001, [300001]);
        var body = RoomCodec.WriteMainMenu(room, TestBuilds.Of("cn-717").Schema);
        Assert.Equal(RoomCodec.MainMenuKind, BitConverter.ToInt32(body, 24));
        Assert.Equal(0, BitConverter.ToInt32(body, 28));
    }

    [Fact]
    public void TutorialPlayGotoRoomWritesModeHordeTutorAndMapStrings()
    {
        var room = new MainMenuRoomOpened(11, "admin", 300001, [300001]);
        const string mapKey = "rel_Village07_Easy02";
        var body = RoomCodec.WriteRoom(
            room, TestBuilds.Of("cn-717").Schema, RoomCodec.PlayRoomKind, mode: 103, mapName: mapKey,
            mapDirectory: mapKey, currentMap: mapKey);
        Assert.Equal(RoomCodec.PlayRoomKind, BitConverter.ToInt32(body, 24));
        Assert.Equal(103, BitConverter.ToInt32(body, 28));
        // After mode u32, unknown u32, Directory, CurrentMap, empty third, then map-key wstr.
        var dirOffset = 28 + 4 + 4;
        Assert.Equal(mapKey.Length, BitConverter.ToInt32(body, dirOffset));
        var mapOffset = dirOffset + 4 + mapKey.Length * 2 + 4 + mapKey.Length * 2 + 4;
        Assert.Equal(mapKey.Length, BitConverter.ToInt32(body, mapOffset));
        var chars = System.Text.Encoding.Unicode.GetString(body, mapOffset + 4, mapKey.Length * 2);
        Assert.Equal(mapKey, chars);
    }

    [Fact]
    public void Cn717UserSlotSeatIndexIsZeroNotFf()
    {
        // Seat byte is the fourth field of UserSlot (three bool32 then u8). Find the sole
        // UserSlot after SessionSetting + two u32 + count.
        var room = new MainMenuRoomOpened(11, "admin", 300001, [300001]);
        var body = RoomCodec.WriteRoom(
            room, TestBuilds.Of("cn-717").Schema, RoomCodec.PlayRoomKind, mode: 103, mapName: "rel_prologue_01",
            mapDirectory: "rel_prologue_01", currentMap: "rel_prologue_01");
        // Kind at 24, then GameSetting…; walk to UserSlots by scanning for count 1 followed by
        // three true bool32 and seat u8 0 (not 0xFF).
        var found = false;
        for (var i = 0; i + 16 < body.Length; i++)
        {
            if (BitConverter.ToInt32(body, i) != 1)
                continue;
            if (BitConverter.ToInt32(body, i + 4) != 1)
                continue;
            if (BitConverter.ToInt32(body, i + 8) != 1)
                continue;
            if (BitConverter.ToInt32(body, i + 12) != 1)
                continue;
            Assert.Equal(0, body[i + 16]);
            found = true;
            break;
        }
        Assert.True(found);
    }

    [Fact]
    public void Cn735RoomPinsSessionHeaderKindAndEmptyBodyLength()
    {
        var room = new MainMenuRoomOpened(11, "admin", 300001, [300001, 202001]);
        var body = RoomCodec.WriteMainMenu(room, TestBuilds.Of("cn-735").Schema);
        // SessionSetting sub_4254D0: u16, u32, wstr "", u16 x3, bool, wstr "", then kind.
        Assert.Equal(RoomCodec.MainMenuKind, BitConverter.ToInt32(body, 24));
        // Empty GameSetting sub_424D40 is 173 bytes (the mid-CN u8 is the odd byte). With
        // nickname "admin" and two equipped ids the full GotoRoom body is 503 bytes.
        Assert.Equal(503, body.Length);
        Assert.Equal(4, RoomCodec.WriteSceneTransition(TestBuilds.Packets, "cn-735").Length);
    }

    [Theory]
    [InlineData("cn-735", "cn-717", "kr-812")]
    [InlineData("cn-738", "cn-735", "cn-717", "kr-812")]
    [InlineData("cn-742", "cn-738", "cn-735", "cn-717", "kr-812")]
    public void MidCnRoomBuildsResolveTheCn735Layout(params string[] lineage)
    {
        var choice = TestBuilds.All.Schemas.Require(lineage);
        Assert.Equal("cn-735", choice.Profile);
        Assert.Equal(lineage[0] == "cn-735" ? VariantMatch.Exact : VariantMatch.Inherited, choice.Match);
        var room = new MainMenuRoomOpened(11, "admin", 300001, [300001, 202001]);
        Assert.Equal(RoomCodec.WriteMainMenu(room, TestBuilds.Of("cn-735").Schema),
            RoomCodec.WriteMainMenu(room, choice.Value));
    }
}
