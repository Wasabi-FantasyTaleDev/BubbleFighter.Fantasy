using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Reads one build's tutorial and play-room values from the <c>play</c> block of <c>packets/builds.yaml</c>:
/// which packet starts a match and the shapes of the packets whose field lists differ between builds,
/// beyond the struct field tables (<see cref="RoomSchema"/>). kr-812 states the whole block as the
/// baseline; every other legacy build states only what differs from its base, and the packet tables lay
/// the nested maps over one another, so a key a build does not state is its base's.
/// </summary>
/// <param name="Tutorial">Tutorial scene policy; the request fields are defined in <c>TutorialPlayRequest</c>.</param>
/// <param name="MatchStart">Which S2C starts the client's match from the room.</param>
/// <param name="Wire">The play-room request and round packet shapes.</param>
/// <param name="HostStart">Whether the build has <c>gcpUserHostStart</c>.</param>
/// <param name="OneSeatWaves">
/// Whether the one-seat tutorial answers the load report and the wave requests itself
/// (cn-717 RoomStage cases 0x2BD-0x2C0 and its wave manager).
/// </param>
/// <param name="ScoreReports">
/// Whether inbound kill-credit report codecs have been audited for the build. Outbound scoreboards
/// and result stages are selected separately by the packet table's <c>round_results</c> capability.
/// </param>
/// <param name="Score">The build's score-report and result-flow values; kr-812's when not stated.</param>
/// <param name="TutorialSkipStart">Live-experiment switch: the tutorial start stops after the play-room GotoRoom.</param>
/// <param name="NpcWaves">Whether the native NPC wave exchange is audited, independently of the tutorial entry path.</param>
internal sealed record PlayValues(
    PlayTutorialShape Tutorial, TutorialMatchStart MatchStart, PlayRoomWireShape Wire, bool HostStart,
    bool OneSeatWaves, bool ScoreReports, PlayValues.ScoreValues Score, bool TutorialSkipStart, bool NpcWaves)
{
    /// <summary>Names the score report that carries a client's own star count changes in the star modes.</summary>
    internal enum StarReportPacket
    {
        /// <summary>The six-byte <c>gcpGameUpdateScoreOptimze</c> (kr-812 sub_52D9E0, writer sub_530900).</summary>
        Optimized,

        /// <summary>The build's <c>gcpGameUpdateScore</c> (id-400 sub_48D2F0, cn-324 sub_467E00 and cn-279 sub_818DE0 write its kind first).</summary>
        Full,
    }

    /// <summary>Names the body a build's <c>gspGameTimeOverEnd</c> carries.</summary>
    internal enum TimeOverEndBody
    {
        /// <summary>
        /// Two bytes, the totals of teams 1 and 2 (reader <c>kr-812 sub_80A530</c>; <c>0B 01</c> for an
        /// 11 to 1 round). Sent when the round ended on its time limit.
        /// </summary>
        TeamTotals,

        /// <summary>The whole <c>ScoreData</c> block (reader <c>cn-324 sub_6E9EF0</c>). Sent at every end.</summary>
        Scoreboard,
    }

    /// <summary>
    /// A build's score-report and result-flow values, the <c>score</c> block of the <c>play</c> values. The
    /// complete scoreboard's layout and fixed counts are the ScoreData, ScoreTeam, ScoreRow and ScoreTail
    /// tables in packets/score.yaml.
    /// </summary>
    /// <param name="WideReport">
    /// Whether one <c>gcpGameUpdateScore</c> of eight u32 carries both the knock-down and the finishing blow
    /// (reader <c>cn-324 sub_45CBA0</c>) instead of kr-812's 16-byte report and its separate
    /// <c>gcpGameUpdateScoreOptimze</c>.
    /// </param>
    /// <param name="LiveScorePacket">
    /// Whether the build has <c>gspUpdateScoreData</c>; a build without it is sent the whole scoreboard
    /// after every report.
    /// </param>
    /// <param name="OutcomeFirst">Whether <c>gspGameEndGame</c> carries the outcome in its first word instead of its second.</param>
    /// <param name="NpcTeamOutcome">Whether the native horde result compares the end word with the recipient's team.</param>
    /// <param name="RescueCredit">Whether the build's wide score report has a proven completed-rescue kind.</param>
    /// <param name="HordeStageOutcome">Whether horde result scenes require a separate cleared or failed notification before the end gate.</param>
    /// <param name="TimeOverEnd">
    /// What <c>gspGameTimeOverEnd</c> carries. Its handler opens the score stage of the result scene
    /// (<c>kr-812 sub_804B10</c> calls <c>kr-812 sub_5ACB20</c>; <c>cn-324 sub_6F2280</c> is the only caller of
    /// <c>cn-324 sub_63B230</c>), and it follows <c>gspGameEndGame</c>.
    /// </param>
    /// <param name="StarReport">Which report carries the client's own star count changes in the star modes.</param>
    /// <param name="StarSmallGained">The report kind for one small star more (kr-812 sub_75CE60 and the starting stars of sub_661AF0: 6).</param>
    /// <param name="StarBigGained">The report kind for one big star more (kr-812 sub_75C680: 15; cn-279 sub_88ED00 reports 14).</param>
    /// <param name="StarSmallLost">The report kind for one small star dropped (kr-812 sub_520A20, sub_5632F0: 7).</param>
    /// <param name="StarBigLost">The report kind for one big star dropped (kr-812 sub_520A20, sub_5632F0: 16; cn-279's drop path reports 15).</param>
    internal sealed record ScoreValues(
        bool WideReport = false, bool LiveScorePacket = true, bool OutcomeFirst = false,
        TimeOverEndBody TimeOverEnd = TimeOverEndBody.TeamTotals, bool NpcTeamOutcome = false,
        bool RescueCredit = false, bool HordeStageOutcome = false, StarReportPacket StarReport = StarReportPacket.Optimized,
        int StarSmallGained = 6, int StarBigGained = 15, int StarSmallLost = 7, int StarBigLost = 16);

    // The keys the play block and its sub-blocks know; anything else is a configuration error.
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        "tutorial", "match_start", "wire", "host_start", "one_seat_waves", "score_reports", "score",
        "tutorial_skip_start", "npc_waves",
    };
    private static readonly HashSet<string> TutorialKeys = new(StringComparer.Ordinal)
    {
        "old_map_key", "select_kind", "skip_enter", "old_mode", "wave_map_key", "master_slot_packet",
    };
    private static readonly HashSet<string> WireKeys = new(StringComparer.Ordinal)
    {
        "server_revives", "pre_count_down_first", "unchosen_map_key", "unchosen_map_theme", "spawn_group_applies_locally",
        "unchosen_difficulty_mode", "unchosen_difficulty_map_key", "restate_left_room", "plaza_entry_settle_ms",
    };
    private static readonly HashSet<string> ScoreKeys = new(StringComparer.Ordinal)
    {
        "wide_report", "live_score_packet", "outcome_first", "time_over_end", "npc_team_outcome", "rescue_credit",
        "horde_stage_outcome", "star_report", "star_small_gained", "star_big_gained", "star_small_lost", "star_big_lost",
    };

    /// <summary>
    /// Reads one build's play values, refusing everything the block does not define: an unknown key, a value
    /// that is not one of the names its key accepts, or a missing <c>match_start</c> or <c>host_start</c>
    /// (every legacy build states them or inherits them from kr-812).
    /// </summary>
    /// <param name="tables">The packet tables the build is named in.</param>
    /// <param name="build">The build.</param>
    /// <returns>The resolved values.</returns>
    /// <exception cref="ConfigException">
    /// Thrown when the build states no play block, a key is missing or unknown, or a value does not
    /// spell one of the names its key accepts.
    /// </exception>
    internal static PlayValues Of(PacketTables tables, string build)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var values = tables.ValuesOf(build);
        var where = $"'{tables.Directory}' build '{build}' play";
        if (!values.TryGetValue("play", out var raw) || raw is not Dictionary<string, object?> play)
            throw new ConfigException($"{where}: the build must state the play block.");
        RejectUnknown(play, Keys, where);
        foreach (var required in (string[])["match_start", "host_start"])
            if (!play.ContainsKey(required))
                throw new ConfigException($"{where}.{required} is missing.");
        var tutorial = Block(play, "tutorial", where);
        RejectUnknown(tutorial, TutorialKeys, $"{where}.tutorial");
        var wire = Block(play, "wire", where);
        RejectUnknown(wire, WireKeys, $"{where}.wire");
        var score = Block(play, "score", where);
        RejectUnknown(score, ScoreKeys, $"{where}.score");
        return new(
            new PlayTutorialShape(
                Text(tutorial, "old_map_key", "", $"{where}.tutorial"),
                Number(tutorial, "select_kind", 5, $"{where}.tutorial"),
                Flag(tutorial, "skip_enter", false, $"{where}.tutorial"),
                Number(tutorial, "old_mode", 0, $"{where}.tutorial"),
                Text(tutorial, "wave_map_key", "", $"{where}.tutorial"),
                Flag(tutorial, "master_slot_packet", false, $"{where}.tutorial")),
            // The presence check above makes match_start required; the fallback never applies.
            Choice(play, "match_start", TutorialMatchStart.RoomEnterGame, where),
            new PlayRoomWireShape(
                Flag(wire, "server_revives", false, $"{where}.wire"),
                Flag(wire, "pre_count_down_first", false, $"{where}.wire"),
                Text(wire, "unchosen_map_key", "", $"{where}.wire"),
                Text(wire, "unchosen_map_theme", "", $"{where}.wire"),
                Flag(wire, "spawn_group_applies_locally", false, $"{where}.wire"),
                Number(wire, "unchosen_difficulty_mode", 0, $"{where}.wire"),
                Text(wire, "unchosen_difficulty_map_key", "", $"{where}.wire"),
                Flag(wire, "restate_left_room", false, $"{where}.wire"),
                Number(wire, "plaza_entry_settle_ms", 0, $"{where}.wire")),
            Flag(play, "host_start", false, where),
            Flag(play, "one_seat_waves", false, where),
            Flag(play, "score_reports", false, where),
            new ScoreValues(
                Flag(score, "wide_report", false, $"{where}.score"),
                Flag(score, "live_score_packet", true, $"{where}.score"),
                Flag(score, "outcome_first", false, $"{where}.score"),
                Choice(score, "time_over_end", TimeOverEndBody.TeamTotals, $"{where}.score"),
                Flag(score, "npc_team_outcome", false, $"{where}.score"),
                Flag(score, "rescue_credit", false, $"{where}.score"),
                Flag(score, "horde_stage_outcome", false, $"{where}.score"),
                Choice(score, "star_report", StarReportPacket.Optimized, $"{where}.score"),
                Number(score, "star_small_gained", 6, $"{where}.score"),
                Number(score, "star_big_gained", 15, $"{where}.score"),
                Number(score, "star_small_lost", 7, $"{where}.score"),
                Number(score, "star_big_lost", 16, $"{where}.score")),
            Flag(play, "tutorial_skip_start", false, where),
            Flag(play, "npc_waves", false, where));
    }

    private static void RejectUnknown(Dictionary<string, object?> map, HashSet<string> known, string where)
    {
        if (map.Keys.FirstOrDefault(key => !known.Contains(key)) is { } unknown)
            throw new ConfigException($"{where} has the unknown value '{unknown}'.");
    }

    // A sub-block a build does not state leaves every key at its default.
    private static Dictionary<string, object?> Block(Dictionary<string, object?> map, string key, string where) =>
        !map.TryGetValue(key, out var raw)
            ? []
            : raw as Dictionary<string, object?> ?? throw new ConfigException($"{where}.{key} must be a mapping.");

    private static string Text(Dictionary<string, object?> map, string key, string fallback, string where) =>
        !map.TryGetValue(key, out var raw)
            ? fallback
            : raw as string ?? throw new ConfigException($"{where}.{key} must be a value.");

    private static bool Flag(Dictionary<string, object?> map, string key, bool fallback, string where) =>
        !map.TryGetValue(key, out var raw)
            ? fallback
            : raw switch
            {
                "true" => true,
                "false" => false,
                _ => throw new ConfigException($"{where}.{key} must be true or false."),
            };

    private static int Number(Dictionary<string, object?> map, string key, int fallback, string where)
    {
        if (!map.TryGetValue(key, out var raw))
            return fallback;
        return raw as string is { } text
            && int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ConfigException($"{where}.{key} must be a whole number, not '{raw}'.");
    }

    private static T Choice<T>(Dictionary<string, object?> map, string key, T fallback, string where) where T : struct, Enum
    {
        if (!map.TryGetValue(key, out var raw))
            return fallback;
        return raw as string is { } text && Enum.TryParse<T>(text, out var value) && Enum.IsDefined<T>(value)
            ? value
            : throw new ConfigException($"{where}.{key} must be one of {string.Join(", ", Enum.GetNames<T>())}, not '{raw}'.");
    }
}
