namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>
/// A life report the round ignored because it does not fit the reported character's state. These checks only
/// observe: the packet itself has already gone to the other players, and the round neither rejects it nor
/// disconnects the sender. The counts show how often real clients disagree with the server's view before any
/// check is turned into a refusal.
/// </summary>
internal enum LifeCheck
{
    /// <summary>A report about the sender's own character named another character.</summary>
    OwnEventForeignActor,

    /// <summary>A bubble report for a character that was not alive.</summary>
    BubbledWhileNotAlive,

    /// <summary>A death report for a character that was not alive.</summary>
    DiedWhileNotAlive,

    /// <summary>A bubble expiry for a character that was not in a bubble.</summary>
    ExpiredWhileNotBubbled,

    /// <summary>A release of a character that is not in a bubble, or of no seat's character.</summary>
    ReleasedNotBubbled,

    /// <summary>A finished death presentation for a character that was not dead.</summary>
    CleanedWhileNotDead,

    /// <summary>A reset confirmation without a granted reset.</summary>
    ResetConfirmedWithoutGrant,

    /// <summary>A reset confirmation on another connection than the one the grant went to.</summary>
    ResetConfirmedOnOtherLink,

    /// <summary>A pop whose victim is no seat's character.</summary>
    PopWithoutVictim,

    /// <summary>A pop whose popper is no seat's character.</summary>
    PopWithoutInitiator,

    /// <summary>A pop of the popper's own character.</summary>
    PopOfSelf,

    /// <summary>A pop of a character that is not in a bubble (for example a pop both clients report, counted once).</summary>
    PopOfUnbubbled,

    /// <summary>A pop between characters of the same team, or of a seat without a team.</summary>
    PopWithoutOpposingTeams,
}

/// <summary>Per-round counts of <see cref="LifeCheck"/> results; a fixed array, so it is bounded. Guarded by the room gate.</summary>
internal sealed class LifeObservations
{
    private readonly int[] _counts = new int[Enum.GetValues<LifeCheck>().Length];

    /// <summary>Counts one ignored report.</summary>
    /// <param name="check">Why it was ignored.</param>
    /// <returns>Whether this is the round's first report of that kind.</returns>
    internal bool Note(LifeCheck check)
    {
        ref var count = ref _counts[(int)check];
        if (count < int.MaxValue) count++;
        return count == 1;
    }

    /// <summary>Gets how many reports of one kind the round ignored.</summary>
    internal int Count(LifeCheck check) => _counts[(int)check];

    /// <summary>Gets whether the round ignored any report.</summary>
    internal bool Any => _counts.Any(count => count > 0);

    /// <summary>Gets the nonzero counts as <c>Kind=count</c> pairs for one log line.</summary>
    internal string Summary() => string.Join(", ", Enum.GetValues<LifeCheck>()
        .Where(check => _counts[(int)check] > 0).Select(check => $"{check}={_counts[(int)check]}"));
}
