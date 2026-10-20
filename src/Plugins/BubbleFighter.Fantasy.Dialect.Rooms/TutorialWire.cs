using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Tutorial scene policy; request layouts are selected by the packet tables.
/// </summary>
/// <param name="OldMapKey">
/// The map the server starts when the request's map key is empty: cn-279's second tutorial
/// entry (Shift-click in the select flow, cn-279 sub_6F8B20) sends the smaller request without
/// a map, and the old-tutorial map rel_Tutorial_01 is the server's choice. Empty keeps the
/// empty-key refusal.
/// </param>
/// <param name="SelectKind">
/// The <c>gspGotoRoom</c> kind the create-tutorial reply sends: 5 opens the tutorial select
/// scene; cn-279's table also routes kind 8 to the TutorialRoomStage factory
/// (<c>cn-279 sub_6DA4E0</c> case 8 -> sub_787C20), the dedicated tutorial-room stage whose UI
/// (<c>Ui/TutorialRoomStage01.ui</c>) the 2011/2012 clients still ship.
/// </param>
/// <param name="SkipEnter">Live bisection switch: stop the start burst before gspRoomEnterGame.</param>
/// <param name="OldMode">The mode whose client actor and spawn policies the old tutorial uses.</param>
/// <param name="WaveMapKey">
/// On a build with one-seat waves, the one tutorial map that runs them (the prologue); every other map the
/// tutorial room offers is a numbered single-play stage and starts in mode 15, whose
/// <c>SinglePlayGameStage</c> (<c>kr-812 sub_8E1240</c> -> <c>sub_6FF040</c>) reads the stage's own goals from
/// its MapMeta row. Empty runs every tutorial map as the wave tutorial.
/// </param>
/// <param name="MasterSlotPacket">
/// Whether the tutorial start also sends <c>gspRoomSetMasterSlotIdx</c>: cn-324 takes the master seat only from it
/// (cn-324 <c>sub_6ED2D0</c>), while kr-812's <c>gspGotoRoom</c> reader already stores it (kr-812 <c>sub_68EAD0</c> and
/// the 502 handler <c>sub_7FD460</c> both call <c>sub_689F80</c>), and 502 then shows the room-master notice whenever
/// the room stage is already current.
/// </param>
internal readonly record struct PlayTutorialShape(string OldMapKey = "", int SelectKind = 5, bool SkipEnter = false, int OldMode = 0,
    string WaveMapKey = "", bool MasterSlotPacket = false);

/// <summary>
/// Parsed <c>gcpReqPlayTutorial</c> after the nested <c>MapMetaData</c> block. The third wide
/// string is the MapMeta row name the TutorialRoomStage selected (kr-812 fill
/// <c>sub_814A70</c> copies that name into the third wstr before SETOP 107).
/// </summary>
/// <param name="Unknown0">First wide string after MapMeta (directory / path family).</param>
/// <param name="Unknown1">Second wide string after MapMeta.</param>
/// <param name="MapKey">Third wide string; MapMeta document name for <c>gspRoomEnterGame</c>.</param>
/// <param name="Difficulty">
/// Trailing u32 after the three wstr. Cn-717 fill <c>sub_89A330</c> reads MapMeta
/// <c>Difficulty</c>; GotoRoom echoes it at <c>GameSetting</c>+164 (room+252).
/// </param>
/// <param name="TimeAttackTimeLimitMs">
/// Nested MapMeta u32 index 8. Cn-717 fill <c>sub_89A330</c> loads the
/// <c>TimeAttackTimeLimit</c> column and multiplies by 1000 (prologue sends 99999000).
/// </param>
internal readonly record struct PlayTutorialRequest(
    string Unknown0, string Unknown1, string MapKey, int Difficulty, int TimeAttackTimeLimitMs);

/// <summary>
/// Parsed <c>gcpGameReportMapLoadingComplete</c> (cn-717 reader <c>sub_8CB120</c>): four u32.
/// The send helper <c>sub_543160</c> writes a mode-dependent first field, then the iterator
/// value from the load path (<c>sub_6BE2D0</c>), then two zeroes. The second field is not the
/// GotoRoom seat index.
/// </summary>
/// <param name="Unknown0">First u32 (0 on mode-15 tutorial; 5000 on modes 7/11/17/21).</param>
/// <param name="Unknown1">Second u32 from the load-path iterator (not the seat index).</param>
/// <param name="Unknown2">Third u32; sender writes 0.</param>
/// <param name="Unknown3">Fourth u32; sender writes 0.</param>
internal readonly record struct MapLoadingCompleteReport(int Unknown0, int Unknown1, int Unknown2, int Unknown3);

/// <summary>
/// The play-room wire details that differ between builds inside packets whose field roles the
/// room flow already pins: the <c>gcpReqJoinSession</c> tail width, the
/// <c>gcpRoomChangeGameSetting</c> trailing bool, and the field counts of
/// <c>gspGameEverybodyLoadingComplete</c> and <c>gspGameStartGame</c>.
/// </summary>
/// <param name="ServerRevives">
/// Whether the client only reports its death and waits to be brought back (cn-324: the death
/// states send <c>gcpGameReqRespawn</c> once and latch, dc.c:239779-239783, and the only revive
/// path is the <c>gspRespawn</c> handler <c>sub_475380</c>).
/// </param>
/// <param name="PreCountDownFirst">
/// Whether <c>gspGamePreCountDown</c> goes out before <c>gspGameCountDown</c> (cn-324: handler
/// <c>sub_46DB00</c> clears the timers the countdown is drawn from, so it must come first, and
/// <c>sub_6F0BB0</c> then shows the control and arms the countdown).
/// </param>
/// <param name="UnchosenMapKey">
/// The map key of the all-random entry, shown for a room whose creator chose no map; empty leaves such a room
/// as it was sent. A create request never carries a choice: cn-324's leaves the key empty (the room screen then
/// draws no map, <c>cn-324 sub_59F940</c>) and kr-812's copies the startup map of <c>Config/Lambient.xml</c>
/// (<c>kr-812 sub_480700</c>). The host's start resolves the literal <c>Random</c> by itself
/// (<c>cn-324 sub_5AE9A0</c> 0x5AF988, <c>kr-812 sub_5C3F80</c>) and sends the map it drew.
/// </param>
/// <param name="UnchosenMapTheme">The theme the all-random entry sits under (the first entry of the client's theme list).</param>
/// <param name="SpawnGroupAppliesLocally">Whether the native sender also queues its spawn-group request for local execution.</param>
/// <param name="UnchosenDifficultyMode">
/// The mode whose all-random entry is <paramref name="UnchosenDifficultyMapKey"/> instead of
/// <paramref name="UnchosenMapKey"/>; 0 for none.
/// </param>
/// <param name="UnchosenDifficultyMapKey">
/// The all-random entry of <paramref name="UnchosenDifficultyMode"/>: the first difficulty's random marker,
/// which the host's start resolves to one of that difficulty's maps (<c>kr-812 sub_415D30</c> /
/// <c>kr-812 sub_415F30</c>); empty for none.
/// </param>
/// <param name="RestateLeftRoom">
/// Whether the leaver's <c>gspRoomSetSession</c> restates the room just left (title, open seats and
/// <c>GameSetting</c>) under the main-menu kind, instead of a bare main-menu session (copied whole by <c>kr-812 sub_7FD310</c>).
/// </param>
/// <param name="PlazaEntrySettleMs">
/// How long the plaza entry waits after <c>gspGotoLobby</c> before the rest of its burst, so the client's
/// switch to the lobby stage (<c>kr-812 sub_81FF00</c> asks the stage manager for stage 3) is done before the
/// scene move, which acts only on a current <c>LobbyStage</c> (<c>kr-812 sub_81C6F0</c>); 0 sends it at once.
/// </param>
internal readonly record struct PlayRoomWireShape(
    bool ServerRevives = false, bool PreCountDownFirst = false,
    string UnchosenMapKey = "", string UnchosenMapTheme = "", bool SpawnGroupAppliesLocally = false,
    int UnchosenDifficultyMode = 0, string UnchosenDifficultyMapKey = "", bool RestateLeftRoom = false,
    int PlazaEntrySettleMs = 0);
/// <summary>
/// S2C that leaves the GotoRoom kind-2 lobby and starts play. kr-812 / cn-717 use
/// <c>gspRoomEnterGame</c>; id-400 has no usable host-start path here and keeps
/// <c>gspRepAutoStart</c>.
/// </summary>
internal enum TutorialMatchStart
{
    /// <summary>
    /// kr-812 / cn-717 <c>gspRoomEnterGame</c> unknown0 = 0 with the PlayTutorial map key
    /// (cn-717 handler <c>sub_882D40</c>; kr-812 <c>sub_7FF340</c>). Does not require the
    /// host-ready byte at room+532 that gates <c>gspUserHostStart</c>.
    /// </summary>
    RoomEnterGame,

    /// <summary>
    /// id-400 <c>gspRepAutoStart</c> true: RoomStage gate <c>sub_626970</c> → <c>sub_627C60</c>.
    /// id-1809 keeps the same gate at <c>sub_63ED50</c> (opcode == 520) → <c>sub_640850</c>.
    /// </summary>
    RepAutoStart,
}

/// <summary>
/// One cleared-stage row of <c>gspResSingleStageInfo</c>. Element reader
/// <c>sSINGLESTAGECLOEARINFO</c> (kr-812 <c>sub_9D95D0</c>): three u32, stored by kr-812 <c>sub_824200</c> in a map keyed
/// by the first. The TutorialRoomStage looks a row up as row plus ten times its difficulty tab (kr-812
/// <c>sub_6B3AC0</c>), the MapMeta <c>stageNum</c> numbering, and marks it cleared when present (kr-812 <c>sub_6B50E0</c>).
/// </summary>
/// <param name="Stage">The stage's MapMeta <c>stageNum</c>.</param>
/// <param name="BestCount">Best count record; kr-812 <c>sub_6B4110</c> shows it when above 0.</param>
/// <param name="BestSeconds">Best time in seconds; kr-812 <c>sub_6B4110</c> shows it as minutes and seconds otherwise.</param>
internal readonly record struct SingleStageClearInfo(int Stage, int BestCount, int BestSeconds);

/// <summary>
/// Tutorial select-scene and start-stage wire helpers for the legacy builds. Select opens with
/// <c>gspGotoRoom</c> kind 5 then <c>gspResSingleStageInfo</c>; start uses kind 2 then the
/// build's <see cref="TutorialMatchStart"/> packet.
/// </summary>
internal static class TutorialWire
{
    /// <summary>Reads cn-279 sub_44C1A0's single stage-completion word, sent by cn-279 sub_818630.</summary>
    public static bool ReadEndStage(PacketTables tables, string build, ReadOnlySpan<byte> payload)
    {
        var value = (int)PacketCodec.ReadScalars(tables, build, "TutorialEndStage", payload)["completed"];
        if (value is not (0 or 1))
            throw new PacketFormatException($"gcpReqEndStage has unsupported completion flag {value}.", 0);
        return value == 1;
    }
    /// <summary>
    /// Writes <c>gspResSingleStageInfo</c> (kr-812 reader <c>sub_840770</c>, handler
    /// <c>sub_824200</c>): u32 count, then count times <c>sSINGLESTAGECLOEARINFO</c>
    /// (u32, u32, u32). Reward item ids are not encoded here.
    /// </summary>
    /// <param name="cleared">Cleared-stage rows; empty is a valid open of the select scene.</param>
    /// <param name="tables">Audited packet structures.</param>
    /// <param name="build">Client build whose own list layout is selected.</param>
    /// <returns>The S2C body.</returns>
    public static byte[] WriteSingleStageInfo(PacketTables tables, string build, IReadOnlyList<SingleStageClearInfo> cleared)
    {
        if (cleared.Count != 0 && tables.Resolve(build, "TutorialStageInfo").Single().Type == "empty_list")
            throw new InvalidOperationException("This build has no audited nonempty tutorial stage list.");
        return PacketCodec.Write(tables, build, "TutorialStageInfo", new Dictionary<string, object?>
        {
            ["cleared"] = cleared.Select(row => new Dictionary<string, object?>
            {
                ["stage"] = row.Stage,
                ["best_count"] = row.BestCount,
                ["best_seconds"] = row.BestSeconds,
            }),
        });
    }

    /// <summary>
    /// Writes <c>gspRoomSetMasterSlotIdx</c> (cn-324 reader <c>sub_410910</c>, handler
    /// <c>sub_6ED2D0</c>): one u32 seat index. The handler stores it at room+136, which is the
    /// left side of the "am I the room master" test every host-only path uses (for example the
    /// wave manager's spawn request, cn-324 <c>sub_413A00</c>); the right side is the local seat
    /// fixed when the room context is built. A one-seat room that never receives this packet is
    /// never treated as its own host, so it never asks to spawn and the wave stays empty.
    /// </summary>
    /// <param name="tables">Audited packet layouts.</param>
    /// <param name="build">Client build whose layout is selected.</param>
    /// <param name="seat">The seat index of the room master.</param>
    /// <returns>The S2C body (exactly four bytes).</returns>
    public static byte[] WriteMasterSlot(PacketTables tables, string build, int seat) =>
        PacketCodec.Write(tables, build, "TutorialMasterSlot",
            new Dictionary<string, object?> { ["master_slot"] = seat });

    /// <summary>
    /// Writes <c>gspUserHostStart</c> (cn-717 reader <c>sub_8C4210</c>): one i32. Kept for
    /// byte-length pins; tutorial play no longer sends this — handler <c>sub_8BEDF0</c> returns
    /// early unless capability 23 is on and room+532 (host-ready UI) is non-zero.
    /// </summary>
    /// <param name="tables">Audited packet layouts.</param>
    /// <param name="build">Client build whose layout is selected.</param>
    /// <param name="unknown0">Result switch.</param>
    /// <returns>The S2C body (exactly four bytes).</returns>
    public static byte[] WriteUserHostStart(PacketTables tables, string build, int unknown0) =>
        PacketCodec.Write(tables, build, "TutorialHostStart",
            new Dictionary<string, object?> { ["unknown_4"] = unknown0 });

    /// <summary>Writes the complete native entry envelope through its nested packet tables.</summary>
    /// <param name="tables">Audited packet layouts.</param>
    /// <param name="build">Native client build.</param>
    /// <param name="unknown0">Entry selector.</param>
    /// <param name="unknown1">MapMeta row name.</param>
    /// <param name="unknown2">First suffix word where present.</param>
    /// <param name="unknown3">Second suffix word where present.</param>
    /// <param name="unknown4">Native room state in the suffix where present.</param>
    /// <param name="scoreboardValues">Named ScoreData values for builds whose entry requires them.</param>
    /// <returns>The complete bounded S2C body.</returns>
    public static byte[] WriteRoomEnterGame(PacketTables tables, string build, int unknown0, string unknown1,
        int unknown2 = 0, int unknown3 = 0, int unknown4 = 0,
        IReadOnlyDictionary<string, object?>? scoreboardValues = null)
    {
        if (scoreboardValues is null && EntryNeedsScoreboard(tables, build))
            throw new InvalidOperationException("Room entry requires complete ScoreData values.");
        return PacketCodec.Write(tables, build, "RoomEnterGame", new Dictionary<string, object?>
        {
            ["unknown_4"] = unknown0, ["map_key"] = unknown1,
            ["unknown_36"] = unknown2, ["unknown_40"] = unknown3, ["unknown_44"] = unknown4,
            ["unknown_3204"] = unknown2, ["unknown_3208"] = unknown3, ["unknown_3212"] = unknown4,
            ["unknown_3028"] = unknown2,
            ["scoreboard"] = scoreboardValues,
            // Without a round the block is the client's own empty scoreboard (id-400 sub_780A20,
            // cn-406 sub_6FEA10): every row is a vacant seat.
            ["initial_score"] = scoreboardValues
                ?? PlayRoomCodec.ScoreboardValues(tables, build, new RoundScoreboard(0, 0, [0, 0], [])),
        });
    }

    /// <summary>Checks whether the native entry table requires caller-projected scoreboard values.</summary>
    internal static bool EntryNeedsScoreboard(PacketTables tables, string build) =>
        tables.Resolve(build, "RoomEnterGame").Any(field => field.Name == "scoreboard");

    /// <summary>
    /// Nested MapMetaData u32 index of <c>TimeAttackTimeLimit</c> (cn-717 fill
    /// <c>sub_89A330</c>: eighth of thirteen column reads; value is seconds × 1000).
    /// </summary>
    public const int MapMetaTimeAttackTimeLimitIndex = 8;

    /// <summary>
    /// Consumes <c>gcpReqPlayTutorial</c> (kr-812 reader <c>sub_8455F0</c> / cn-717
    /// <c>sub_8CFAF0</c> / id-1809 reader <c>sub_7BD7C0</c>, serializer <c>sub_7BD770</c>):
    /// nested <c>MapMetaData</c> (13 u32 + N bool32), three wide strings, one u32.
    /// </summary>
    /// <param name="payload">The C2S body.</param>
    /// <param name="tables">Audited packet layouts.</param>
    /// <param name="build">Client build whose request layout is selected.</param>
    /// <returns>The three wide strings, difficulty, and MapMeta TimeAttack ms.</returns>
    public static PlayTutorialRequest ReadPlayTutorial(PacketTables tables, string build, ReadOnlySpan<byte> payload)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "TutorialPlayRequest", payload,
            maxStringCharacters: (ushort.MaxValue - 4) / 2);
        var strings = tables.Resolve(build, "TutorialPlayRequest").Where(field => field.Type == "wstr").ToArray();
        return new PlayTutorialRequest((string)fields[strings[0].Name], (string)fields[strings[1].Name],
            (string)fields["map_key"], (int)fields["difficulty"], (int)fields["time_attack_time_limit_ms"]);
    }

    /// <summary>
    /// Largest C2S <c>gcpReqWaveAllSpawnMobs</c> body accepted. The table is opaque;
    /// S2C 908 is empty (case <c>0x38C</c>) and S2C 909 echoes the table (case <c>0x38D</c>).
    /// </summary>
    public const int MaxWaveAllSpawnBytes = 16384;

    /// <summary>
    /// Largest opaque <c>gppCreateAIObject</c> / <c>gppCreateSpawnGroupMemberMob</c> body
    /// accepted for echo. Layout is host-authored; the server does not invent fields.
    /// </summary>
    public const int MaxMobCreateBytes = 4096;

    /// <summary>
    /// Writes <c>gppCreateMobsFromSpawnGroup</c> (cn-717 builder <c>sub_747250</c> /
    /// apply <c>sub_744160</c>): one u32 spawn-group index. Host emits C2S 32857 with
    /// group 1 from <c>sub_741980</c> after StartGame when <c>NpcGameContext</c> is live;
    /// the S2C apply path is the same opcode (dispatcher case <c>0x8059</c>).
    /// </summary>
    /// <param name="tables">Audited packet layouts.</param>
    /// <param name="build">Client build whose layout is selected.</param>
    /// <param name="spawnGroup">Spawn-group index (prologue host sends 1).</param>
    /// <returns>The body (exactly four bytes).</returns>
    public static byte[] WriteCreateMobsFromSpawnGroup(PacketTables tables, string build, int spawnGroup) =>
        PacketCodec.Write(tables, build, "TutorialSpawnGroup",
            new Dictionary<string, object?> { ["spawn_group"] = spawnGroup });

    /// <summary>
    /// Consumes <c>gppCreateMobsFromSpawnGroup</c>: one u32, no trailing bytes.
    /// </summary>
    /// <param name="payload">The C2S body.</param>
    /// <param name="tables">Audited packet layouts.</param>
    /// <param name="build">Client build whose layout is selected.</param>
    /// <returns>The spawn-group index.</returns>
    public static int ReadCreateMobsFromSpawnGroup(PacketTables tables, string build, ReadOnlySpan<byte> payload) =>
        (int)PacketCodec.ReadScalars(tables, build, "TutorialSpawnGroup", payload)["spawn_group"];

    /// <summary>
    /// Writes <c>gspRepStartNewWave</c> (cn-717 case <c>0x388</c> / <c>sub_ABD9A0</c>):
    /// one u32 wave index. Same layout as C2S <c>gcpReqStartNewWave</c>
    /// (<c>sub_ABD760</c>).
    /// </summary>
    /// <param name="tables">Audited packet layouts.</param>
    /// <param name="build">Client build whose layout is selected.</param>
    /// <param name="wave">Wave index the host requested (prologue starts at 1).</param>
    /// <returns>The body (exactly four bytes).</returns>
    public static byte[] WriteStartNewWave(PacketTables tables, string build, int wave) =>
        PacketCodec.Write(tables, build, "TutorialWave",
            new Dictionary<string, object?> { ["wave"] = wave });

    /// <summary>
    /// Consumes <c>gcpReqStartNewWave</c>: one u32 wave index, no trailing bytes.
    /// </summary>
    /// <param name="payload">The C2S body.</param>
    /// <param name="tables">Audited packet layouts.</param>
    /// <param name="build">Client build whose layout is selected.</param>
    /// <returns>The wave index.</returns>
    public static int ReadStartNewWave(PacketTables tables, string build, ReadOnlySpan<byte> payload) =>
        (int)PacketCodec.ReadScalars(tables, build, "TutorialWave", payload)["wave"];

    /// <summary>
    /// Checks C2S <c>gcpReqWaveAllSpawnMobs</c>: opaque wave table, size-bounded only.
    /// S2C 908 is empty (case <c>0x38C</c>); S2C 909 echoes this body (case <c>0x38D</c>).
    /// </summary>
    /// <param name="payload">The C2S body.</param>
    public static void CheckWaveAllSpawnMobs(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > MaxWaveAllSpawnBytes)
            throw new PacketFormatException(
                $"gcpReqWaveAllSpawnMobs must be 1..{MaxWaveAllSpawnBytes} bytes; got {payload.Length}.",
                0);
    }

    /// <summary>
    /// Checks an opaque per-mob create body (<c>gppCreateAIObject</c> /
    /// <c>gppCreateSpawnGroupMemberMob</c>) before echoing it unchanged.
    /// </summary>
    /// <param name="payload">The C2S body.</param>
    /// <param name="opcodeName">Opcode name for the error text.</param>
    public static void CheckMobCreateBody(ReadOnlySpan<byte> payload, string opcodeName)
    {
        if (payload.Length is 0 or > MaxMobCreateBytes)
            throw new PacketFormatException(
                $"{opcodeName} must be 1..{MaxMobCreateBytes} bytes; got {payload.Length}.", 0);
    }

    /// <summary>
    /// Consumes <c>gcpGameReportMapLoadingComplete</c> (cn-717 reader <c>sub_8CB120</c>):
    /// four u32, no trailing bytes.
    /// </summary>
    /// <param name="payload">The C2S body.</param>
    /// <param name="tables">Audited packet layouts.</param>
    /// <param name="build">Client build whose layout is selected.</param>
    /// <returns>The four u32 fields.</returns>
    public static MapLoadingCompleteReport ReadMapLoadingComplete(PacketTables tables, string build, ReadOnlySpan<byte> payload)
    {
        var fields = PacketCodec.ReadScalars(tables, build, "RoundMapLoadingReport", payload);
        return new MapLoadingCompleteReport((int)fields["unknown_4"], (int)fields["unknown_8"],
            (int)fields["unknown_12"], (int)fields["unknown_16"]);
    }

    /// <summary>Writes the loaded player's seat using the audited round-start table.</summary>
    public static byte[] WriteMapLoadingComplete(PacketTables tables, string build, int seat) =>
        PacketCodec.Write(tables, build, "RoundMapLoadingComplete", new Dictionary<string, object?> { ["seat"] = seat });

    /// <summary>Writes the loading gates; the packet table selects the native flag count.</summary>
    public static byte[] WriteEverybodyLoadingComplete(PacketTables tables, string build, bool unknown0, bool unknown1, bool unknown2) =>
        PacketCodec.Write(tables, build, "RoundEverybodyLoadingComplete", new Dictionary<string, object?>
        {
            ["unknown_4"] = unknown0, ["unknown_5"] = unknown1, ["unknown_6"] = unknown2,
        });

    /// <summary>Writes the millisecond timer for the native countdown or pre-countdown packet.</summary>
    public static byte[] WriteGameCountDown(PacketTables tables, string build, int milliseconds) =>
        PacketCodec.Write(tables, build, "RoundCountdown", new Dictionary<string, object?> { ["milliseconds"] = milliseconds });

    /// <summary>Writes the start words without imposing a field count on the build's native layout.</summary>
    public static byte[] WriteGameStartGame(PacketTables tables, string build, int unknown0, int unknown1, int unknown2) =>
        PacketCodec.Write(tables, build, "RoundStartGame", new Dictionary<string, object?>
        {
            ["unknown_4"] = unknown0, ["unknown_8"] = unknown1, ["unknown_12"] = unknown2,
        });
}
