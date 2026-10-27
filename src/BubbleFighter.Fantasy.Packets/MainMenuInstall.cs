using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Writes the main-menu <c>gspGotoRoom</c> body our server sends today, from the packet tables: a kind-1 session
/// that carries the local player alone, the session scalars kept as sent, one seat, the profile maps and the team
/// block.
/// The current byte layout and every field it fills come from the three modern readers: handler
/// <c>kr-1256 sub_9AC490</c> with parser <c>kr-1256 sub_10D4520</c>, <c>cn-774 sub_BAB9F0</c> and
/// <c>tw-454 sub_A89100</c>. The builds differ only in what <c>packets/room.yaml</c> says, so this writer states
/// one dictionary of values and no build identity.
/// </summary>
public static class MainMenuInstall
{
    /// <summary>Character item ids start here; a seat's <c>character_type</c> is the item id minus this base.</summary>
    private const int CharacterItemBase = 300000;

    /// <summary>
    /// Checks the <c>gspGotoRoom</c> layouts this writer encodes: the kr-1256 shape and the cn-774/tw-454 shape,
    /// the session and seat fields it fills, the team block, the profile-entry element shape the build's list
    /// names, and the kr-1256 +648 optional record. A field that keeps its name but changes its type fails at
    /// activation instead of when the main menu installs.
    /// </summary>
    /// <param name="tables">The packet tables that state each build's layout.</param>
    /// <param name="build">The client build whose layout is checked.</param>
    public static void RequireLayouts(PacketTables tables, string build)
    {
        PacketLayoutContract.RequireSignature(tables, build, "gspGotoRoom", "Main menu install",
            "session_setting:SessionSetting,master_slot:u32,unknown_548:u32,user_slots:list<UserSlot>,"
            + "profile_entries:list<RoomProfileEntry>,unknown_588:list<RoomProfileEntry>,team_info:ServerTeamInfo,"
            + "unknown_648:optional<0x3A,MainMenuOptional>,local_slot:u32,party_transition:bool",
            "session_setting:SessionSetting,master_slot:u32,local_slot:u32,user_slots:list<UserSlot>,"
            + "equipped_objects:list<EquippedObjectEntry>,party_transition:bool,unknown_n1:u32,"
            + "team_info:ServerTeamInfo");
        PacketLayoutContract.RequireSignature(tables, build, "SessionSetting", "Main menu install",
            "room_id:u16,session_state:u32,title:wstr,occupants:u16,unknown_34:u16,open_seats:u16,has_password:bool,"
            + "master_nickname:wstr,kind:u32,game_setting:GameSetting,unknown_68:u32,unknown_72:u32,unknown_75:u32,"
            + "unknown_80:u64,unknown_88:wstr,unknown_112:bool,unknown_116:u32,unknown_120:u32,unknown_124:u32,"
            + "unknown_128:u16",
            "room_id:u16,session_state:u32,title:wstr,occupants:u16,unknown_34:u16,open_seats:u16,has_password:bool,"
            + "master_nickname:wstr,kind:u32,game_setting:GameSetting,unknown_68:u32,unknown_72:u64,unknown_80:wstr,"
            + "unknown_104:bool,unknown_108:u32,unknown_112:u32,unknown_116:u32,unknown_120:u16");
        PacketLayoutContract.RequireFields(tables, build, "UserSlot", "Main menu install",
            ("occupied", "bool"), ("user_number", "u32"), ("nickname", "wstr"),
            ("level", "u32"), ("social", "SocialSystemInfo"));
        if (tables.Resolve(build, "UserSlot").Any(field => field.Name == "team"))
            PacketLayoutContract.RequireFields(tables, build, "UserSlot", "Main menu install", ("team", "u8"));
        if (tables.Resolve(build, "UserSlot").Any(field => field.Name == "character_type"))
            PacketLayoutContract.RequireFields(tables, build, "UserSlot", "Main menu install",
                ("character_type", "u32"));
        PacketLayoutContract.RequireFields(tables, build, "ServerTeamInfo", "Main menu install",
            ("team_slots", "list<ServerTeamSlots>"), ("unknown_12", "list<u32>"));
        var room = tables.Resolve(build, "gspGotoRoom");
        if (room.Any(field => field.Name == "profile_entries"))
            PacketLayoutContract.RequireFields(tables, build, "RoomProfileEntry", "Main menu install",
                ("user_number", "u32"), ("character_id", "u32"), ("equipped_items", "list<u32>"));
        if (room.Any(field => field.Name == "equipped_objects"))
            PacketLayoutContract.RequireFields(tables, build, "EquippedObjectEntry", "Main menu install",
                ("user_number", "u32"), ("character_id", "u32"));
        if (room.Any(field => field.Name == "unknown_648"))
            PacketLayoutContract.RequireFields(tables, build, "MainMenuOptional", "Main menu install",
                ("unknown_0", "u32"), ("unknown_8", "u64"), ("unknown_n3", "u8"), ("unknown_48", "u32"),
                ("unknown_52", "u32"), ("unknown_56", "u32"), ("unknown_60", "u32"), ("unknown_64", "u32"));
    }

    /// <summary>
    /// The login install kind: case 1 of <c>kr-1256 sub_9AC490</c>, <c>cn-774 sub_BAB9F0</c> and
    /// <c>tw-454 sub_A89100</c> selects the main-menu stage; kind 2 enters the room-specific path.
    /// </summary>
    private const int MainMenuKind = 1;

    /// <summary>
    /// Writes one main-menu <c>gspGotoRoom</c> body for the requested build. The values are the session kind and
    /// the scalars kept as sent today, one occupied seat at slot 0, the same profile entry in both profile maps,
    /// the team block kept as sent today, the kr-1256 +648 optional record and, where the build reads them, the
    /// equipped-object vector and the cleared party-transition flag. Fields the build's tables do not name are
    /// dropped by the projection, so one dictionary serves all three layouts.
    /// </summary>
    /// <param name="tables">The packet tables that state each build's layout and main-menu menu values.</param>
    /// <param name="build">The client build whose <c>gspGotoRoom</c> body is written.</param>
    /// <param name="userNumber">The local player's identity, used as the seat and profile-map key.</param>
    /// <param name="nickname">The local player's nickname, which the client matches itself by.</param>
    /// <param name="level">The player's level; a value below 1 is raised to 1, which shows the level icon.</param>
    /// <param name="characterItemId">The equipped character item id (300001..301999), also the seat's character type base.</param>
    /// <returns>The complete unframed <c>gspGotoRoom</c> body.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tables"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for a character item id outside 300001..301999.</exception>
    public static byte[] Write(PacketTables tables, string build, int userNumber, string nickname, int level, int characterItemId)
    {
        ArgumentNullException.ThrowIfNull(tables);
        if (characterItemId is < CharacterItemBase + 1 or > CharacterItemBase + 1999)
            throw new ArgumentOutOfRangeException(nameof(characterItemId), characterItemId,
                "Modern room characters must use a 300001..301999 item id.");
        // The cn-774 and tw-454 seats copy the character type, not the item id (their readers store it at +40).
        var characterType = characterItemId - CharacterItemBase;
        var game = PacketMenuProjection.Project(tables, build, "GameSetting", new Dictionary<string, object?>());
        var session = PacketMenuProjection.Project(tables, build, "SessionSetting", new Dictionary<string, object?>
        {
            // The main-menu install writes u16 1, i32 1 and "MainMenu" in every build's first three fields
            // (the first three fields are read by kr-1256 sub_113BD20); the values are kept as sent today.
            ["room_id"] = 1,
            ["session_state"] = 1,
            ["title"] = "MainMenu",
            ["kind"] = MainMenuKind,
            ["game_setting"] = game,
            // Both words are kept as sent today; their meaning is not traced.
            ["unknown_68"] = 1,
            ["unknown_72"] = 1,
        });
        var seat = PacketMenuProjection.Project(tables, build, "UserSlot", new Dictionary<string, object?>
        {
            ["occupied"] = true,
            // The seat carries team 2 as sent today; kr-1256 sub_980730's team board switches on the value, but
            // the assignment's meaning is not traced.
            ["team"] = 2,
            ["user_number"] = userNumber,
            ["nickname"] = nickname,
            ["character_type"] = characterType,
            // kr-1256 sub_EE9550 stores level - 1, so 0 would draw no level icon.
            ["level"] = Math.Max(1, level),
            ["social"] = new Dictionary<string, object?>(),
        });
        var profile = new Dictionary<string, object?>
        {
            ["user_number"] = userNumber,
            ["character_id"] = characterItemId,
            ["equipped_items"] = new[] { characterItemId },
        };
        var equippedObject = new Dictionary<string, object?>
        {
            ["user_number"] = userNumber,
            ["character_id"] = characterItemId,
        };
        var teamInfo = new Dictionary<string, object?>
        {
            // One map entry {team 2, slots [0]}, then the slot vector 1..11 kept as sent today (meaning not
            // traced), then an empty vector.
            ["team_slots"] = new[]
            {
                new Dictionary<string, object?> { ["team"] = 2, ["slots"] = new[] { 0 } },
            },
            ["unknown_12"] = Enumerable.Range(1, 11).ToArray(),
        };
        // kr-1256 sub_10D4E40 reads this block only after the 0x3A tag; every field is zero or empty.
        var optional = new Dictionary<string, object?>
        {
            ["unknown_0"] = 0,
            ["unknown_8"] = 0L,
            ["unknown_n3"] = (byte)0,
            ["unknown_48"] = 0,
            ["unknown_52"] = 0,
            ["unknown_56"] = 0,
            ["unknown_60"] = 0,
            ["unknown_64"] = 0,
        };
        var values = PacketMenuProjection.Project(tables, build, "gspGotoRoom", new Dictionary<string, object?>
        {
            ["session_setting"] = session,
            ["master_slot"] = 0,
            ["unknown_548"] = 0,
            ["user_slots"] = new[] { seat },
            ["profile_entries"] = new[] { profile },
            // kr-1256 sub_10C4010 reads the second profile map; the main-menu install repeats the entry there.
            ["unknown_588"] = new[] { profile },
            ["equipped_objects"] = new[] { equippedObject },
            ["unknown_4"] = 0,
            ["unknown_n1"] = 0,
            ["team_info"] = teamInfo,
            ["unknown_648"] = optional,
            ["local_slot"] = 0,
            ["party_transition"] = false,
        });
        return PacketCodec.Write(tables, build, "gspGotoRoom", values);
    }
}
