using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>Decodes audited modern waiting-room requests without registering a wire route.</summary>
internal static class RoomRequests
{
    // kr-1256 sub_962200 writes 0x3A plus one word or 0xBA. The established create path also accepts
    // a body ending after the password; only that final tag may be supplied, never a missing field.
    private const byte CreateAbsentTag = 0xBA;

    internal static Dictionary<string, object?> ReadCreate(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        RequireCreateLayout(tables, build);
        var tagged = tables.Resolve(build, "RoomCreateRequest").Any(field => field.Name == "unknown_n2");
        Dictionary<string, object?> fields;
        try
        {
            fields = PacketCodec.Read(tables, build, "RoomCreateRequest", body);
        }
        catch (PacketFormatException ex) when (tagged && ex.Offset == body.Length && body.Length < ushort.MaxValue)
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
        if (tagged && fields["unknown_n2"] is not Dictionary<string, object?> && fields["unknown_n2"] is not CreateAbsentTag)
            throw new PacketFormatException("RoomCreateRequest tag must be 0x3A or 0xBA.", body.Length - 1);
        return fields;
    }

    private static void RequireCreateLayout(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireFields(tables, build, "RoomCreateRequest", "Room requests",
            ("session_setting", "SessionSetting"), ("password", "wstr"));
        // A native creator without a tagged suffix must consume exactly its session and password.
        var fields = tables.Resolve(build, "RoomCreateRequest");
        if (fields.Count == 2)
            return;
        PacketLayoutContract.RequireFields(tables, build, "RoomCreateRequest", "Room requests",
            ("unknown_n2", "optional<0x3A,RoomCreateTail>"));
        if (fields.Count != 3 || fields[^1].Name != "unknown_n2")
            throw new BubbleFighter.Fantasy.Abstractions.ConfigException("RoomCreateRequest has an unaudited suffix.");
    }

    internal static RoomJoinRequested ReadJoin(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        PacketLayoutContract.RequireFields(tables, build, "RoomJoinRequest", "Room requests",
            ("room_id", "u32"), ("password", "wstr"));
        // kr-1256 sub_854C90 returns the clicked row's room id and the password control's text.
        var fields = PacketCodec.ReadScalars(tables, build, "RoomJoinRequest", body);
        return new RoomJoinRequested((int)fields["room_id"], (string)fields["password"]);
    }

    internal static RoomReadyRequested ReadReady(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        PacketLayoutContract.RequireFields(tables, build, "RoomReadyRequest", "Room requests",
            ("ready", "bool"), ("unknown_5", "bool"), ("unknown_6", "bool"));
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
        PacketLayoutContract.RequireFields(tables, build, "RoomChatRequest", "Room requests",
            ("channel", "u32"), ("text", "wstr"));
        // kr-1256 sub_4E9070 writes both strings; the unproven second string is consumed but not interpreted.
        var fields = PacketCodec.ReadScalars(tables, build, "RoomChatRequest", body);
        return new RoomChatSent((int)fields["channel"], (string)fields["text"]);
    }

    internal static RoomSeatLockRequested ReadSeatLock(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        PacketLayoutContract.RequireFields(tables, build, "RoomSeatLock", "Room requests",
            ("slot", "u32"), ("closed", "bool"));
        // kr-1256 sub_980300 sends a slot word and a canonical closed flag from the empty-seat control.
        var fields = PacketCodec.ReadScalars(tables, build, "RoomSeatLock", body, requireCanonicalBooleans: true);
        return new RoomSeatLockRequested((int)fields["slot"], (bool)fields["closed"]);
    }

    internal static Dictionary<string, object> ReadStart(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        PacketLayoutContract.RequireFields(tables, build, "RoomEnterGameRequest", "Room requests",
            ("map_key", "wstr"), ("unknown_36", "wstr"), ("unknown_64", "wstr"),
            ("death_match_time_limit_ms", "u32"), ("time_attack_time_limit_ms", "u32"));
        // kr-1256 sub_9D7570 writes the strings, rule record and trailing word even on a seated start click.
        return PacketCodec.ReadScalars(tables, build, "RoomEnterGameRequest", body);
    }

    internal static RoomTeamChangeRequested ReadTeamChange(PacketTables tables, string build, ReadOnlySpan<byte> body)
    {
        var plain = RequireTeamChangeLayout(tables, build);
        var fields = PacketCodec.Read(tables, build, "RoomTeamChangeRequest", body);
        if (plain)
        {
            // cn-774 sub_B739A0 / tw-454 sub_7DA940 qualify no other actor for an ordinary local toggle.
            if (Convert.ToInt32(fields["unknown_0"], System.Globalization.CultureInfo.InvariantCulture) != -1
                || Convert.ToInt32(fields["unknown_4"], System.Globalization.CultureInfo.InvariantCulture) != 0
                || Convert.ToInt32(fields["unknown_8"], System.Globalization.CultureInfo.InvariantCulture) != 0)
                throw new NotSupportedException("Qualified room team changes have no audited typed operation.");
            return new RoomTeamChangeRequested();
        }
        foreach (var value in fields.Values)
            if (value is not Dictionary<string, object?> && value is not (byte)0xBA)
                throw new PacketFormatException("RoomTeamChangeRequest tag must be 0x3A or 0xBA.", 0);
        // kr-1256 sub_969730 -> sub_971140 -> sub_4B3AC0 clears both blocks for an ordinary team click.
        // A populated qualifier is structurally readable but does not prove another actor may be changed.
        if (fields.Values.Any(value => value is Dictionary<string, object?>))
            throw new NotSupportedException("Qualified room team changes have no audited typed operation.");
        return new RoomTeamChangeRequested();
    }

    private static bool RequireTeamChangeLayout(PacketTables tables, string build)
    {
        var fields = tables.Resolve(build, "RoomTeamChangeRequest");
        if (fields.Count == 3 && fields.All(field => field.Type == "u32"))
        {
            PacketLayoutContract.RequireFields(tables, build, "RoomTeamChangeRequest", "Room requests",
                ("unknown_0", "u32"), ("unknown_4", "u32"), ("unknown_8", "u32"));
            return true;
        }
        PacketLayoutContract.RequireFields(tables, build, "RoomTeamChangeRequest", "Room requests",
            ("unknown_n0", "optional<0x3A,RoomTeamChangeFirstTail>"), ("unknown_n1", "optional<0x3A,TeamChangeTail>"));
        if (fields.Count != 2)
            throw new BubbleFighter.Fantasy.Abstractions.ConfigException("RoomTeamChangeRequest has an unaudited suffix.");
        return false;
    }

    /// <summary>
    /// Checks every structure the request readers cast, for the bindings' mount steps: the join request's room id
    /// and password (kr-1256 sub_854C90 writes both), the ready request's three canonical flags (kr-1256
    /// sub_9785E0), the chat channel and text, the seat lock's slot and closed word, the start request's map
    /// strings and time limits (kr-1256 sub_9D7570), the create request's session, password and final tag
    /// (kr-1256 sub_962200), and the team change's two tagged blocks. Each reader repeats its own structure's
    /// check so a caller with a single-structure table still fails loudly at the field it needs.
    /// </summary>
    /// <param name="tables">The shared native packet layouts.</param>
    /// <param name="build">The audited client build.</param>
    internal static void ValidateLayouts(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireFields(tables, build, "RoomJoinRequest", "Room requests",
            ("room_id", "u32"), ("password", "wstr"));
        PacketLayoutContract.RequireFields(tables, build, "RoomReadyRequest", "Room requests",
            ("ready", "bool"), ("unknown_5", "bool"), ("unknown_6", "bool"));
        PacketLayoutContract.RequireFields(tables, build, "RoomChatRequest", "Room requests",
            ("channel", "u32"), ("text", "wstr"));
        PacketLayoutContract.RequireFields(tables, build, "RoomSeatLock", "Room requests",
            ("slot", "u32"), ("closed", "bool"));
        PacketLayoutContract.RequireFields(tables, build, "RoomEnterGameRequest", "Room requests",
            ("map_key", "wstr"), ("unknown_36", "wstr"), ("unknown_64", "wstr"),
            ("death_match_time_limit_ms", "u32"), ("time_attack_time_limit_ms", "u32"));
        RequireCreateLayout(tables, build);
        _ = RequireTeamChangeLayout(tables, build);
    }
}
