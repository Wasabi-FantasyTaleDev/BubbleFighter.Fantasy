using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Modern.Messages.Play;

/// <summary>Decodes audited modern waiting-room requests without registering a wire route.</summary>
internal static class ModernRoomRequests
{
    // kr-1256 sub_962200 writes 0x3A plus one word or 0xBA. The established create path also accepts
    // a body ending after the password; only that final tag may be supplied, never a missing field.
    private const byte CreateAbsentTag = 0xBA;

    internal static Dictionary<string, object?> ReadCreate(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        Dictionary<string, object?> fields;
        try
        {
            fields = PacketCodec.Read(tables, build, "RoomCreateRequest", body);
        }
        catch (PacketFormatException ex) when (ex.Offset == body.Length && body.Length < ushort.MaxValue)
        {
            var last = tables.Resolve(build, "RoomCreateRequest")[^1];
            if (last.Name != "unknown_n2" || last.Type != "optional<0x3A,RoomCreateTail>")
                throw;
            var completed = new byte[body.Length + 1];
            body.CopyTo(completed);
            completed[^1] = CreateAbsentTag;
            fields = PacketCodec.Read(tables, build, "RoomCreateRequest", completed);
            if (fields["unknown_n2"] is not byte marker || marker != CreateAbsentTag)
                throw new PacketFormatException("RoomCreateRequest is missing more than its final absent tag.", body.Length);
        }
        if (fields["unknown_n2"] is not Dictionary<string, object?> && fields["unknown_n2"] is not CreateAbsentTag)
            throw new PacketFormatException("RoomCreateRequest tag must be 0x3A or 0xBA.", body.Length - 1);
        return fields;
    }

    internal static RoomJoinRequested ReadJoin(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        // kr-1256 sub_854C90 returns the clicked row's room id and the password control's text.
        var fields = PacketCodec.ReadScalars(tables, build, "RoomJoinRequest", body);
        return new RoomJoinRequested((int)fields["room_id"], (string)fields["password"]);
    }

    internal static RoomReadyRequested ReadReady(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        // kr-1256 sub_9785E0 writes ready, shop and inventory booleans from mutually exclusive UI states.
        var fields = PacketCodec.ReadScalars(tables, build, "RoomReadyRequest", body, requireCanonicalBooleans: true);
        var ready = (bool)fields["ready"];
        var shop = (bool)fields["unknown_5"];
        var inventory = (bool)fields["unknown_6"];
        if ((ready ? 1 : 0) + (shop ? 1 : 0) + (inventory ? 1 : 0) > 1)
            throw new PacketFormatException("RoomReadyRequest requires mutually exclusive ready, shop and inventory flags.", 0);
        return new RoomReadyRequested(ready) { InShop = shop, InInventory = inventory };
    }

    internal static RoomChatSent ReadChat(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        // kr-1256 sub_4E9070 writes both strings; the unproven second string is consumed but not interpreted.
        var fields = PacketCodec.ReadScalars(tables, build, "RoomChatRequest", body);
        return new RoomChatSent((int)fields["channel"], (string)fields["text"]);
    }

    internal static RoomSeatLockRequested ReadSeatLock(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        // kr-1256 sub_980300 sends a slot word and a canonical closed flag from the empty-seat control.
        var fields = PacketCodec.ReadScalars(tables, build, "RoomSeatLock", body, requireCanonicalBooleans: true);
        return new RoomSeatLockRequested((int)fields["slot"], (bool)fields["closed"]);
    }

    internal static Dictionary<string, object> ReadStart(PacketTables tables, string build, ReadOnlySpan<byte> body) =>
        // kr-1256 sub_9D7570 writes the strings, rule record and trailing word even on a seated start click.
        PacketCodec.ReadScalars(tables, build, "RoomEnterGameRequest", body);

    internal static RoomTeamChangeRequested ReadTeamChange(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var fields = PacketCodec.Read(tables, build, "RoomTeamChangeRequest", body);
        foreach (var value in fields.Values)
            if (value is not Dictionary<string, object?> && value is not (byte)0xBA)
                throw new PacketFormatException("RoomTeamChangeRequest tag must be 0x3A or 0xBA.", 0);
        // kr-1256 sub_969730 -> sub_971140 -> sub_4B3AC0 clears both blocks for an ordinary team click.
        // A populated qualifier is structurally readable but does not prove another actor may be changed.
        if (fields.Values.Any(value => value is Dictionary<string, object?>))
            throw new NotSupportedException("Qualified room team changes have no audited typed operation.");
        return new RoomTeamChangeRequested();
    }
}
