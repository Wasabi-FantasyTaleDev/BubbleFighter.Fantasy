using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>
/// The race clocks of an ExtremeRacing round (round mode 24). The numbers are the client's own code words:
/// kr-1256 sub_67E8B0 switches on 2500, 20000 and 210000 and ignores any other value.
/// </summary>
public enum RoundExtremeRacingTimer
{
    /// <summary>The race part is over: an active frame clears the stage's race flag (stage +10352), so markers stop
    /// counting, shows the time-over splash and arms the intermission clocks for the duration.</summary>
    RoundOver = 2500,

    /// <summary>The finish countdown: an active frame arms the stage timer at +3064 that kr-1256 sub_D159F0 shows in
    /// whole seconds; an inactive frame clears it.</summary>
    FinishCountdown = 20000,

    /// <summary>The race part runs: an active frame sets the stage's race flag and arms the race clock at +3040 that
    /// kr-1256 sub_D16870 shows as minutes and seconds; an inactive frame clears that clock.</summary>
    RaceRunning = 210000,
}

/// <summary>
/// A race clock change the round delivers to every member (kr-1256 S2C 711, read inline by kr-1256 sub_CE5600 and
/// applied by kr-1256 sub_67E8B0).
/// </summary>
/// <param name="Timer">Which clock changes.</param>
/// <param name="DurationMs">The clock's length in milliseconds.</param>
/// <param name="Active">Whether the clock starts (true) or stops (false).</param>
public sealed record RoundExtremeRacingTimerChanged(RoundExtremeRacingTimer Timer, int DurationMs, bool Active) : IMessage;

/// <summary>
/// The race round the stage moves to (kr-1256 S2C 712, kr-1256 sub_CE5600 case 0x2C8 -> kr-1256 sub_67E4A0,
/// which stores it at stage +3160 and the previous one at +3164). The stage starts on round 1 by itself
/// (kr-1256 sub_CE54F0), so this is sent for the later rounds.
/// </summary>
/// <param name="Round">The new one-based round.</param>
public sealed record RoundExtremeRacingRoundChanged(int Round) : IMessage;

/// <summary>
/// A player's report that it touched the goal of a race round (kr-1256 C2S 711 from kr-1256 sub_B0D760 kind 5).
/// The client sends it once per round, only while the race flag is set and only for the goal whose round equals
/// the stage's current round.
/// </summary>
/// <param name="Round">The round whose goal was reached.</param>
public sealed record RoundExtremeRacingGoalReported(int Round) : IMessage;

/// <summary>
/// A checkpoint touch decoded from the native peer frame. The marker is the remainder of the map marker index
/// divided by ten; kr-1256 sub_B0D760 kind 10 sends the actor, quotient and remainder in that order.
/// </summary>
/// <param name="Actor">The touching character's scene identity.</param>
/// <param name="Round">The one-based race round.</param>
/// <param name="Marker">The marker within that round, from zero through nine.</param>
public sealed record RoundExtremeRacingCheckpoint(int Actor, int Round, int Marker);

/// <summary>
/// The fields of a player's race record (kr-1256 sub_1155410). Two pairs feed the boards: the round pair is summed
/// per team by kr-1256 sub_D16410 and shown for the local player by kr-1256 sub_D17680; the total pair is summed
/// for the ranking of kr-1256 sub_D1A220, where a larger sum ranks first. The remaining field meanings are not
/// established and the server does not send them.
/// </summary>
public enum RoundExtremeRacingRecordField
{
    /// <summary>The first part of the total pair (record +72).</summary>
    TotalFirst = 0,

    /// <summary>The first part of the round pair (record +44).</summary>
    RoundFirst = 1,

    /// <summary>The second part of the total pair (record +76).</summary>
    TotalSecond = 2,

    /// <summary>The second part of the round pair (record +48).</summary>
    RoundSecond = 3,
}

/// <summary>One record field update.</summary>
/// <param name="Field">The field.</param>
/// <param name="Value">Its new value.</param>
public sealed record RoundExtremeRacingRecordValue(RoundExtremeRacingRecordField Field, int Value);

/// <summary>One seat's record updates; the client keys its records by seat index (kr-1256 sub_D189F0).</summary>
/// <param name="Seat">The seat index.</param>
/// <param name="Values">The fields to update, in order.</param>
public sealed record RoundExtremeRacingRecord(int Seat, IReadOnlyList<RoundExtremeRacingRecordValue> Values);

/// <summary>
/// Record updates for the race boards (kr-1256 S2C 714, kr-1256 sub_CE5600 case 0x2CA -> kr-1256 sub_CE6BA0). Scope 1
/// applies every record, refreshes the ranking board and recomputes the team round totals.
/// </summary>
/// <param name="Records">The seats' updates.</param>
public sealed record RoundExtremeRacingRecordsUpdated(IReadOnlyList<RoundExtremeRacingRecord> Records) : IMessage;
