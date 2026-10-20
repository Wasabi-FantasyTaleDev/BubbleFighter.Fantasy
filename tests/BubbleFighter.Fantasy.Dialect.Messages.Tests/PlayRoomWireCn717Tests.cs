using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// Byte-length and role-offset pins for the cn-717 play-room packets. The lengths are derived from
/// the client readers (<c>sub_41AD80</c> SessionSetting, <c>sub_41B2C0</c> UserSlot, <c>sub_569E70</c>
/// EquippedItemList, <c>sub_6EDE80</c> gspGotoRoom).
/// </summary>
public sealed class PlayRoomWireCn717Tests
{
    private static readonly RoomSchema Schema = TestBuilds.Of("cn-717").Schema;

    // Twelve-character nickname and nineteen worn items; seated, ready, team 1, start position 3, level 77.
    private static readonly RoomSlotValues Player = new(true, false, 282, "TwelveLetter", 300010,
        [.. Enumerable.Range(100000, 19)], true)
    {
        Team = 1, SpawnIndex = 3, Level = 77,
    };

    // UserSlot wire size (sub_41B2C0): 12 bool32 (48) + 2 u8 (2) + 7 u16 (14) + 20 u32 including the
    // +220 member (80) + SocialSystemInfo 8 u32 (32) + two wstr counts (8) + the equipped count (4) = 188,
    // plus two bytes per nickname character and four per worn item.
    private const int SlotFixed = 188;

    // EquippedItemList wire size (sub_569E70): 3 u32 (12) + four vector counts (16) + 4 per worn item.
    private const int ItemListFixed = 28;

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
    public void RoomSnapshotWithOnePlayerAndElevenEmptySlotsHasTheDerivedLength()
    {
        var slots = Enumerable.Range(0, 12).Select(i => i == 0 ? Player : RoomSlotValues.Empty(i >= 4)).ToArray();
        var body = RoomCodec.WriteRoom(Schema, new RoomValues(RoomCodec.PlayRoomKind, slots)
        {
            Play = true, RoomId = 400, Title = "ElevenChars", SessionState = 2, OpenSeats = 4, GameSetting = new byte[304],
            MasterSlot = 2, LocalSlot = 3,
        });

        // SessionSetting (sub_41AD80): u32, u16, title wstr, 3 u16, bool32, wstr, u32 kind, GameSetting,
        // u32, f64, wstr, 4 u32, bool32 = 64 fixed bytes + 22 title bytes + the 304-byte GameSetting = 390.
        const int sessionSetting = 64 + 22 + 304;
        // Slots: the player (288) and eleven empty ones (188 each); the vector counts add 4 each.
        const int userSlots = 4 + (SlotFixed + 24 + 19 * 4) + 11 * SlotFixed;
        // Item lists: the player's (28 + 76) and eleven empty ones.
        const int itemLists = 4 + (ItemListFixed + 19 * 4) + 11 * ItemListFixed;
        // gspGotoRoom (sub_6EDE80): SessionSetting, master u32, local u32, both vectors, bool32, u32.
        Assert.Equal(sessionSetting + 8 + userSlots + itemLists + 8, body.Length);
        Assert.Equal(3182, body.Length);

        Assert.Equal(2, BitConverter.ToInt32(body, 0));
        Assert.Equal(400, BitConverter.ToUInt16(body, 4));
        Assert.Equal(11, BitConverter.ToInt32(body, 6));
        Assert.Equal("ElevenChars", System.Text.Encoding.Unicode.GetString(body, 10, 22));
        // After the title: seated players, an unnamed u16, max seats.
        Assert.Equal([1, 0, 4], new[] { 32, 34, 36 }.Select(offset => (int)BitConverter.ToUInt16(body, offset)));
        Assert.Equal(RoomCodec.PlayRoomKind, BitConverter.ToInt32(body, 46));
        // Master and local slot follow the SessionSetting (room +600 and +604).
        Assert.Equal([2, 3, 12], new[] { sessionSetting, sessionSetting + 4, sessionSetting + 8 }
            .Select(offset => BitConverter.ToInt32(body, offset)));

        // First UserSlot: bool32 x3, u8 start position, u32 user number, wstr, u16, u16, u8 team, u32, ready.
        var slot0 = sessionSetting + 12;
        Assert.Equal([1, 0, 1], new[] { 0, 4, 8 }.Select(o => BitConverter.ToInt32(body, slot0 + o)));
        Assert.Equal(3, body[slot0 + 12]);
        Assert.Equal(282, BitConverter.ToInt32(body, slot0 + 13));
        Assert.Equal((10, 0), (BitConverter.ToUInt16(body, slot0 + 45), BitConverter.ToUInt16(body, slot0 + 47)));
        Assert.Equal(1, body[slot0 + 49]);
        Assert.Equal(77, BitConverter.ToInt32(body, slot0 + 50));
        Assert.Equal(1, BitConverter.ToInt32(body, slot0 + 54));
    }

    [Fact]
    public void SlotPacketsHaveTheDerivedLengths()
    {
        // gspRoomSetSlot for the seated player: index u32 + UserSlot + EquippedItemList.
        var seated = Slot(0, Player, items: true);
        Assert.Equal(4 + (SlotFixed + 24 + 19 * 4) + (ItemListFixed + 19 * 4), seated.Length);
        Assert.Equal(396, seated.Length);
        // The slot sits after the index: role offsets within the packet.
        Assert.Equal(0, BitConverter.ToInt32(seated, 0));
        Assert.Equal([1, 0, 1], new[] { 4, 8, 12 }.Select(offset => BitConverter.ToInt32(seated, offset)));
        Assert.Equal(3, seated[16]);
        Assert.Equal(282, BitConverter.ToInt32(seated, 17));
        Assert.Equal(10, BitConverter.ToUInt16(seated, 49));
        Assert.Equal(1, seated[53]);
        Assert.Equal(1, BitConverter.ToInt32(seated, 58));

        // A closed empty slot: no nickname, no items, empty item list.
        var closed = Slot(3, RoomSlotValues.Empty(closed: true), items: true);
        Assert.Equal(4 + SlotFixed + ItemListFixed, closed.Length);
        Assert.Equal(220, closed.Length);
        Assert.Equal([0, 1, 0], new[] { 4, 8, 12 }.Select(offset => BitConverter.ToInt32(closed, offset)));
        Assert.Equal(0xFF, closed[16]);

        // A leaver's record: the seated constants without the occupied marks or the equipment.
        var left = Slot(0, Player with { Occupied = false, Departed = true, EquippedItemIds = [], Ready = false }, items: false);
        Assert.Equal(4 + SlotFixed + 24, left.Length);
        Assert.Equal(216, left.Length);
        Assert.Equal([0, 0, 0], new[] { 4, 8, 12 }.Select(offset => BitConverter.ToInt32(left, offset)));
        Assert.Equal(282, BitConverter.ToInt32(left, 17));
    }
}
