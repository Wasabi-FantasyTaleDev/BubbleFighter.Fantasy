using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Dialect.Rooms;

/// <summary>
/// Everything the tutorial and plaza-room adapters need to know about one client build beyond
/// its struct field tables (<see cref="RoomSchema"/>): which packet starts a match and the
/// shapes of the packets whose field lists differ between builds. One row per build that
/// introduced a difference (see <see cref="RoomTables"/>), written as kr-812's row with the
/// build's own differences.
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
/// <param name="Score">How the build's score packets differ from kr-812's; kr-812's when not stated.</param>
/// <param name="TutorialSkipStart">Live-experiment switch: the tutorial start stops after the play-room GotoRoom.</param>
/// <param name="NpcWaves">Whether the native NPC wave exchange is audited, independently of the tutorial entry path.</param>
internal sealed record PlayBuild(
    PlayTutorialShape Tutorial, TutorialMatchStart MatchStart, PlayRoomWireShape Wire, bool HostStart, bool OneSeatWaves = false, bool ScoreReports = false,
    ScoreShape? Score = null, bool TutorialSkipStart = false, bool NpcWaves = false);

/// <summary>
/// A build's score-report and result-flow policy. The complete scoreboard's layout and fixed
/// counts are the ScoreData, ScoreTeam, ScoreRow and ScoreTail tables in packets/score.yaml.
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
/// <param name="StarBigGained">The report kind for one big star more (kr-812 sub_75C680: 15).</param>
/// <param name="StarSmallLost">The report kind for one small star dropped (kr-812 sub_520A20, sub_5632F0: 7).</param>
/// <param name="StarBigLost">The report kind for one big star dropped (kr-812 sub_520A20, sub_5632F0: 16).</param>
internal sealed record ScoreShape(
    bool WideReport = false, bool LiveScorePacket = true, bool OutcomeFirst = false,
      TimeOverEndBody TimeOverEnd = TimeOverEndBody.TeamTotals, bool NpcTeamOutcome = false,
    bool RescueCredit = false, bool HordeStageOutcome = false, StarReportPacket StarReport = StarReportPacket.Optimized,
    int StarSmallGained = 6, int StarBigGained = 15, int StarSmallLost = 7, int StarBigLost = 16);

/// <summary>Which score report carries a client's own star count changes in the star modes.</summary>
internal enum StarReportPacket
{
    /// <summary>The six-byte <c>gcpGameUpdateScoreOptimze</c> (kr-812 sub_52D9E0, writer sub_530900).</summary>
    Optimized,

    /// <summary>The build's <c>gcpGameUpdateScore</c> (id-400 sub_48D2F0, cn-324 sub_467E00 and cn-279 sub_818DE0 write its kind first).</summary>
    Full,
}

/// <summary>What a build's <c>gspGameTimeOverEnd</c> carries.</summary>
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
