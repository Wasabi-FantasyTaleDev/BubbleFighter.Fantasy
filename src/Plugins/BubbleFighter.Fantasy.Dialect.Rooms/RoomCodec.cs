using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>How one room-session field is encoded.</summary>
internal enum RoomFieldType
{
    /// <summary>One byte (kr-812 <c>sub_A56BB0</c>).</summary>
    U8,

    /// <summary>A little-endian 16-bit integer.</summary>
    U16,

    /// <summary>A little-endian 32-bit integer.</summary>
    U32,

    /// <summary>The client's 4-byte boolean (kr-812 <c>sub_A56BD0</c>).</summary>
    Bool,

    /// <summary>A 32-bit float.</summary>
    F32,

    /// <summary>A 64-bit float.</summary>
    F64,

    /// <summary>A <c>u32</c> character count plus UTF-16LE.</summary>
    WStr,

    /// <summary>A <c>u32</c> count of zero: an empty vector of any element type.</summary>
    EmptyVector,

    /// <summary><c>vector&lt;u32&gt;</c> of the player's equipped item ids.</summary>
    Equipped,

    /// <summary>The nested <c>SessionSetting</c>.</summary>
    SessionSetting,

    /// <summary>The nested <c>GameSetting</c>.</summary>
    GameSetting,

    /// <summary><c>vector&lt;UserSlot&gt;</c> holding the local player alone.</summary>
    UserSlots,

    /// <summary><c>vector&lt;EquippedItemList&gt;</c> holding the local player alone.</summary>
    EquippedItemLists,
}

/// <summary>Where a room-session field's value comes from.</summary>
internal enum RoomFieldValue
{
    /// <summary>The field's constant.</summary>
    Constant,

    /// <summary>The player's user number, the identity the client matches to find itself.</summary>
    UserNumber,

    /// <summary>The player's nickname.</summary>
    Nickname,

    /// <summary>The character type (FighterConfig key): the character item id minus 300000.</summary>
    CharacterType,

    /// <summary>The avatar's material color index, independent of its team.</summary>
    ColorIndex,

    /// <summary>The character item id.</summary>
    CharacterItem,

    /// <summary>The session kind that opens the main menu.</summary>
    Kind,

    /// <summary>
    /// The <c>GameSetting</c> map key (cn-717 fourth opening wstr → room+188). Taken from the
    /// <c>WriteRoom</c> mapName argument; empty on the main-menu path.
    /// </summary>
    MapName,

    /// <summary>
    /// The <c>GameSetting</c> first opening wstr (cn-717 object +16 → room+104). PlayTutorial's
    /// Directory string; empty on the main-menu / select path.
    /// </summary>
    MapDirectory,

    /// <summary>
    /// The <c>GameSetting</c> second opening wstr (cn-717 object +44 → room+132). Modes
    /// 101/102/103 copy this slot in <c>sub_6EB170</c>; an empty value leaves MapMeta lookups
    /// keyed on "" and ACCESS_VIOLATIONs in <c>sub_B1DFE0</c> on the prologue start path.
    /// </summary>
    CurrentMap,

    /// <summary>
    /// Compatibility name for the third opening wstr at object+72 / room+160.
    /// KR2015's <c>kr-812 sub_40EE80</c> reads Theme here; the NPC Lua map getter
    /// <c>kr-812 sub_A128A0</c> instead copies room+132. Empty on the main-menu path.
    /// </summary>
    NpcTableKey,

    /// <summary>The room master's nickname, which the lobby list shows beside the title.</summary>
    MasterNickname,

    /// <summary>
    /// The <c>GameSetting</c> theme wstr of the map dialog's tab the map sits under (cn-324 object +24 → room+64,
    /// matched against the client's theme list by <c>cn-324 sub_59DE30</c>). Sent empty by the server's own tables.
    /// </summary>
    MapTheme,

    /// <summary>
    /// The <c>GameSetting</c> mode u32 at object +8 (cn-717 room+96). Taken from the
    /// <c>WriteRoom</c> mode argument; zero on the main-menu path, 103 on cn-717 tutorial play.
    /// </summary>
    Mode,

    /// <summary>The room number, in a play room's <c>SessionSetting</c>.</summary>
    RoomId,

    /// <summary>The room title, in a play room's <c>SessionSetting</c>.</summary>
    Title,

    /// <summary>The number of seated players, in a play room's <c>SessionSetting</c>.</summary>
    Occupants,

    /// <summary>The number of seats that are not closed, in a play room's <c>SessionSetting</c>.</summary>
    OpenSeats,

    /// <summary>The leading <c>SessionSetting</c> value: 2 while seated in a play room, 0 otherwise.</summary>
    SessionState,

    /// <summary>Whether joining the room needs a password, <c>SessionSetting</c> +46.</summary>
    HasPassword,

    /// <summary>Whether a player sits in the slot.</summary>
    Occupied,

    /// <summary>Whether the room master closed the empty slot.</summary>
    Closed,

    /// <summary>The seated player's ready mark.</summary>
    Ready,

    /// <summary>The seated player's team.</summary>
    Team,

    /// <summary>The slot's start position index, <c>UserSlot</c> +129: 0xFF until the round is set up.</summary>
    SpawnIndex,

    /// <summary>The seated player's level.</summary>
    Level,

    /// <summary>The slot index of the room master, <c>gspGotoRoom</c> +680.</summary>
    MasterSlot,

    /// <summary>The receiving player's own slot index, <c>gspGotoRoom</c> +684.</summary>
    LocalSlot,

    /// <summary>The room install belongs to a party scene transition.</summary>
    PartyTransition,

    /// <summary>
    /// The <c>GameSetting</c> mission difficulty u32 at object +164 (cn-717 room+252). Fifth of
    /// the seven u32 after the two bools in <c>sub_41A510</c>. PlayTutorial's trailing u32;
    /// zero leaves wave Lua with a nil <c>TableWave2Level</c> row (no prologue groups).
    /// </summary>
    Difficulty,
}

/// <summary>One field of a room-session structure, in wire order.</summary>
/// <param name="Type">The encoding.</param>
/// <param name="Value">The value's source.</param>
/// <param name="Constant">The value when <paramref name="Value"/> is <see cref="RoomFieldValue.Constant"/>.</param>
/// <param name="Vacant">The constant an empty slot carries instead, when it differs from <paramref name="Constant"/>.</param>
/// <param name="Name">The field's name in the packet tables, for messages; empty for a field built in code.</param>
internal readonly record struct RoomField(RoomFieldType Type, RoomFieldValue Value = RoomFieldValue.Constant, long Constant = 0, long? Vacant = null,
    string Name = "");

/// <summary>One slot of a room as the writer needs it: a seated player, or an empty slot.</summary>
/// <param name="Occupied">Whether a player sits here; the remaining values are ignored otherwise.</param>
/// <param name="Closed">Whether the empty slot is closed.</param>
/// <param name="UserNumber">The player's user number.</param>
/// <param name="Nickname">The player's nickname.</param>
/// <param name="CharacterItemId">The player's character item.</param>
/// <param name="EquippedItemIds">The player's worn items.</param>
/// <param name="Ready">The player's ready mark.</param>
internal sealed record RoomSlotValues(bool Occupied, bool Closed, int UserNumber, string Nickname, int CharacterItemId,
    IReadOnlyList<int> EquippedItemIds, bool Ready)
{
    /// <summary>Gets the persisted avatar material color index; zero keeps the native default.</summary>
    public ushort ColorIndex { get; init; }

    /// <summary>Gets the player's team byte.</summary>
    public int Team { get; init; }

    /// <summary>Gets the start position index; 0xFF outside a starting round.</summary>
    public int SpawnIndex { get; init; } = 0xFF;

    /// <summary>Gets the player's level.</summary>
    public int Level { get; init; } = 1;

    /// <summary>The character item an empty slot carries.</summary>
    public const int VacantCharacterItem = 300000;

    /// <summary>
    /// Gets whether an unoccupied slot still carries a player's record, as the slot a player just
    /// left does: the constants of a seated player, without the occupied marks.
    /// </summary>
    public bool Departed { get; init; }

    /// <summary>An empty slot.</summary>
    /// <param name="closed">Whether the master closed it.</param>
    public static RoomSlotValues Empty(bool closed) => new(false, closed, 0, "", VacantCharacterItem, [], false);
}

/// <summary>Everything the room writer takes its non-constant values from.</summary>
/// <param name="Kind">The <c>SessionSetting</c> kind.</param>
/// <param name="Slots">The slots written into the <c>UserSlot</c> and <c>EquippedItemList</c> vectors, in slot order.</param>
internal sealed record RoomValues(int Kind, IReadOnlyList<RoomSlotValues> Slots)
{
    /// <summary>Gets the <c>GameSetting</c> mode.</summary>
    public int Mode { get; init; }

    /// <summary>Gets the <c>GameSetting</c> map key.</summary>
    public string MapName { get; init; } = "";

    /// <summary>Gets the <c>GameSetting</c> first opening wstr.</summary>
    public string MapDirectory { get; init; } = "";

    /// <summary>Gets the <c>GameSetting</c> second opening wstr.</summary>
    public string CurrentMap { get; init; } = "";

    /// <summary>Gets the third opening wstr under its compatibility name; KR2015 uses a map theme.</summary>
    public string NpcTableKey { get; init; } = "";

    /// <summary>Gets the room master's nickname for the lobby list row; empty outside the list.</summary>
    public string MasterNickname { get; init; } = "";

    /// <summary>Gets the <c>GameSetting</c> mission difficulty.</summary>
    public int Difficulty { get; init; }

    /// <summary>Gets whether the play-room tables are used instead of the main-menu ones.</summary>
    public bool Play { get; init; }

    /// <summary>Gets the room number.</summary>
    public int RoomId { get; init; }

    /// <summary>Gets the room title.</summary>
    public string Title { get; init; } = "";

    /// <summary>Gets the leading <c>SessionSetting</c> value.</summary>
    public int SessionState { get; init; }

    /// <summary>Gets the seated-player count when no slots are written, as on a list row; otherwise the slots are counted.</summary>
    public int? Occupants { get; init; }

    /// <summary>Gets whether joining needs a password.</summary>
    public bool HasPassword { get; init; }

    /// <summary>Gets the room master's slot index.</summary>
    public int MasterSlot { get; init; }

    /// <summary>Gets the receiving player's slot index.</summary>
    public int LocalSlot { get; init; }

    /// <summary>Gets whether the room install displays the native party transition notice.</summary>
    public bool PartyTransition { get; init; }

    /// <summary>Gets the number of slots that are not closed.</summary>
    public int OpenSeats { get; init; }

    /// <summary>
    /// Gets an already encoded <c>GameSetting</c> written in place of the table, or empty. A play room
    /// returns the block its creator's client sent, so every setting the dialog chose is kept.
    /// </summary>
    public ReadOnlyMemory<byte> GameSetting { get; init; }
}

/// <summary>
/// A legacy build's main-menu room session as field tables, one per structure, in the order
/// the build's readers consume them. Builds differ only in these tables; the writer is shared.
/// </summary>
/// <param name="GotoRoom">The <c>gspGotoRoom</c> body.</param>
/// <param name="SessionSetting">The <c>SessionSetting</c> fields.</param>
/// <param name="GameSetting">The <c>GameSetting</c> fields.</param>
/// <param name="UserSlot">The <c>UserSlot</c> fields.</param>
/// <param name="EquippedItemList">The <c>EquippedItemList</c> fields.</param>
internal sealed record RoomSchema(
    RoomField[] GotoRoom, RoomField[] SessionSetting, RoomField[] GameSetting, RoomField[] UserSlot, RoomField[] EquippedItemList)
{
    /// <summary>Gets the source tables used to resolve nested native structures without recreating field types.</summary>
    public required PacketTables Tables { get; init; }

    /// <summary>Gets the build whose source tables produced this room schema.</summary>
    public required string Build { get; init; }

    /// <summary>Gets explicit main-menu defaults for the nested game settings, when supplied by the tables.</summary>
    public RoomField[]? MenuGameSetting { get; init; }

    /// <summary>Gets the <c>SessionSetting</c> fields of a play room, or null when the build's play rooms are not decoded.</summary>
    public RoomField[]? PlaySessionSetting { get; init; }

    /// <summary>Gets the <c>UserSlot</c> fields of a play room, with the constants an empty slot carries.</summary>
    public RoomField[]? PlayUserSlot { get; init; }

    /// <summary>Gets the <c>gspGotoRoom</c> fields of a play room where they differ from the main menu's, or null.</summary>
    public RoomField[]? PlayGotoRoom { get; init; }

    /// <summary>
    /// Gets whether the main-menu plaza gate needs the solo idle <c>gspUpdateParty</c> before
    /// <c>gcpReqGotoLobby</c> can leave. Every legacy build's does unless shown otherwise.
    /// </summary>
    public bool NeedsIdleParty { get; init; } = true;

    /// <summary>Gets the <c>ScoreRow</c> fields of the build: one seat of the scoreboard, in wire order.</summary>
    public IReadOnlyList<PacketField> ScoreRow { get; init; } = [];
}

/// <summary>
/// The legacy main menu's room session: S2C 110 <c>gspGotoRoom</c> (kr-812 handler
/// <c>sub_820060</c>) written from a build's <see cref="RoomSchema"/>, and S2C 108
/// <c>gspGotoMenu</c>.
/// </summary>
internal static class RoomCodec
{
    /// <summary>The first character item; a character's type index is its item id minus this base.</summary>
    private const int CharacterItemBase = 300000;

    /// <summary>
    /// The session kind of the main menu's character session. <c>sub_820060</c> switches on
    /// <c>SessionSetting</c> +76 only once the client is past stage 2: kind 1 moves to stage 2,
    /// <c>MainMenuStage</c> (the stage factory <c>sub_8E1240</c> maps 2 to <c>sub_8DDFE0</c>),
    /// and kind 2 to stage 4, a room. Right after login the switch is skipped, so the session
    /// is only built and <c>gspGotoMenu</c> (kr-812 handler <c>sub_81FDA0</c>) does the move.
    /// </summary>
    public const int MainMenuKind = 1;

    /// <summary>
    /// One-seat play room. <c>sub_820060</c> kind 2 moves to stage 4 (<c>RoomStage8vs8</c>);
    /// the tutorial start path uses this after <c>gcpReqPlayTutorial</c>.
    /// </summary>
    public const int PlayRoomKind = 2;

    /// <summary>
    /// Tutorial stage-select scene. <c>sub_820060</c> kind 5 calls <c>SetStage(8)</c>; the stage
    /// factory <c>sub_8E1240</c> case 8 builds <c>TutorialRoomStage</c> (<c>sub_6B36C0</c>).
    /// </summary>
    public const int TutorialSelectKind = 5;

    /// <summary>
    /// Writes the <c>gspGotoMenu</c>/<c>gspGotoLobby</c> scene transition and its party notice flag.
    /// kr-812 sub_80C160 reads one bool32; kr-812 sub_81FDA0 and kr-812 sub_81FF00 consume it.
    /// </summary>
    /// <param name="tables">The native packet layouts.</param>
    /// <param name="build">The selected client build.</param>
    /// <param name="partyTransition">Whether to display the party transition notice.</param>
    /// <returns>The four-byte scene transition body.</returns>
    public static byte[] WriteSceneTransition(PacketTables tables, string build, bool partyTransition = false) =>
        PacketCodec.Write(tables, build, "RoomSceneTransition", new Dictionary<string, object?> { ["notice"] = partyTransition });

    /// <summary>
    /// Writes <c>gspGotoRoom</c> for the main menu with the local player alone. Mode and map
    /// strings stay zero / empty so login never carries the tutorial <c>wgtHordeTutor</c>
    /// mode 103 or a PlayTutorial MapMeta row name.
    /// </summary>
    /// <param name="room">The opened main-menu room.</param>
    /// <param name="schema">The build's room layout.</param>
    /// <returns>The S2C 110 body.</returns>
    public static byte[] WriteMainMenu(MainMenuRoomOpened room, RoomSchema schema) =>
        WriteRoom(room, schema, MainMenuKind, mode: 0, mapName: "");

    /// <summary>
    /// Writes <c>gspGotoRoom</c> kind 5 for <c>TutorialRoomStage</c>. Mode and map strings stay
    /// zero / empty: the select scene must not carry PlayTutorial's mode 103 or MapMeta row
    /// (cn-717 <c>sub_8A6D50</c> case 5 only <c>SetStage(8)</c>; seeding mode/map here sent
    /// the client into the same null-MapMeta path as a bad EnterGame).
    /// </summary>
    /// <param name="room">The player occupying the sole seat.</param>
    /// <param name="schema">The build's room layout.</param>
    /// <returns>The S2C GotoRoom body with kind 5.</returns>
    public static byte[] WriteTutorialSelect(MainMenuRoomOpened room, RoomSchema schema) =>
        WriteRoom(room, schema, TutorialSelectKind, mode: 0, mapName: "");

    /// <summary>Writes <c>gspGotoRoom</c> for one local player with a chosen session kind.</summary>
    /// <param name="room">The player occupying the sole seat.</param>
    /// <param name="schema">The build's room layout.</param>
    /// <param name="kind"><c>SessionSetting</c> kind at the build's kind field.</param>
    /// <param name="mode">
    /// <c>GameSetting</c> mode at object +8 (cn-717 room+96). Ignored for kind 1 and kind 5
    /// (forced to 0). Cn-717 tutorial play after <c>gcpReqPlayTutorial</c> passes 103 on kind 2
    /// (<c>wgtHordeTutor</c>); bare <c>sub_6B9310</c> builds <c>NpcGameContext</c> for that mode.
    /// </param>
    /// <param name="mapName">
    /// <c>GameSetting</c> map key (cn-717 fourth opening wstr → room+188). Ignored for kind 1
    /// and kind 5 (forced empty). Tutorial play on kind 2 passes the PlayTutorial MapMeta row.
    /// </param>
    /// <param name="mapDirectory">
    /// <c>GameSetting</c> first opening wstr (cn-717 +16 → room+104). PlayTutorial Directory;
    /// forced empty on kind 1 / 5.
    /// </param>
    /// <param name="currentMap">
    /// <c>GameSetting</c> second opening wstr (cn-717 +44 → room+132). Required non-empty for
    /// modes 101/102/103 (<c>sub_6EB170</c>); forced empty on kind 1 / 5.
    /// </param>
    /// <param name="difficulty">
    /// <c>GameSetting</c>+164 (cn-717 room+252). PlayTutorial trailing u32 on kind 2; forced 0
    /// on kind 1 / 5.
    /// </param>
    /// <param name="npcTableKey">
    /// <c>GameSetting</c> third opening wstr (room+160), retained under its compatibility
    /// parameter name; <c>kr-812 sub_40EE80</c> reads Theme. Forced empty on kind 1 / 5.
    /// </param>
    /// <returns>The S2C 110 body.</returns>
    public static byte[] WriteRoom(
        MainMenuRoomOpened room,
        RoomSchema schema,
        int kind,
        int mode = 0,
        string mapName = "",
        string mapDirectory = "",
        string currentMap = "",
        int difficulty = 0,
        string npcTableKey = "")
    {
        // Main menu and tutorial select share the zero mode / empty map strings. Only kind 2
        // (and other play kinds) may carry PlayTutorial's mode 103 + Directory / CurrentMap / key.
        if (kind is MainMenuKind or TutorialSelectKind)
        {
            mode = 0;
            mapName = "";
            mapDirectory = "";
            currentMap = "";
            difficulty = 0;
            npcTableKey = "";
        }

        return WriteRoom(schema, new RoomValues(kind, [Seated(room, ready: false)])
        {
            Mode = mode, MapName = mapName, MapDirectory = mapDirectory, CurrentMap = currentMap, Difficulty = difficulty,
            NpcTableKey = npcTableKey,
        });
    }

    /// <summary>Writes <c>gspGotoRoom</c> from explicit room values; play rooms carry every slot.</summary>
    /// <param name="schema">The build's room layout.</param>
    /// <param name="values">The room and its slots.</param>
    /// <returns>The S2C 110 body.</returns>
    public static byte[] WriteRoom(RoomSchema schema, RoomValues values)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        Write(new PacketWriter(buffer), values.Play && schema.PlayGotoRoom is { } play ? play : schema.GotoRoom, schema, values, null, legacyReady: false);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Writes one <c>UserSlot</c> then one <c>EquippedItemList</c>, the body of a slot update.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="room">The player occupying the slot.</param>
    /// <param name="schema">The build's room layout.</param>
    internal static void WriteSlotAndItems(PacketWriter w, MainMenuRoomOpened room, RoomSchema schema)
    {
        WriteUserSlot(w, room, schema, ready: false);
        WriteEquippedItemList(w, room, schema);
    }

    /// <summary>
    /// Writes one <c>UserSlot</c>. When <paramref name="ready"/> is set, the bool at object
    /// +125 (kr-812 reader <c>sub_4196F0</c>) is forced true; that is the seat ready flag.
    /// </summary>
    /// <param name="w">The writer.</param>
    /// <param name="room">The player occupying the slot.</param>
    /// <param name="schema">The build's room layout.</param>
    /// <param name="ready">Seat ready flag at UserSlot +125.</param>
    internal static void WriteUserSlot(PacketWriter w, MainMenuRoomOpened room, RoomSchema schema, bool ready)
    {
        var slot = Seated(room, ready);
        Write(w, schema.UserSlot, schema, new RoomValues(MainMenuKind, [slot]), slot, legacyReady: ready);
    }

    /// <summary>Writes one <c>EquippedItemList</c> for the local player.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="room">The player occupying the slot.</param>
    /// <param name="schema">The build's room layout.</param>
    internal static void WriteEquippedItemList(PacketWriter w, MainMenuRoomOpened room, RoomSchema schema)
    {
        var slot = Seated(room, ready: false);
        Write(w, schema.EquippedItemList, schema, new RoomValues(MainMenuKind, [slot]), slot, legacyReady: false);
    }

    /// <summary>Writes one play-room <c>UserSlot</c> then its <c>EquippedItemList</c>.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="schema">The build's room layout.</param>
    /// <param name="slot">The slot's values.</param>
    internal static void WritePlaySlotAndItems(PacketWriter w, RoomSchema schema, RoomSlotValues slot)
    {
        WritePlayUserSlot(w, schema, slot);
        Write(w, schema.EquippedItemList, schema, PlayOf(slot), slot, legacyReady: false);
    }

    /// <summary>
    /// Writes one seat's match-start push (<c>gspRoomSetSlot</c>: slot index, play-room
    /// <c>UserSlot</c>, <c>EquippedItemList</c>). The start burst carries one per seat
    /// before <c>gspRoomEnterGame</c>; the client takes its own seat index from it, and the test
    /// that lets the host build its spawn group compares that seat with the room master's.
    /// </summary>
    /// <param name="room">The seat's occupant.</param>
    /// <param name="schema">The build's room layout.</param>
    /// <param name="tables">The native packet composition tables.</param>
    /// <param name="build">The client build whose nested records are encoded.</param>
    /// <param name="slot">The seat index, which is also the start position index.</param>
    /// <param name="team">The occupant's team, used by tutorial maps to select their spawn group.</param>
    /// <returns>The S2C 503 body.</returns>
    internal static byte[] WritePlaySlot(MainMenuRoomOpened room, PacketTables tables, string build,
        RoomSchema schema, int slot, int team = 0) =>
        WriteSeatEnvelope(tables, build, schema, slot, Seated(room, ready: false) with { SpawnIndex = slot, Team = team });

    /// <summary>Writes one play-room <c>UserSlot</c>.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="schema">The build's room layout.</param>
    /// <param name="slot">The slot's values.</param>
    internal static void WritePlayUserSlot(PacketWriter w, RoomSchema schema, RoomSlotValues slot) =>
        Write(w, PlayTable(schema.PlayUserSlot), schema, PlayOf(slot), slot, legacyReady: false);

    /// <summary>
    /// Writes a <c>SessionSetting</c> (with nested <c>GameSetting</c>) using the build schema.
    /// Used by plaza CreateSession round-trip pins.
    /// </summary>
    /// <param name="w">The writer.</param>
    /// <param name="room">Identity for nickname / character fields inside nested slots if any.</param>
    /// <param name="schema">The build's room layout.</param>
    /// <param name="kind"><c>SessionSetting</c> kind field.</param>
    internal static void WriteSessionSetting(PacketWriter w, MainMenuRoomOpened room, RoomSchema schema, int kind) =>
        Write(w, schema.SessionSetting, schema, new RoomValues(kind, [Seated(room, ready: false)]), null, legacyReady: false);

    /// <summary>Writes the build's <c>GameSetting</c> table with its constants.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="schema">The build's room layout.</param>
    internal static void WriteGameSetting(PacketWriter w, RoomSchema schema) =>
        Write(w, schema.GameSetting, schema, new RoomValues(PlayRoomKind, []) { Play = true }, null, legacyReady: false);

    /// <summary>Writes the <c>gspRoomChangeGameSetting</c> body: the build's <c>GameSetting</c> from explicit values.</summary>
    /// <param name="schema">The build's room layout.</param>
    /// <param name="values">The room values the change carries (mode, map strings, difficulty, npc table key).</param>
    /// <returns>The S2C 505 body.</returns>
    public static byte[] WriteGameSettingChange(RoomSchema schema, RoomValues values)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        Write(new PacketWriter(buffer), schema.GameSetting, schema, values, null, legacyReady: false);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Writes a <c>SessionSetting</c> from explicit room values.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="schema">The build's room layout.</param>
    /// <param name="values">The room.</param>
    internal static void WriteSessionSetting(PacketWriter w, RoomSchema schema, RoomValues values) =>
        Write(w, values.Play ? PlayTable(schema.PlaySessionSetting) : schema.SessionSetting, schema, values, null, legacyReady: false);

    private static RoomSlotValues Seated(MainMenuRoomOpened room, bool ready) =>
        new(true, false, room.UserNumber, room.Nickname, room.CharacterItemId, room.EquippedItemIds, ready) { ColorIndex = room.ColorIndex };

    private static RoomValues PlayOf(RoomSlotValues slot) => new(PlayRoomKind, [slot]) { Play = true };

    private static RoomField[] PlayTable(RoomField[]? table) =>
        table ?? throw new InvalidOperationException("This build's play-room layout is not decoded.");

    /// <summary>Writes the complete native room envelope with separately scoped nested values.</summary>
    internal static byte[] WriteRoomEnvelope(PacketTables tables, string build, RoomSchema schema, RoomValues room) =>
        PacketCodec.Write(tables, build, "gspGotoRoom",
            FieldValues(room.Play && schema.PlayGotoRoom is { } play ? play : schema.GotoRoom, schema, room, null));

    /// <summary>Writes a sole player's room while keeping menu and tutorial-select map fields empty.</summary>
    internal static byte[] WriteSingleRoomEnvelope(MainMenuRoomOpened player, PacketTables tables, string build,
        RoomSchema schema, int kind, int mode = 0, string mapName = "", string mapDirectory = "",
        string currentMap = "", int difficulty = 0, string npcTableKey = "")
    {
        var neutral = kind is MainMenuKind or TutorialSelectKind;
        return WriteRoomEnvelope(tables, build, schema, new RoomValues(kind, [Seated(player, ready: false)])
        {
            Mode = neutral ? 0 : mode, MapName = neutral ? "" : mapName,
            MapDirectory = neutral ? "" : mapDirectory, CurrentMap = neutral ? "" : currentMap,
            Difficulty = neutral ? 0 : difficulty, NpcTableKey = neutral ? "" : npcTableKey,
        });
    }

    /// <summary>Writes the native main-menu session before its stage builds the local avatar.</summary>
    internal static byte[] WriteMainMenuEnvelope(MainMenuRoomOpened player, PacketTables tables, string build,
        RoomSchema schema) => WriteSingleRoomEnvelope(player, tables, build, schema, MainMenuKind);

    /// <summary>Writes standalone game settings from explicit room values through the native table.</summary>
    internal static byte[] WriteGameSettingEnvelope(PacketTables tables, string build, RoomSchema schema, RoomValues room) =>
        PacketCodec.Write(tables, build, "GameSetting", FieldValues(schema.GameSetting, schema, room, null));

    /// <summary>Writes one standalone session setting with its menu or play projection.</summary>
    internal static byte[] WriteSessionEnvelope(PacketTables tables, string build, RoomSchema schema, RoomValues room) =>
        PacketCodec.Write(tables, build, "SessionSetting",
            FieldValues(room.Play ? PlayTable(schema.PlaySessionSetting) : schema.SessionSetting, schema, room, null));

    /// <summary>Projects a play-room list row, including its host-supplied native game settings.</summary>
    internal static Dictionary<string, object?> PlaySessionValues(RoomSchema schema, RoomValues room) =>
        FieldValues(PlayTable(schema.PlaySessionSetting), schema, room, null);

    private static Dictionary<string, object?> FieldValues(RoomField[] fields, RoomSchema schema,
        RoomValues room, RoomSlotValues? slot, bool legacyReady = false)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var forceReady = legacyReady && field.Name == "ready" && field.Type == RoomFieldType.Bool;
            values.Add(field.Name, field.Type switch
            {
                RoomFieldType.SessionSetting => FieldValues(room.Play ? PlayTable(schema.PlaySessionSetting) : schema.SessionSetting,
                    schema, room, null),
                RoomFieldType.UserSlots => room.Slots.Select(each => FieldValues(
                    room.Play ? PlayTable(schema.PlayUserSlot) : schema.UserSlot, schema, room, each)).ToArray(),
                RoomFieldType.EquippedItemLists => room.Slots.Select(each => FieldValues(
                    schema.EquippedItemList, schema, room, each)).ToArray(),
                RoomFieldType.GameSetting => room.GameSetting.IsEmpty
                    ? FieldValues(room.Kind == MainMenuKind ? schema.MenuGameSetting ?? schema.GameSetting : schema.GameSetting,
                        schema, room, null)
                    : ReadGameSettingValues(schema, room.GameSetting.Span),
                RoomFieldType.WStr => StringValue(field, room, slot),
                RoomFieldType.Equipped => slot?.EquippedItemIds ?? [],
                RoomFieldType.EmptyVector => null,
                RoomFieldType.Bool => NumericValue(field, room, slot, forceReady) != 0,
                RoomFieldType.U32 => unchecked((int)NumericValue(field, room, slot, forceReady)),
                RoomFieldType.U8 => checked((byte)NumericValue(field, room, slot, forceReady)),
                RoomFieldType.U16 => checked((ushort)NumericValue(field, room, slot, forceReady)),
                RoomFieldType.F32 or RoomFieldType.F64 => NumericValue(field, room, slot, forceReady),
                _ => throw new InvalidOperationException($"Unexpected room field {field.Name}: {field.Type}."),
            });
        }
        return values;
    }

    /// <summary>Reads the already audited native game-setting fields for nested table composition.</summary>
    internal static Dictionary<string, object?> ReadGameSettingValues(RoomSchema schema, ReadOnlySpan<byte> body) =>
        PacketCodec.Read(schema.Tables, schema.Build, "GameSetting", body,
            new PacketReadLimits(MaxStringCharacters: (ushort.MaxValue - 4) / 2), requireCanonicalBooleans: true);

    /// <summary>Projects play-seat values by name without encoding native field order or widths.</summary>
    internal static Dictionary<string, object?> PlaySeatValues(RoomSchema schema, RoomSlotValues slot, bool equipment) =>
        FieldValues(equipment ? schema.EquippedItemList : PlayTable(schema.PlayUserSlot), schema, PlayOf(slot), slot);

    /// <summary>Projects a seat with the main-menu <c>UserSlot</c> values (the tables' <c>menu</c> annotations).</summary>
    internal static Dictionary<string, object?> MenuSeatValues(RoomSchema schema, RoomSlotValues slot) =>
        FieldValues(schema.UserSlot, schema, new RoomValues(MainMenuKind, [slot]), slot);

    /// <summary>Writes a menu seat through the shared native envelope with explicit menu field values.</summary>
    internal static byte[] WriteMenuSeatEnvelope(PacketTables tables, string build, RoomSchema schema,
        MainMenuRoomOpened player, int index, bool ready)
    {
        var slot = Seated(player, ready);
        var room = new RoomValues(MainMenuKind, [slot]);
        return PacketCodec.Write(tables, build, "RoomSeatUpdate", new Dictionary<string, object?>
        {
            ["slot"] = index,
            ["user"] = FieldValues(schema.UserSlot, schema, room, slot, legacyReady: ready),
            ["equipment"] = FieldValues(schema.EquippedItemList, schema, room, slot),
        });
    }

    /// <summary>Writes the native seat envelope with separately scoped user and equipment values.</summary>
    internal static byte[] WriteSeatEnvelope(PacketTables tables, string build, RoomSchema schema,
        int index, RoomSlotValues slot, bool leaving = false) =>
        PacketCodec.Write(tables, build, leaving ? "RoomSeatLeave" : "RoomSeatUpdate", new Dictionary<string, object?>
        {
            ["slot"] = index,
            ["user"] = PlaySeatValues(schema, slot, equipment: false),
            ["equipment"] = PlaySeatValues(schema, slot, equipment: true),
        });

    private static long NumericValue(RoomField field, RoomValues room, RoomSlotValues? slot, bool forceReady) =>
        field.Value switch
        {
            RoomFieldValue.Constant => forceReady ? 1 : (slot is null || slot.Occupied || slot.Departed) ? field.Constant : field.Vacant ?? field.Constant,
            RoomFieldValue.UserNumber => slot?.UserNumber ?? 0,
            RoomFieldValue.CharacterType => slot is not null && (slot.Occupied || slot.Departed) ? slot.CharacterItemId - CharacterItemBase : 0,
            RoomFieldValue.ColorIndex => slot is not null && (slot.Occupied || slot.Departed) ? slot.ColorIndex : 0,
            RoomFieldValue.CharacterItem => slot?.CharacterItemId ?? RoomSlotValues.VacantCharacterItem,
            RoomFieldValue.Kind => room.Kind,
            RoomFieldValue.Mode => room.Mode,
            RoomFieldValue.Difficulty => room.Difficulty,
            RoomFieldValue.RoomId => room.RoomId,
            RoomFieldValue.Occupants => room.Occupants ?? room.Slots.Count(candidate => candidate.Occupied),
            RoomFieldValue.OpenSeats => room.OpenSeats,
            RoomFieldValue.SessionState => room.SessionState,
            RoomFieldValue.HasPassword => room.HasPassword ? 1 : 0,
            RoomFieldValue.Occupied => slot is { Occupied: true } ? 1 : 0,
            RoomFieldValue.Closed => slot is { Closed: true } ? 1 : 0,
            RoomFieldValue.Ready => slot is { Occupied: true, Ready: true } ? 1 : 0,
            RoomFieldValue.Team => slot is not null && (slot.Occupied || slot.Departed) ? slot.Team : 0,
            RoomFieldValue.SpawnIndex => slot?.SpawnIndex ?? 0xFF,
            RoomFieldValue.Level => slot is not null && (slot.Occupied || slot.Departed) ? slot.Level : field.Vacant ?? 0,
            RoomFieldValue.MasterSlot => room.MasterSlot,
            RoomFieldValue.LocalSlot => room.LocalSlot,
            RoomFieldValue.PartyTransition => room.PartyTransition ? 1 : 0,
            RoomFieldValue.Nickname or RoomFieldValue.MapName or RoomFieldValue.MapDirectory
                or RoomFieldValue.CurrentMap or RoomFieldValue.NpcTableKey or RoomFieldValue.MasterNickname
                or RoomFieldValue.MapTheme or RoomFieldValue.Title => 0,
            _ => throw new InvalidOperationException($"Unknown room field value {field.Value}."),
        };

    private static string StringValue(RoomField field, RoomValues room, RoomSlotValues? slot) =>
        field.Value switch
        {
            RoomFieldValue.Nickname => slot?.Nickname ?? "",
            RoomFieldValue.MapName => room.MapName,
            RoomFieldValue.MapDirectory => room.MapDirectory,
            RoomFieldValue.CurrentMap => room.CurrentMap,
            RoomFieldValue.NpcTableKey => room.NpcTableKey,
            RoomFieldValue.MasterNickname => room.MasterNickname,
            RoomFieldValue.Title => room.Title,
            _ => "",
        };

    private static void Write(PacketWriter w, RoomField[] fields, RoomSchema schema, RoomValues room,
        RoomSlotValues? slot, bool legacyReady)
    {
        // Menu defaults clear the ready role; the explicit fallback update restores only the named flag.
        // kr-812 sub_4196F0 reads the ready flag at UserSlot +125.
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            var value = NumericValue(field, room, slot, legacyReady && field.Name == "ready" && field.Type == RoomFieldType.Bool);
            switch (field.Type)
            {
                case RoomFieldType.U8:
                    w.WriteByte(checked((byte)value));
                    break;
                case RoomFieldType.U16:
                    w.WriteUInt16(checked((ushort)value));
                    break;
                case RoomFieldType.U32:
                    w.WriteInt32(unchecked((int)value));
                    break;
                case RoomFieldType.Bool:
                    w.WriteBool32(value != 0);
                    break;
                case RoomFieldType.F32:
                    w.WriteSingle(value);
                    break;
                case RoomFieldType.F64:
                    w.WriteDouble(value);
                    break;
                case RoomFieldType.WStr:
                    w.WriteWideStringPrefixed(StringValue(field, room, slot));
                    break;
                case RoomFieldType.EmptyVector:
                    w.WriteInt32(0);
                    break;
                case RoomFieldType.Equipped:
                    var items = slot?.EquippedItemIds ?? [];
                    w.WriteInt32(items.Count);
                    foreach (var item in items)
                        w.WriteInt32(item);
                    break;
                case RoomFieldType.SessionSetting:
                    Write(w, room.Play ? PlayTable(schema.PlaySessionSetting) : schema.SessionSetting, schema, room, null, legacyReady: false);
                    break;
                case RoomFieldType.GameSetting:
                    if (room.GameSetting.IsEmpty)
                        Write(w, room.Kind == MainMenuKind ? schema.MenuGameSetting ?? schema.GameSetting : schema.GameSetting,
                            schema, room, null, legacyReady: false);
                    else
                        w.WriteBytes(room.GameSetting.Span);
                    break;
                case RoomFieldType.UserSlots:
                    w.WriteInt32(room.Slots.Count);
                    foreach (var each in room.Slots)
                        Write(w, room.Play ? PlayTable(schema.PlayUserSlot) : schema.UserSlot, schema, room, each, legacyReady: false);
                    break;
                case RoomFieldType.EquippedItemLists:
                    w.WriteInt32(room.Slots.Count);
                    foreach (var each in room.Slots)
                        Write(w, schema.EquippedItemList, schema, room, each, legacyReady: false);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown room field type {field.Type}.");
            }
        }
    }
}
