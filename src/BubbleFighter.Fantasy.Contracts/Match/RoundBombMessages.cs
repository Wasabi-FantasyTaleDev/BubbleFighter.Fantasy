using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>The bomb's state in a bomb-mission round (round mode 19). The numbers are the client's own
/// state word: kr-812 sub_4245B0 switches on it with cases 0..6.</summary>
public enum RoundBombState
{
    /// <summary>The bomb is gone: installer and defuser records are cleared and the position zeroed.</summary>
    Reset = 0,

    /// <summary>Planting is in progress; the client reports countdown only after its operation timer expires.</summary>
    Planting = 1,

    /// <summary>The detonation countdown is armed (the native client runs 60000 ms unless the frame aligns it).</summary>
    Countdown = 2,

    /// <summary>The bomb exploded; the exploded side's outcome word is recorded for the mission-over report.</summary>
    Exploded = 3,

    /// <summary>The bomb is being defused; the operator word is the defuser.</summary>
    Defusing = 4,

    /// <summary>The defuse was interrupted; the detonation countdown continues where it was.</summary>
    DefuseInterrupted = 5,

    /// <summary>The defuse completed; the defusing side's outcome word is recorded for the mission-over report.</summary>
    Defused = 6,
}

/// <summary>
/// A client's bomb report in a bomb-mission round, read from C2S <c>gcpBombState</c> (kr-812 2012,
/// kr-812 sub_52A590). Plant and defuse operations are driven locally; the room master reports detonation
/// (kr-812 sub_422850 and the master/local-slot comparison kr-812 sub_807260).
/// </summary>
/// <param name="State">The state the acting client reports.</param>
/// <param name="Operator">The native operator's user number, stored as planter or defuser; -1 represents a cleared reference.</param>
/// <param name="PositionX">The bomb's position, first float.</param>
/// <param name="PositionY">The bomb's position, second float.</param>
/// <param name="PositionZ">The bomb's position, third float.</param>
public sealed record RoundBombStateReported(RoundBombState State, int Operator, float PositionX, float PositionY,
    float PositionZ) : IMessage;

/// <summary>
/// The bomb state the round delivers to every member as S2C <c>gspBombState</c> (kr-812 973, applied by
/// kr-812 sub_4245B0).
/// </summary>
/// <param name="State">The bomb's state.</param>
/// <param name="Operator">The unchanged native operator user number.</param>
/// <param name="PositionX">The bomb's position, first float.</param>
/// <param name="PositionY">The bomb's position, second float.</param>
/// <param name="PositionZ">The bomb's position, third float.</param>
public sealed record RoundBombStateChanged(RoundBombState State, int Operator, float PositionX, float PositionY,
    float PositionZ) : IMessage
{
    /// <summary>
    /// Gets whether the receivers align their countdown from the shared game clock instead of running the plain
    /// 60000 ms (kr-812 sub_4245B0 case 2 reads the clock minus 2000 when this is set). The server keeps it clear:
    /// every client then runs its own countdown and the room master reports the explosion, which needs no
    /// clock semantics the server has not established.
    /// </summary>
    public bool TimerAligned { get; init; }
}

/// <summary>
/// The report that a bomb-mission round is decided, read from C2S <c>gcpBombMissionOver</c> (kr-812 2011,
/// kr-812 sub_52AAC0). The room master reports after the explosion or defuse tail, or when a team is
/// eliminated before arming (kr-812 sub_422850 and kr-812 sub_52A640, gated by kr-812 sub_807260).
/// </summary>
/// <param name="WinningTeam">The winning side (team 1 or 2).</param>
/// <param name="Planter">The native planter reference, or -1 when elimination decides before arming.</param>
/// <param name="Defuser">The native defuser reference, or -1 when no defuser is recorded.</param>
public sealed record RoundBombMissionOverReported(int WinningTeam, int Planter, int Defuser) : IMessage;
