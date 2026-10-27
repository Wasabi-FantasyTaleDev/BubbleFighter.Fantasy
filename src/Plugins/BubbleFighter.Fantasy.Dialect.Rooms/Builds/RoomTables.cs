using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Every legacy build's room structures and play values, read from the packet tables under
/// <c>packets/</c>. A field's name says what the server fills it from: a name this plugin knows
/// (<see cref="RoomFieldValue"/> in snake case, or <c>seated</c>) takes that value, any other name is
/// written with the field's default. Adding a build means adding it to the tables: no code changes.
/// </summary>
internal sealed class RoomTables
{
    private const string SessionSetting = "SessionSetting";
    private const string GameSetting = "GameSetting";
    private const string UserSlot = "UserSlot";
    private const string EquippedItemList = "EquippedItemList";
    private const string GotoRoom = "gspGotoRoom";
    private const string ScoreRow = "ScoreRow";

    // The field types the scoreboard row writer knows.
    private static readonly HashSet<string> ScoreRowTypes = new(StringComparer.Ordinal) { "u8", "u16", "u32", "f32", "bool", "wstr", "list<wstr>" };

    // Values only a play room has; on the main menu their fields carry the row's `menu` value, or 0.
    private static readonly HashSet<RoomFieldValue> PlayOnly =
    [
        RoomFieldValue.SessionState, RoomFieldValue.RoomId, RoomFieldValue.Title, RoomFieldValue.Occupants, RoomFieldValue.OpenSeats,
        RoomFieldValue.HasPassword, RoomFieldValue.Occupied, RoomFieldValue.Closed, RoomFieldValue.Ready, RoomFieldValue.Team,
        RoomFieldValue.SpawnIndex, RoomFieldValue.Level, RoomFieldValue.MasterSlot, RoomFieldValue.LocalSlot,
    ];

    private static readonly Dictionary<string, RoomFieldValue> Roles = BuildRoles();

    private static readonly Dictionary<string, RoomFieldType> Types = new(StringComparer.Ordinal)
    {
        ["u8"] = RoomFieldType.U8, ["u16"] = RoomFieldType.U16, ["u32"] = RoomFieldType.U32, ["bool"] = RoomFieldType.Bool,
        ["f32"] = RoomFieldType.F32, ["f64"] = RoomFieldType.F64, ["wstr"] = RoomFieldType.WStr,
        ["empty_list"] = RoomFieldType.EmptyVector, ["list<u32>"] = RoomFieldType.Equipped,
        // Native validation can carry these vectors; room projections currently supply none of their values.
        ["list<u8>"] = RoomFieldType.EmptyVector, ["list<ItemAttr>"] = RoomFieldType.EmptyVector,
        ["list<ItemExpiredTime>"] = RoomFieldType.EmptyVector,
        [SessionSetting] = RoomFieldType.SessionSetting, [GameSetting] = RoomFieldType.GameSetting,
        ["list<UserSlot>"] = RoomFieldType.UserSlots, ["list<EquippedItemList>"] = RoomFieldType.EquippedItemLists,
    };

    private readonly Dictionary<string, (RoomSchema Schema, PlayValues Play)> _builds = new(StringComparer.Ordinal);

    private RoomTables(PacketTables tables) => Tables = tables;

    /// <summary>Gets the packet tables the builds were read from.</summary>
    public PacketTables Tables { get; }

    /// <summary>Gets each build's struct field tables, under the build's name.</summary>
    public BuildVariants<RoomSchema> Schemas { get; } = new("legacy main-menu room");

    /// <summary>Gets each build's tutorial and play-room row, under the build's name.</summary>
    public BuildVariants<PlayValues> Play { get; } = new("legacy tutorial and plaza rooms");

    /// <summary>Gets, per build, a line saying how much the tables state for it and whose values the rest are.</summary>
    public IReadOnlyList<string> Summary { get; private set; } = [];

    /// <summary>Gets one build's resolved tables.</summary>
    /// <param name="build">The build's name.</param>
    /// <returns>The struct tables and the play row.</returns>
    /// <exception cref="ConfigException">Thrown when the tables do not name the build.</exception>
    public (RoomSchema Schema, PlayValues Play) Of(string build) =>
        _builds.TryGetValue(build, out var resolved)
            ? resolved
            : throw new ConfigException($"The packets directory '{Tables.Directory}' does not name the build '{build}'.");

    /// <summary>Reads the packet tables and turns every build's room structures into field tables.</summary>
    /// <param name="packetsDirectory">The directory that holds <c>builds.yaml</c> and the structure files.</param>
    /// <returns>The resolved builds.</returns>
    /// <exception cref="ConfigException">
    /// Thrown for anything <see cref="PacketTables.Load"/> rejects, a field type this plugin cannot write,
    /// a <c>menu</c> value that is neither a number, a known name nor <c>empty</c>, or an unknown play value.
    /// </exception>
    public static RoomTables Load(string packetsDirectory)
    {
        var tables = PacketTables.Load(packetsDirectory);
        var builds = new RoomTables(tables);
        var summary = new List<string>();
        foreach (var build in tables.Builds)
        {
            var where = $"'{packetsDirectory}' build '{build}'";
            var values = tables.ValuesOf(build);
            // Feature values, including initial_tutorial_routes, are validated by their owning rows.
            if (values.Keys.FirstOrDefault(key => key is not ("needs_idle_party" or "play" or "capabilities" or "room_setting_change_fields" or "room_round_setting_fields" or "room_projection_fields" or "room_unchosen_map" or "room_unchosen_difficulty" or "room_setting_change_map" or "room_ai_seat" or "round_score_row_marker" or "round_feed" or "shop" or "lobby" or "login" or "inventory" or "equipment" or "family" or "initial_tutorial_routes")) is { } unknown)
                throw new ConfigException($"{where} has the unknown value '{unknown}'.");
            // A build of another family (its own value or its base's) is served by that family's room binding.
            if (values.TryGetValue("family", out var family))
            {
                if (family as string != "modern")
                    throw new ConfigException($"{where}: 'family' must be 'modern' when it is stated.");
                summary.Add($"{build}: the modern family, not served by this room writer");
                continue;
            }
            var needsIdleParty = !values.TryGetValue("needs_idle_party", out var rawParty) || (rawParty as string) switch
            {
                "true" => true,
                "false" => false,
                _ => throw new ConfigException($"{where}: 'needs_idle_party' must be true or false."),
            };
            var play = PlayValues.Of(tables, build);

            var schema = new RoomSchema(
                Fields(tables, build, GotoRoom, menu: true), Fields(tables, build, SessionSetting, menu: true),
                Fields(tables, build, GameSetting, menu: false), Fields(tables, build, UserSlot, menu: true),
                Fields(tables, build, EquippedItemList, menu: false))
            {
                Tables = tables,
                Build = build,
                MenuGameSetting = Fields(tables, build, GameSetting, menu: true),
                PlaySessionSetting = Fields(tables, build, SessionSetting, menu: false),
                PlayUserSlot = Fields(tables, build, UserSlot, menu: false),
                PlayGotoRoom = Fields(tables, build, GotoRoom, menu: false),
                NeedsIdleParty = needsIdleParty,
                ScoreRow = ScoreRowOf(tables, build),
            };
            builds._builds[build] = (schema, play);
            builds.Schemas.Add(build, schema);
            builds.Play.Add(build, play);

            var (rows, own) = tables.StatedRows(build);
            summary.Add(tables.BaseOf(build) is not { } parent
                ? $"{build}: the baseline"
                : $"{build}: {rows} field row(s){(own.Count > 0 ? $", {string.Join(" and ", own)} restated," : "")} and "
                    + $"{tables.StatedValues(build)} value(s) stated; the rest is {parent}'s");
        }
        builds.Summary = summary;
        return builds;
    }

    // One structure of one build as the writer's field table: the play table takes every known name from
    // the room, the menu table writes the play-only ones as constants.
    private static RoomField[] Fields(PacketTables tables, string build, string structure, bool menu)
    {
        var where = $"'{tables.Directory}' structure '{structure}' of '{build}'";
        return [.. tables.Resolve(build, structure).Select(field =>
        {
            if (!Types.TryGetValue(field.Type, out var type))
                throw new ConfigException($"{where}: field '{field.Name}' has the type '{field.Type}', which the room writer does not know.");
            var role = type is RoomFieldType.U8 or RoomFieldType.U16 or RoomFieldType.U32 or RoomFieldType.Bool or RoomFieldType.WStr
                ? Roles.GetValueOrDefault(field.Name, RoomFieldValue.Constant)
                : RoomFieldValue.Constant;
            if (!menu)
                return new RoomField(type, role, field.Default, field.Vacant, field.Name);
            if (field.Menu is { } text)
            {
                if (text == "empty")
                    return new RoomField(RoomFieldType.EmptyVector, Name: field.Name);
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var constant))
                    return new RoomField(type, RoomFieldValue.Constant, constant, Name: field.Name);
                return Roles.TryGetValue(text, out var named)
                    ? new RoomField(type, named, Name: field.Name)
                    : throw new ConfigException($"{where}: field '{field.Name}' has menu '{text}', which is not a number, a known name or 'empty'.");
            }
            if (PlayOnly.Contains(role))
                return new RoomField(type, Name: field.Name);
            return new RoomField(type, role, field.Default, Name: field.Name);
        })];
    }

    // The scoreboard row of one build, refused when it holds a type its writer cannot encode.
    private static IReadOnlyList<PacketField> ScoreRowOf(PacketTables tables, string build)
    {
        var fields = tables.Resolve(build, ScoreRow);
        if (fields.FirstOrDefault(field => !ScoreRowTypes.Contains(field.Type)) is { } unknown)
            throw new ConfigException($"'{tables.Directory}' structure '{ScoreRow}' of '{build}': field '{unknown.Name}' has the type "
                + $"'{unknown.Type}', which the scoreboard writer does not know.");
        return fields;
    }

    private static Dictionary<string, RoomFieldValue> BuildRoles()
    {
        var roles = Enum.GetValues<RoomFieldValue>().Where(value => value != RoomFieldValue.Constant)
            .ToDictionary(value => Snake(value.ToString()), StringComparer.Ordinal);
        // UserSlot +6 is set for a seated player together with +4; both take the occupied value.
        roles["seated"] = RoomFieldValue.Occupied;
        return roles;
    }

    private static string Snake(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));
}
