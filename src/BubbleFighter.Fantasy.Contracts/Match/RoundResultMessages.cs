using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>
/// A player's client reports that it knocked another fighter down. The report carries the
/// client's own references, never a seat: the dialect translates them before the logic sees
/// the message.
/// </summary>
/// <param name="ReporterSlot">The seat whose client sent the report.</param>
/// <param name="Victim">
/// The fighter that went down, as a seat, or <see langword="null"/> for a reference that names
/// no seat of this round (an NPC run by another client, or a client-local entity).
/// </param>
/// <param name="ComboCount">
/// How many knock-downs in a row this client has now reported, 1 when the report carries none.
/// </param>
public sealed record RoundKillCreditReported(int ReporterSlot, int? Victim, int ComboCount) : IMessage
{
    /// <summary>Gets whether this report credits a completed teammate rescue instead of a knock-down.</summary>
    public bool IsRescue { get; init; }

    /// <summary>Gets whether the decoded scorer reference must match the authenticated reporting seat.</summary>
    public bool RequiresReporterOwnership { get; init; }
}

/// <summary>
/// A player's client reports a finishing blow, including a direct Super Pang. Dialects whose
/// report omits the victim use the reporter's outstanding knock-down; reports with an explicit
/// victim use that reference even when it names an NPC rather than a seated player.
/// </summary>
/// <param name="ReporterSlot">The seat whose client sent the report.</param>
public sealed record RoundFinishingBlowReported(int ReporterSlot) : IMessage
{
    /// <summary>Gets the victim seat, or null when the report identifies a non-player entity.</summary>
    public int? Victim { get; init; }

    /// <summary>Gets whether the dialect supplied a victim reference instead of requiring knock-down pairing.</summary>
    public bool HasVictimReference { get; init; }

    /// <summary>Gets whether this direct kill requires the configured survival mode and authenticated reporter checks.</summary>
    public bool RequiresDirectKillMode { get; init; }

    /// <summary>Gets whether an explicit scorer reference requires an authenticated reporting-seat check.</summary>
    public bool RequiresReporterOwnership { get; init; }
}

/// <summary>What one seat did in the round so far; every counter is the absolute value, never a delta.</summary>
/// <param name="Slot">The seat index.</param>
/// <param name="Kills">Finishing blows the seat's client reported - the round's kill count.</param>
/// <param name="Knockdowns">Knock-downs the seat's client reported.</param>
/// <param name="Score">The seat's score under the round's scoring rule.</param>
/// <param name="Deaths">Times the seat went down to another player.</param>
public sealed record RoundSeatStats(int Slot, int Kills, int Knockdowns, int Score, int Deaths)
{
    /// <summary>Gets the completed teammate rescues credited to this seat.</summary>
    public int Rescues { get; init; }

    /// <summary>Gets the stars the seat holds in a star round; null in a round that counts no stars.</summary>
    public int? Stars { get; init; }
}

/// <summary>
/// The live score of a running round, sent to every member after each scoring event: the scoring
/// team and its new total, and the absolute counters of the seats the event touched.
/// </summary>
/// <param name="RoomId">The room.</param>
/// <param name="ScoringTeam">The team the event scored for, 1 or 2.</param>
/// <param name="TeamTotals">Each team's total, indexed by the team number minus one; team 1 is index 0.</param>
/// <param name="Seats">The seats this event updated, with their absolute counters. The first is the scorer; a second is the victim.</param>
/// <param name="Scoreboard">The whole table as it stands after the event, for a build that has no packet for a single change.</param>
public sealed record RoundScoreUpdated(int RoomId, int ScoringTeam, IReadOnlyList<int> TeamTotals, IReadOnlyList<RoundSeatStats> Seats,
    RoundScoreboard? Scoreboard = null) : IMessage;

/// <summary>How a round finished for one player.</summary>
public enum RoundOutcome
{
    /// <summary>The round ended without a winner, or the player's team drew.</summary>
    Draw,

    /// <summary>The player's team lost.</summary>
    Lost,

    /// <summary>The player's team won.</summary>
    Won,
}

/// <summary>What a seat earned in the round; the client shows these beside the result bar.</summary>
/// <param name="Level">The level after the round.</param>
/// <param name="LevelFloorExp">Experience at which <paramref name="Level"/> starts.</param>
/// <param name="TotalExp">The seat's experience after the round.</param>
/// <param name="NextLevelFloorExp">Experience at which the next level starts.</param>
/// <param name="ExpEarned">Experience the round earned.</param>
/// <param name="LucciEarned">Lucci the round earned.</param>
/// <param name="LeveledUp">Whether the round took the seat to a new level.</param>
/// <param name="BonusBadges">Image paths the client loads as bonus animations beside the bar, at most five.</param>
public sealed record RoundRewardRow(int Level, long LevelFloorExp, long TotalExp, long NextLevelFloorExp,
    int ExpEarned, int LucciEarned, bool LeveledUp, IReadOnlyList<string> BonusBadges);

/// <summary>One row of the final scoreboard.</summary>
/// <param name="Slot">The seat index, 0..N-1; no two rows share it.</param>
/// <param name="Nickname">The name shown in the row's first column.</param>
/// <param name="Team">The seat's team, 1 or 2.</param>
/// <param name="Level">The seat's level at the end of the round.</param>
/// <param name="Stats">The seat's counters at the end of the round.</param>
/// <param name="Reward">What the seat earned, or <see langword="null"/> on the pass sent before the rewards are computed.</param>
public sealed record RoundScoreboardRow(int Slot, string Nickname, int Team, int Level, RoundSeatStats Stats, RoundRewardRow? Reward)
{
    /// <summary>Gets the committed primary reward-card sale identity; zero means no selected reward.</summary>
    public int AwardDisplayId { get; init; }

    /// <summary>Gets the player-specific final outcome; null leaves live and initialization rows without a final result.</summary>
    public RoundOutcome? Outcome { get; init; }

    /// <summary>Gets the seat's player, for a build whose rows carry the player's identity; zero when unknown.</summary>
    public long UserNumber { get; init; }
}

/// <summary>
/// The round's final scoreboard. It replaces the client's whole table, so both passes - the one
/// before the rewards and the one that carries them - repeat every row.
/// </summary>
/// <param name="RoomId">The room.</param>
/// <param name="WinnerTeam">The winning team (1 or 2), or 0 for a draw.</param>
/// <param name="TeamTotals">Each team's total, indexed by the team number minus one.</param>
/// <param name="Rows">The player rows.</param>
/// <param name="WithRewards">Whether this is the pass that carries the rewards.</param>
/// <param name="TimeOver">Whether the round ended on its time limit and this pass announces it, after the end message.</param>
public sealed record RoundScoreboard(int RoomId, int WinnerTeam, IReadOnlyList<int> TeamTotals, IReadOnlyList<RoundScoreboardRow> Rows,
    bool WithRewards = false, bool TimeOver = false) : IMessage
{
    /// <summary>Gets the round's mode, so the dialect can apply the build's per-mode result presentation.</summary>
    public int ModeId { get; init; }
}

/// <summary>The balances and level the round left the player with; the client's user-info block is refreshed from it.</summary>
/// <param name="UserNum">The player.</param>
/// <param name="Lucci">The Lucci balance after the round.</param>
/// <param name="Cash">The cash balance after the round.</param>
/// <param name="Level">The level after the round.</param>
public sealed record RoundPlayerWalletChanged(int UserNum, int Lucci, int Cash, int Level) : IMessage;

/// <summary>Why a round ended; the client shows a different banner for each.</summary>
public enum RoundEndReason
{
    /// <summary>The round's time limit ran out.</summary>
    TimeLimit,

    /// <summary>A player's client reported the stage over.</summary>
    StageReported,

    /// <summary>The round's win condition was met before its time limit.</summary>
    WinCondition,

    /// <summary>The authoritative host reports that every cooperative enemy wave was cleared.</summary>
    WavesCleared,

    /// <summary>The authoritative host reports failure of the cooperative stage objective.</summary>
    CooperativeFailed,
}

/// <summary>Sent to one member when the round is over: what happened to that member's team.</summary>
/// <param name="RoomId">The room.</param>
/// <param name="Outcome">Whether this recipient's team won, lost or drew.</param>
/// <param name="Reason">Why the round ended.</param>
public sealed record RoundEnded(int RoomId, RoundOutcome Outcome, RoundEndReason Reason = RoundEndReason.StageReported) : IMessage
{
    /// <summary>Gets the round's mode so the dialect can apply the native result presentation policy.</summary>
    public int ModeId { get; init; }

    /// <summary>Gets this recipient's team; zero retains the dialect's unassigned-seat wire policy.</summary>
    public int RecipientTeam { get; init; }
}

/// <summary>Declares a client's ordering dependency between its result gate and scoreboard scene.</summary>
public interface IRoundResultSequencePolicy
{
    /// <summary>Gets whether the end message must set the result gate before the first scoreboard arrives.</summary>
    bool EndBeforeScoreboard { get; }

    /// <summary>
    /// Gets whether the client takes one scoreboard, carrying the rewards, instead of a scoreboard before the rewards and
    /// another with them.
    /// </summary>
    bool SingleScoreboard => false;
}

/// <summary>Where the client's result screen goes next.</summary>
public enum ResultStage
{
    /// <summary>The result screen: the scoreboard without rewards.</summary>
    Scoreboard,

    /// <summary>The reward screen: the scoreboard with the rewards beside the bars.</summary>
    Reward,
}

/// <summary>Sent to every member to move their client to the next stage of the result.</summary>
/// <param name="RoomId">The room.</param>
/// <param name="Stage">The stage the client enters.</param>
public sealed record ResultStageAdvanced(int RoomId, ResultStage Stage) : IMessage;
