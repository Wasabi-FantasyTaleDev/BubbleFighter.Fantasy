using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Writes the solo idle <c>gspUpdateParty</c> body of the modern login and main-menu sequence. The packet
/// tables choose the build's shape for the same values: the kr-1256 keyed profile map (handler
/// <c>kr-1256 sub_9064B0</c> -&gt; body parser <c>kr-1256 sub_905D20</c>, member
/// <c>kr-1256 sub_1141670</c>), or the unkeyed equipped-record vector cn-774 and tw-454 read in the same
/// slot (<c>cn-774 sub_B009A0</c> / <c>cn-774 sub_105A3C0</c>; <c>tw-454 sub_4A0550</c>).
/// </summary>
internal static class LoginPartySnapshot
{
    /// <summary>
    /// Checks the party snapshot layout this writer encodes: the three audited top-level shapes (the legacy
    /// unkeyed body, the kr-1256 keyed profile map and the cn-774/tw-454 unkeyed equipped vector), the member
    /// fields it fills and the profile-entry fields of the element shape the build's list names.
    /// </summary>
    /// <param name="tables">The row's packet tables.</param>
    /// <param name="build">The build whose layout is checked.</param>
    internal static void RequireLayouts(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireSignature(tables, build, "SoloPartySnapshot", "Login party snapshot",
            "state:u32,members:list<SoloPartyMember>,equipped:empty_list",
            "state:u32,members:list<SoloPartyMember>,profile_entries:list<RoomProfileEntry>",
            "state:u32,members:list<SoloPartyMember>,profile_entries:list<SoloPartyEquippedItem>");
        PacketLayoutContract.RequireFields(tables, build, "SoloPartyMember", "Login party snapshot",
            ("user_number", "u32"), ("nickname", "wstr"));
        var entries = tables.Resolve(build, "SoloPartySnapshot").FirstOrDefault(field => field.Name == "profile_entries");
        if (entries?.Type == "list<RoomProfileEntry>")
            PacketLayoutContract.RequireFields(tables, build, "RoomProfileEntry", "Login party snapshot",
                ("user_number", "u32"), ("character_id", "u32"));
        else if (entries?.Type == "list<SoloPartyEquippedItem>")
            PacketLayoutContract.RequireFields(tables, build, "SoloPartyEquippedItem", "Login party snapshot",
                ("user_number", "u32"), ("character_id", "u32"));
    }

    /// <summary>
    /// Writes the idle party of one player: state 2, one member, and one profile record that carries the
    /// equipped character item, in the slot the build reads as a keyed map or as an element vector.
    /// </summary>
    /// <param name="tables">The row's packet tables.</param>
    /// <param name="build">The row's client build; the tables select the body layout.</param>
    /// <param name="userNumber">The login user number written as the member and profile identity.</param>
    /// <param name="nickname">The player's nickname, written into the member.</param>
    /// <param name="characterItemId">The persisted equipped character item id carried by the profile record.</param>
    /// <returns>The <c>gspUpdateParty</c> body.</returns>
    internal static byte[] Write(BubbleFighter.Fantasy.Packets.PacketTables tables, string build, int userNumber,
        string nickname, int characterItemId)
    {
        var member = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["user_number"] = userNumber,
            ["nickname"] = nickname,
        };
        // kr-1256 reads user_number as the profile map's key and character_id as its payload head; cn-774
        // and tw-454 read the same two words as the record's own identity and equipped character.
        var profile = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["user_number"] = userNumber,
            ["character_id"] = characterItemId,
        };
        return PacketCodec.Write(tables, build, "SoloPartySnapshot", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // kr-1256 sub_99D510 leaves for the plaza only in state 2, the value every reader stores at +4.
            ["state"] = 2,
            ["members"] = new[] { member },
            // The kr-1256 keyed map and the cn-774/tw-454 unkeyed vector are the same table slot; each
            // build encodes the entries with its own element shape.
            ["profile_entries"] = new[] { profile },
        });
    }
}
