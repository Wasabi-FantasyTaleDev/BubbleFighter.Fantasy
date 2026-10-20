using System.Buffers;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Wire codecs for the legacy plaza room loop: list / create / ready / host-start.
/// Field widths are proven per build (kr-812 / cn-717 / id-400).
/// </summary>
internal static class PlazaRoomWire
{
    /// <summary>
    /// Default MapMeta row name used when CreateSession carries no non-empty GameSetting wstr.
    /// </summary>
    public const string DefaultMapKey = "rel_Village07_Easy02";

    /// <summary>
    /// Writes a CreateSession body for tests: SessionSetting from the schema, then password wstr
    /// and trailing bool32. GameSetting wstrs stay empty unless the schema constants carry them.
    /// </summary>
    /// <param name="room">Identity for nested fields.</param>
    /// <param name="schema">The build's room layout.</param>
    /// <param name="password">Trailing password wstr.</param>
    /// <param name="unknown0">Trailing bool32.</param>
    /// <returns>The C2S body.</returns>
    public static byte[] WriteCreateSessionForTest(MainMenuRoomOpened room, RoomSchema schema, string password, bool unknown0)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        var w = new PacketWriter(buffer);
        RoomCodec.WriteSessionSetting(w, room, schema, RoomCodec.PlayRoomKind);
        w.WriteWideStringPrefixed(password);
        w.WriteBool32(unknown0);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads the audited list request through the build's packet table.</summary>
    /// <param name="tables">The packet tables.</param><param name="build">The client build.</param>
    /// <param name="payload">The request body.</param>
    /// <returns>The two native unknown words.</returns>
    public static (ushort Unknown0, ushort Unknown1) ReadListSession(PacketTables tables, string build, ReadOnlySpan<byte> payload)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "RoomListRequest", payload);
        return (Convert.ToUInt16(fields["unknown_4"]), Convert.ToUInt16(fields["unknown_6"]));
    }

    /// <summary>
    /// S2C <c>gspRepListSession</c> (kr-812 reader <c>sub_680780</c> / cn-717 <c>sub_6D98E0</c> /
    /// id-400 <c>sub_5DBAA0</c> / id-1809 <c>sub_623930</c>): <c>u32</c> unknown0,
    /// <c>bool32</c> unknown1, <c>u32</c> count, then count SessionSetting — the build's
    /// <see cref="RoomSchema.SessionSetting"/> table, which id-1809 reads through
    /// <c>sub_4173B0</c> (the kr-812 layout, longer than id-400's <c>sub_5DB3C0</c>).
    /// Empty list is valid (cn-717 LobbyStage <c>sub_6D1620</c>
    /// opcode 101; id-400 <c>sub_5E0BB0</c> case <c>'e'</c>).
    /// </summary>
    /// <param name="tables">The native list envelope tables.</param>
    /// <param name="build">The selected client build.</param>
    /// <returns>A twelve-byte empty list body.</returns>
    public static byte[] WriteRepListSessionEmpty(PacketTables tables, string build) =>
        PacketCodec.Write(tables, build, "RoomListReply", new Dictionary<string, object?>());

    /// <summary>
    /// C2S <c>gcpReqCreateSession</c> (kr-812 serialize <c>sub_480D40</c> / reader
    /// <c>sub_480D80</c>; id-1809 serializer <c>sub_450090</c> / reader <c>sub_4500D0</c>):
    /// nested SessionSetting, then a trailing wstr and bool32.
    /// </summary>
    /// <param name="payload">The C2S body.</param>
    /// <param name="schema">The build's room field tables.</param>
    /// <returns>The first non-empty GameSetting wstr (map key candidate) and the password wstr.</returns>
    public static CreateSessionRequest ReadCreateSession(ReadOnlySpan<byte> payload, RoomSchema schema)
    {
        var r = new PacketReader(payload);
        var mapKey = ReadSessionSettingMaps(ref r, schema);
        var password = r.ReadWideStringPrefixed();
        var unknown0 = r.ReadBool32();
        if (r.Remaining > 0)
            throw new PacketFormatException($"gcpReqCreateSession has {r.Remaining} trailing byte(s).", r.Offset);
        return new CreateSessionRequest(mapKey, password, unknown0);
    }

    /// <summary>
    /// C2S <c>gcpReqCreateSession</c> for a play room (kr-812 serialize <c>sub_480D40</c>;
    /// id-1809 serializer <c>sub_450090</c>): the <c>SessionSetting</c> member first, then the
    /// password wstr and a bool32. The title is the <c>SessionSetting</c> wstr at +12, the mode
    /// and map key are the <c>GameSetting</c> roles of the build's table, and the whole
    /// <c>GameSetting</c> is returned as sent.
    /// </summary>
    /// <param name="payload">The C2S body.</param>
    /// <param name="schema">The build's room field tables.</param>
    /// <returns>The decoded request.</returns>
    public static CreateRoomRequest ReadCreateRoom(ReadOnlySpan<byte> payload, RoomSchema schema)
    {
        var r = new PacketReader(payload);
        var setting = ReadPlaySessionSetting(ref r, schema);
        var password = r.ReadWideStringPrefixed();
        var unknown0 = r.ReadBool32();
        if (r.Remaining > 0)
            throw new PacketFormatException($"gcpReqCreateSession has {r.Remaining} trailing byte(s).", r.Offset);
        return new CreateRoomRequest(setting.Title, setting.Mode, setting.MapKey,
            payload[setting.GameSettingStart..setting.GameSettingEnd].ToArray(), password, unknown0);
    }

    /// <summary>Reads the stage a <c>GameSetting</c> block selects: its <c>current_map</c> string.</summary>
    /// <param name="block">The GameSetting bytes in this build's layout.</param>
    /// <param name="schema">The build's room field tables.</param>
    /// <returns>The stage key, or empty when the layout has no current map.</returns>
    internal static string ReadGameSettingCurrentMap(ReadOnlySpan<byte> block, RoomSchema schema)
    {
        var r = new PacketReader(block);
        foreach (var field in schema.GameSetting)
        {
            if (field.Type != RoomFieldType.WStr)
            {
                ConsumeField(ref r, field);
                continue;
            }
            var text = r.ReadWideStringPrefixed();
            if (field.Value == RoomFieldValue.CurrentMap)
                return text;
        }
        return "";
    }

    /// <summary>Reads one play-room <c>SessionSetting</c> and reports where its <c>GameSetting</c> lies.</summary>
    /// <param name="r">The reader, positioned at the structure.</param>
    /// <param name="schema">The build's room field tables.</param>
    /// <returns>The title, mode, map key and the byte range of the nested <c>GameSetting</c>.</returns>
    internal static PlaySessionSetting ReadPlaySessionSetting(ref PacketReader r, RoomSchema schema)
    {
        var table = schema.PlaySessionSetting
            ?? throw new InvalidOperationException("This build's play-room layout is not decoded.");
        string title = "", currentMap = "", mapName = "";
        int gameStart = 0, gameEnd = 0;
        var mode = 0;
        foreach (var field in table)
        {
            if (field.Type == RoomFieldType.GameSetting)
            {
                gameStart = r.Offset;
                foreach (var inner in schema.GameSetting)
                {
                    if (inner.Type == RoomFieldType.WStr)
                    {
                        var text = r.ReadWideStringPrefixed();
                        if (inner.Value == RoomFieldValue.CurrentMap)
                            currentMap = text;
                        else if (inner.Value == RoomFieldValue.MapName)
                            mapName = text;
                    }
                    else if (inner is { Type: RoomFieldType.U32, Value: RoomFieldValue.Mode })
                        mode = r.ReadInt32();
                    else
                        ConsumeField(ref r, inner);
                }
                gameEnd = r.Offset;
            }
            else if (field is { Type: RoomFieldType.WStr, Value: RoomFieldValue.Title })
                title = r.ReadWideStringPrefixed();
            else
                ConsumeField(ref r, field);
        }

        // cn-324 sub_59E1C0 writes the selected row to GameSetting+28; +20 is its resource directory.
        return new PlaySessionSetting(title, mode, mapName.Length > 0 ? mapName : currentMap, gameStart, gameEnd);
    }

    /// <summary>Reads the build-specific join request through the shared packet table.</summary>
    /// <param name="tables">The packet tables.</param><param name="build">The client build.</param>
    /// <param name="payload">The request body.</param>
    /// <returns>The room number, password and three fields whose meanings remain unproven.</returns>
    public static JoinSessionRequest ReadJoinSession(PacketTables tables, string build, ReadOnlySpan<byte> payload)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "RoomJoinRequest", payload);
        var tail = fields.TryGetValue("unknown_44", out var value) ? Convert.ToInt32(value) : 0;
        return new JoinSessionRequest((int)fields["room_id"], (int)fields["unknown_8"],
            (int)fields["unknown_12"], (string)fields["password"], tail);
    }

    /// <summary>
    /// C2S <c>gcpRoomChangeGameSetting</c> (kr-812 serialize <c>sub_5C8DE0</c> / reader <c>sub_5C8ED0</c>):
    /// wstr x4, bool32 x2, u32 x10, bool32. The strings are <c>GameSetting</c> +16, +44, +72 and
    /// +100, the bools +4 and +5, and the ninth and tenth u32 the mode and sub-mode; the other
    /// values have no proven target.
    /// </summary>
    /// <param name="tables">The native packet layouts.</param>
    /// <param name="build">The selected client build.</param>
    /// <param name="payload">The C2S body.</param>
    /// <returns>The decoded change.</returns>
    public static GameSettingChange ReadChangeGameSetting(PacketTables tables, string build, ReadOnlySpan<byte> payload)
    {
        var values = PacketCodec.ReadScalars(tables, build, "RoomGameSettingChangeRequest", payload);
        var aliases = SettingChangeFields.Require(tables, build);
        return new GameSettingChange((string)values["map_directory"], (string)values["current_map"],
            (string)values[aliases.ThirdString], (string)values["map_key"], (bool)values[aliases.FirstFlag],
            (bool)values[aliases.SecondFlag], (int)values["mode"], (int)values["sub_mode"]);
    }

    /// <summary>
    /// Returns <paramref name="gameSetting"/> with a recorded settings change applied: the four
    /// strings, the two bools and the mode and sub-mode. An empty block starts from the build's
    /// table. Every other byte is kept as it was.
    /// </summary>
    /// <param name="schema">The build's named room fields used to locate each changed value.</param>
    /// <param name="tables">The native request layouts.</param>
    /// <param name="build">The selected client build.</param>
    /// <param name="gameSetting">The encoded block, or empty.</param>
    /// <param name="change">The encoded <c>gcpRoomChangeGameSetting</c> body, or empty for no change.</param>
    /// <returns>The block to send.</returns>
    public static byte[] ApplyGameSettingChange(RoomSchema schema, PacketTables tables, string build,
        ReadOnlySpan<byte> gameSetting, ReadOnlySpan<byte> change)
    {
        if (gameSetting.IsEmpty)
            gameSetting = RoomCodec.WriteGameSettingEnvelope(tables, build, schema,
                new RoomValues(RoomCodec.PlayRoomKind, []) { Play = true });
        if (change.IsEmpty)
            return gameSetting.ToArray();

        foreach (var name in new[] { "mode", "unknown_12", "unknown_4", "unknown_5" })
            if (!schema.GameSetting.Any(field => field.Name == name &&
                field.Type == (name is "mode" or "unknown_12" ? RoomFieldType.U32 : RoomFieldType.Bool)))
                throw new ConfigException($"GameSetting patch requires the audited '{name}' field.");
        foreach (var role in new[] { RoomFieldValue.MapDirectory, RoomFieldValue.CurrentMap, RoomFieldValue.MapName })
            if (!schema.GameSetting.Any(field => field.Value == role && field.Type == RoomFieldType.WStr))
                throw new ConfigException($"GameSetting patch requires the audited {role} mapping.");
        if (!schema.GameSetting.Any(field => field.Type == RoomFieldType.WStr &&
            field.Value is RoomFieldValue.NpcTableKey or RoomFieldValue.MapTheme))
            throw new ConfigException("GameSetting patch requires an audited NPC-key or theme mapping.");
        var applied = ReadChangeGameSetting(tables, build, change);
        var r = new PacketReader(gameSetting);
        var buffer = new ArrayBufferWriter<byte>(gameSetting.Length + 128);
        var w = new PacketWriter(buffer);
        for (var i = 0; i < schema.GameSetting.Length; i++)
        {
            var field = schema.GameSetting[i];
            var start = r.Offset;
            if (field.Type == RoomFieldType.WStr)
            {
                var text = r.ReadWideStringPrefixed();
                // kr-812 sub_5C2B80 copies the map strings; the third carries the theme tab.
                w.WriteWideStringPrefixed(field.Value switch
                {
                    RoomFieldValue.MapDirectory => applied.DisplayName,
                    RoomFieldValue.CurrentMap => applied.CurrentMap,
                    RoomFieldValue.NpcTableKey or RoomFieldValue.MapTheme => applied.Unknown72,
                    RoomFieldValue.MapName => applied.MapKey,
                    _ => text,
                });
                continue;
            }
            if (field.Type == RoomFieldType.Bool)
            {
                var value = r.ReadBool32();
                w.WriteBool32(field.Name switch
                {
                    "unknown_4" => applied.Flag4,
                    "unknown_5" => applied.Flag5,
                    _ => value,
                });
                continue;
            }
            if (field.Type == RoomFieldType.U32 && field.Name is "mode" or "unknown_12")
            {
                r.ReadInt32();
                w.WriteInt32(field.Name == "mode" ? applied.Mode : applied.SubMode);
                continue;
            }
            ConsumeField(ref r, field);
            w.WriteBytes(gameSetting[start..r.Offset]);
        }
        if (r.Remaining > 0)
            throw new PacketFormatException($"GameSetting has {r.Remaining} trailing byte(s).", r.Offset);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Returns <paramref name="gameSetting"/> with its <c>mode</c> word replaced, every other byte kept. The server
    /// uses it when it changes a room's mode itself, so the settings the clients read agree with the room.
    /// </summary>
    /// <param name="schema">The build's named room fields; they must name a mode word.</param>
    /// <param name="gameSetting">The encoded block in this build's layout.</param>
    /// <param name="mode">The mode id to write.</param>
    /// <returns>The block with the new mode.</returns>
    public static byte[] ApplyGameSettingMode(RoomSchema schema, ReadOnlySpan<byte> gameSetting, int mode)
    {
        if (!schema.GameSetting.Any(field => field is { Type: RoomFieldType.U32, Value: RoomFieldValue.Mode }))
            throw new ConfigException("GameSetting mode change requires an audited mode field.");
        var r = new PacketReader(gameSetting);
        var buffer = new ArrayBufferWriter<byte>(gameSetting.Length);
        var w = new PacketWriter(buffer);
        foreach (var field in schema.GameSetting)
        {
            var start = r.Offset;
            if (field is { Type: RoomFieldType.U32, Value: RoomFieldValue.Mode })
            {
                r.ReadInt32();
                w.WriteInt32(mode);
                continue;
            }
            ConsumeField(ref r, field);
            w.WriteBytes(gameSetting[start..r.Offset]);
        }
        if (r.Remaining > 0)
            throw new PacketFormatException($"GameSetting has {r.Remaining} trailing byte(s).", r.Offset);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The <c>gspRepJoinSession</c> result code for a refusal. The kr-812 handler <c>sub_67AE80</c>
    /// switches on it and shows a fixed message per case: 1 the round already started
    /// (<c>0xD40CF4</c>), 3 the room is full (<c>0xD40D18</c>), 4 wrong password (<c>0xD40D34</c>),
    /// 5 no matching room (<c>0xD40D8C</c>). Zero shows nothing.
    /// </summary>
    /// <param name="reason">Why the join was refused.</param>
    /// <returns>The code the client switches on.</returns>
    public static int JoinResultCode(BubbleFighter.Fantasy.Contracts.Rooms.RoomJoinRefusal reason) => reason switch
    {
        BubbleFighter.Fantasy.Contracts.Rooms.RoomJoinRefusal.Started => 1,
        BubbleFighter.Fantasy.Contracts.Rooms.RoomJoinRefusal.Full => 3,
        BubbleFighter.Fantasy.Contracts.Rooms.RoomJoinRefusal.WrongPassword => 4,
        BubbleFighter.Fantasy.Contracts.Rooms.RoomJoinRefusal.NoRoom => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "No result code is known for this refusal."),
    };

    /// <summary>
    /// C2S <c>gcpRoomReqReadyStatus</c> (kr-812 <c>sub_5C8C00</c> / cn-717 <c>sub_88EE40</c> /
    /// id-400 <c>sub_613F80</c>): three bool32.
    /// </summary>
    /// <param name="tables">The native packet layouts.</param>
    /// <param name="build">The selected client build.</param>
    /// <param name="payload">The C2S body.</param>
    /// <returns>The three ready-status flags.</returns>
    public static (bool Unknown0, bool Unknown1, bool Unknown2) ReadReadyStatus(PacketTables tables, string build,
        ReadOnlySpan<byte> payload)
    {
        var values = PacketCodec.ReadScalars(tables, build, "RoomReadyRequest", payload);
        return ((bool)values["ready"], (bool)values["unknown_5"], (bool)values["unknown_6"]);
    }

    /// <summary>
    /// C2S <c>gcpUserHostStart</c> (kr-812 send helper <c>sub_5C85E0</c> SETOP 2025; deserialize
    /// nullsub <c>sub_5C8FC0</c>): empty body. cn-717 send helper <c>sub_60C560</c> SETOP 2025 /
    /// nullsub <c>sub_BD9960</c> matches.
    /// </summary>
    /// <param name="payload">The C2S body.</param>
    public static void ReadUserHostStart(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 0)
            throw new PacketFormatException($"gcpUserHostStart must be empty; got {payload.Length} byte(s).", 0);
    }

    /// <summary>
    /// C2S <c>gcpRoomReqEnterGame</c> (kr-812 reader <c>sub_5C8D90</c>; id-1809 serializer
    /// <c>sub_581670</c> / reader <c>sub_5816C0</c>): bool32, three wstr, u32, then nested
    /// <c>MapMetaData</c> (13 u32 + N bool32). kr-812 sub_816300 copies session+132 into
    /// the first string and session+188 into the third; a pirate entry carries a
    /// physical directory and mission selection.
    /// </summary>
    /// <param name="tables">The native packet layouts.</param>
    /// <param name="build">The selected client build.</param>
    /// <param name="payload">The C2S body.</param>
    /// <returns>The first wstr (resolved map) and the pre-MapMeta word, retained under its compatibility name.</returns>
    public static (string MapKey, int Unknown1) ReadRoomReqEnterGame(PacketTables tables, string build,
        ReadOnlySpan<byte> payload)
    {
        var values = PacketCodec.ReadScalars(tables, build, "RoomEnterGameRequest", payload);
        var firstWord = tables.Resolve(build, "RoomEnterGameRequest").First(field => field.Type == "u32");
        return ((string)values["map_key"], (int)values[firstWord.Name]);
    }

    /// <summary>
    /// S2C <c>gspRoomSetSlot</c> (kr-812 reader <c>sub_80C1B0</c> / handler <c>sub_7FD800</c>;
    /// id-1809 reader <c>sub_7A5A30</c> via room dispatcher <c>sub_7A6A40</c> case 0x1F7):
    /// <c>u32</c> slot, then one UserSlot and one EquippedItemList.
    /// UserSlot ready flag is the bool at object +125 (kr-812 reader <c>sub_4196F0</c>;
    /// id-1809 <c>sub_417880</c> reads the same slot at +125 inside its 240-byte object).
    /// id-1809's <c>EquippedItemList</c> reader is <c>sub_4D4960</c>.
    /// </summary>
    /// <param name="room">The seat occupant.</param>
    /// <param name="schema">The build's room layout.</param>
    /// <param name="slot">Seat index (the host is at 0).</param>
    /// <param name="tables">The native seat-envelope tables.</param>
    /// <param name="build">The client build used to resolve nested records.</param>
    /// <param name="ready">Value for the named UserSlot ready flag.</param>
    /// <returns>The S2C 503 body.</returns>
    public static byte[] WriteSetSlot(MainMenuRoomOpened room, PacketTables tables, string build,
        RoomSchema schema, int slot, bool ready) =>
        RoomCodec.WriteMenuSeatEnvelope(tables, build, schema, room, slot, ready);

    private static string ReadSessionSettingMaps(ref PacketReader r, RoomSchema schema)
    {
        string mapKey = "";
        foreach (var field in schema.SessionSetting)
        {
            if (field.Type == RoomFieldType.GameSetting)
            {
                var fromGame = ReadGameSettingMaps(ref r, schema);
                if (mapKey.Length == 0)
                    mapKey = fromGame;
                continue;
            }

            ConsumeField(ref r, field);
        }

        return mapKey;
    }

    private static string ReadGameSettingMaps(ref PacketReader r, RoomSchema schema)
    {
        string mapKey = "";
        foreach (var field in schema.GameSetting)
        {
            if (field.Type == RoomFieldType.WStr)
            {
                var s = r.ReadWideStringPrefixed();
                if (mapKey.Length == 0 && s.Length > 0)
                    mapKey = s;
                continue;
            }

            ConsumeField(ref r, field);
        }

        return mapKey;
    }

    /// <summary>
    /// Returns the <c>GameSetting</c> block with a map that was not chosen shown as the all-random entry:
    /// the display name, the current map and the map key all become the entry's key, and the theme string
    /// becomes the first theme, as the client's own map dialog writes that entry (<c>cn-324 sub_59E1C0</c>)
    /// and as a create is answered (S2C 110:
    /// <c>Random_Hot_Dif1</c>, <c>Random_Hot_Dif1</c>, <c>All</c>, <c>Random_Hot_Dif1</c> for a mode-101 create
    /// that carried <c>rel_Village01_Byway</c>).
    /// A map was not chosen when the key is empty (<c>cn-324 sub_582C00</c> fills only the display name and the
    /// current map) or when the block is the creator's create request: every create path copies the startup map
    /// of <c>Config/Lambient.xml</c> (<c>kr-812 sub_480730</c> and <c>kr-812 sub_480860</c> through
    /// <c>kr-812 sub_480700</c>), so that map is the client's default, never the player's choice.
    /// Any other block whose key is set is returned unchanged.
    /// </summary>
    /// <param name="schema">The build's room field tables.</param>
    /// <param name="block">One encoded <c>GameSetting</c>.</param>
    /// <param name="unchosenKey">The key the all-random entry has; empty leaves every block as it is.</param>
    /// <param name="unchosenTheme">The theme the all-random entry sits under.</param>
    /// <param name="createRequest">Whether the block is the creator's create request, unchanged since.</param>
    /// <param name="difficultyMode">The mode whose all-random entry is <paramref name="difficultyKey"/>; 0 for none.</param>
    /// <param name="difficultyKey">The all-random entry of <paramref name="difficultyMode"/>; empty for none.</param>
    /// <returns>The block to show the room's members.</returns>
    internal static ReadOnlyMemory<byte> PresentUnchosenMap(RoomSchema schema, ReadOnlySpan<byte> block,
        string unchosenKey, string unchosenTheme, bool createRequest = false, int difficultyMode = 0, string difficultyKey = "")
    {
        if (unchosenKey.Length == 0)
            return block.ToArray();
        var r = new PacketReader(block);
        var key = "";
        var mode = 0;
        var spans = new SortedDictionary<int, (RoomFieldValue Role, int End)>();
        foreach (var field in schema.GameSetting)
        {
            var start = r.Offset;
            if (field.Type == RoomFieldType.WStr)
            {
                var text = r.ReadWideStringPrefixed();
                if (field.Value is RoomFieldValue.MapDirectory or RoomFieldValue.CurrentMap or RoomFieldValue.MapTheme
                    or RoomFieldValue.NpcTableKey or RoomFieldValue.MapName)
                    spans[start] = (field.Value, r.Offset);
                if (field.Value == RoomFieldValue.MapName)
                    key = text;
            }
            else if (field is { Type: RoomFieldType.U32, Value: RoomFieldValue.Mode })
                mode = r.ReadInt32();
            else
                ConsumeField(ref r, field);
        }
        if ((key.Length > 0 && !createRequest) || !spans.Values.Any(span => span.Role == RoomFieldValue.MapName))
            return block.ToArray();

        var entry = difficultyKey.Length > 0 && mode == difficultyMode ? difficultyKey : unchosenKey;
        var buffer = new ArrayBufferWriter<byte>(block.Length + 2 * (3 * entry.Length + unchosenTheme.Length));
        var w = new PacketWriter(buffer);
        var cursor = 0;
        foreach (var (start, (role, end)) in spans)
        {
            w.WriteBytes(block[cursor..start]);
            // kr-812 sub_40EE80 reads the third string as the theme; the layouts name it npc_table_key or map_theme.
            w.WriteWideStringPrefixed(role is RoomFieldValue.MapTheme or RoomFieldValue.NpcTableKey ? unchosenTheme : entry);
            cursor = end;
        }
        w.WriteBytes(block[cursor..]);
        return buffer.WrittenMemory;
    }

    private static void ConsumeField(ref PacketReader r, RoomField field)
    {
        switch (field.Type)
        {
            case RoomFieldType.U8:
                r.ReadByte();
                break;
            case RoomFieldType.U16:
                r.ReadUInt16();
                break;
            case RoomFieldType.U32:
                r.ReadInt32();
                break;
            case RoomFieldType.Bool:
                r.ReadBool32();
                break;
            case RoomFieldType.F32:
                r.ReadSingle();
                break;
            case RoomFieldType.F64:
                r.ReadDouble();
                break;
            case RoomFieldType.WStr:
                r.ReadWideStringPrefixed();
                break;
            case RoomFieldType.EmptyVector:
                if (r.ReadInt32() != 0)
                    throw new PacketFormatException("expected empty vector count 0.", r.Offset - 4);
                break;
            case RoomFieldType.Equipped:
                var n = r.ReadInt32();
                for (var i = 0; i < n; i++)
                    r.ReadInt32();
                break;
            default:
                throw new PacketFormatException($"CreateSession reader cannot consume field type {field.Type}.", r.Offset);
        }
    }
}

/// <summary>Parsed C2S <c>gcpReqCreateSession</c> fields needed by the plaza handler.</summary>
/// <param name="MapKey">First non-empty GameSetting wstr, or empty when none were set.</param>
/// <param name="Password">Trailing password wstr after SessionSetting.</param>
/// <param name="Unknown0">Trailing bool32 after the password.</param>
internal readonly record struct CreateSessionRequest(string MapKey, string Password, bool Unknown0);

/// <summary>Parsed play-room C2S <c>gcpReqCreateSession</c>.</summary>
/// <param name="Title">The room title, <c>SessionSetting</c> wstr at +12.</param>
/// <param name="Mode">The <c>GameSetting</c> mode.</param>
/// <param name="MapKey">The <c>GameSetting</c> map key.</param>
/// <param name="GameSetting">The whole <c>GameSetting</c> as the client encoded it.</param>
/// <param name="Password">Trailing password wstr after SessionSetting.</param>
/// <param name="Unknown0">Trailing bool32 after the password.</param>
internal sealed record CreateRoomRequest(string Title, int Mode, string MapKey, byte[] GameSetting, string Password, bool Unknown0);

/// <summary>Parsed C2S <c>gcpReqJoinSession</c>.</summary>
/// <param name="RoomId">The room number from the list row.</param>
/// <param name="Unknown1">Second u32.</param>
/// <param name="Unknown2">Third u32.</param>
/// <param name="Password">The password wstr.</param>
/// <param name="Unknown4">Trailing u32.</param>
internal readonly record struct JoinSessionRequest(int RoomId, int Unknown1, int Unknown2, string Password, int Unknown4);

/// <summary>What the room flow needs from a decoded play-room <c>SessionSetting</c>.</summary>
/// <param name="Title">The room title.</param>
/// <param name="Mode">The <c>GameSetting</c> mode.</param>
/// <param name="MapKey">The <c>GameSetting</c> map key.</param>
/// <param name="GameSettingStart">Offset of the nested <c>GameSetting</c> in the body.</param>
/// <param name="GameSettingEnd">Offset just past it.</param>
internal readonly record struct PlaySessionSetting(string Title, int Mode, string MapKey, int GameSettingStart, int GameSettingEnd);

/// <summary>Parsed C2S <c>gcpRoomChangeGameSetting</c>: the values with a proven <c>GameSetting</c> target.</summary>
/// <param name="DisplayName">The map's display name, <c>GameSetting</c> +16.</param>
/// <param name="CurrentMap">The map table's directory key, <c>GameSetting</c> +44.</param>
/// <param name="Unknown72">The string at <c>GameSetting</c> +72.</param>
/// <param name="MapKey">The player-chosen map key, <c>GameSetting</c> +100.</param>
/// <param name="Flag4">The bool at <c>GameSetting</c> +4.</param>
/// <param name="Flag5">The bool at <c>GameSetting</c> +5.</param>
/// <param name="Mode">The mode, <c>GameSetting</c> +8.</param>
/// <param name="SubMode">The sub-mode, <c>GameSetting</c> +12.</param>
internal sealed record GameSettingChange(string DisplayName, string CurrentMap, string Unknown72, string MapKey,
    bool Flag4, bool Flag5, int Mode, int SubMode);
