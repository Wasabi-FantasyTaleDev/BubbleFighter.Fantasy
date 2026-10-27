using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Preserves the independent cn-735 scalar writer as a migration byte oracle; never used by plugins.</summary>
internal static class GotoRoomGotoMenuGoldenCodec
{
    /// <summary>Character item id base; type index is id minus this (cn-735 <c>UserSlot</c>).</summary>
    private const int CharacterItemBase = 300000;

    /// <summary>
    /// Main-menu session kind. cn-735 handler <c>sub_A23390</c> case 1 calls
    /// <c>SetStage(2)</c> only when the client is already past stage 2; right after
    /// login the session is installed and <c>gspGotoMenu</c> performs the move.
    /// </summary>
    public const int MainMenuKind = 1;

    /// <summary>
    /// Writes <c>gspGotoMenu</c>: one <c>bool32 notice</c> (cn-735 reader in
    /// <c>sub_A23050</c>). False is four zero bytes; the handler then
    /// <c>SetStage(2)</c> (<c>MainMenuStage</c>) when the post-login flag is set.
    /// </summary>
    /// <returns>The S2C body.</returns>
    public static byte[] WriteGotoMenu() => new byte[4];

    /// <summary>Retains the cn-735 sub_A4D6F0 envelope and nested readers as the independent byte oracle.</summary>
    /// <param name="room">The main-menu player data.</param>
    /// <returns>The previous positional menu payload.</returns>
    public static byte[] WriteMainMenu(MainMenuRoomOpened room)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        var w = new PacketWriter(buffer);
        WriteSessionSetting(w);
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteInt32(1);
        WriteUserSlot(w, room);
        w.WriteInt32(1);
        WriteEquippedItemList(w, room);
        w.WriteBool32(false);
        w.WriteInt32(0);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteSessionSetting(PacketWriter w)
    {
        w.WriteUInt16(0);
        w.WriteInt32(0);
        w.WriteWideStringPrefixed("");
        w.WriteUInt16(0);
        w.WriteUInt16(0);
        w.WriteUInt16(0);
        w.WriteBool32(false);
        w.WriteWideStringPrefixed("");
        w.WriteInt32(MainMenuKind);
        WriteGameSetting(w);
        w.WriteInt32(0);
        w.WriteDouble(0);
        w.WriteWideStringPrefixed("");
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteBool32(false);
    }

    private static void WriteGameSetting(PacketWriter w)
    {
        // cn-735 sub_424D40 / cn-735 sub_424B10: three u32, three bools, five u32, four wstr,
        // u32, six wstr, two u32, then cn-735 sub_406590 (bool + empty u32-pair vector +
        // bool + empty u32/wstr vector), five bools, two bools, u8, bool, empty
        // wstr vector, three u32, two wstr, u32. Mode and map key stay zero /
        // empty on the main-menu path.
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteBool32(false);
        w.WriteBool32(false);
        w.WriteBool32(false);
        for (var i = 0; i < 5; i++)
            w.WriteInt32(0);
        for (var i = 0; i < 4; i++)
            w.WriteWideStringPrefixed("");
        w.WriteInt32(0);
        for (var i = 0; i < 6; i++)
            w.WriteWideStringPrefixed("");
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteBool32(false);
        w.WriteInt32(0);
        w.WriteBool32(false);
        w.WriteInt32(0);
        for (var i = 0; i < 5; i++)
            w.WriteBool32(false);
        w.WriteBool32(false);
        w.WriteBool32(false);
        w.WriteByte(0);
        w.WriteBool32(false);
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteWideStringPrefixed("");
        w.WriteWideStringPrefixed("");
        w.WriteInt32(0);
    }

    private static void WriteUserSlot(PacketWriter w, MainMenuRoomOpened room)
    {
        var characterType = room.CharacterItemId - CharacterItemBase;
        w.WriteBool32(true);
        w.WriteBool32(true);
        w.WriteBool32(true);
        w.WriteByte(0xFF);
        w.WriteInt32(room.UserNumber);
        w.WriteWideStringPrefixed(room.Nickname);
        w.WriteUInt16(10);
        w.WriteUInt16(0);
        w.WriteByte(checked((byte)characterType));
        w.WriteInt32(8);
        w.WriteBool32(false);
        w.WriteBool32(true);
        w.WriteBool32(true);
        w.WriteBool32(true);
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteInt32(room.EquippedItemIds.Count);
        foreach (var id in room.EquippedItemIds)
            w.WriteInt32(id);
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteInt32(-1);
        w.WriteBool32(false);
        w.WriteInt32(0);
        w.WriteBool32(false);
        w.WriteInt32(-1);
        for (var i = 0; i < 5; i++)
            w.WriteUInt16(0xFFFF);
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteBool32(false);
        w.WriteInt32(-1);
        w.WriteWideStringPrefixed("");
        w.WriteBool32(false);
        w.WriteBool32(false);
        w.WriteInt32(0);
        w.WriteInt32(1);
        // Plain u32 at +220 (vtable off_10B35AC deserialize cn-735 sub_4013A0), then
        // SocialSystemInfo cn-735 sub_426E90 (u32 x7, bool, f64), then three trailing u32.
        w.WriteInt32(0);
        for (var i = 0; i < 7; i++)
            w.WriteInt32(0);
        w.WriteBool32(false);
        w.WriteDouble(0);
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteInt32(0);
    }

    private static void WriteEquippedItemList(PacketWriter w, MainMenuRoomOpened room)
    {
        w.WriteInt32(room.UserNumber);
        w.WriteInt32(room.CharacterItemId);
        w.WriteInt32(0);
        w.WriteInt32(room.EquippedItemIds.Count);
        foreach (var id in room.EquippedItemIds)
            w.WriteInt32(id);
        w.WriteInt32(0);
        w.WriteInt32(0);
        w.WriteInt32(0);
    }
}
