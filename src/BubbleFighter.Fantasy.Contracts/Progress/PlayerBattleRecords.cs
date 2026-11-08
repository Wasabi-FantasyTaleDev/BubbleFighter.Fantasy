namespace BubbleFighter.Fantasy.Contracts.Progress;

/// <summary>The terminal result of a player's participation in one match.</summary>
public enum BattleRecordOutcome
{
    /// <summary>The player completed the match on its winning side.</summary>
    Won,
    /// <summary>The player completed a match that had no winning side.</summary>
    Draw,
    /// <summary>The player completed the match on its losing side.</summary>
    Lost,
    /// <summary>The player left an active match before its result was decided.</summary>
    Escaped,
}

/// <summary>Accepted server counters and an outcome for one player in one match.</summary>
/// <param name="ModeId">The shared game mode identifier; presentation groups belong to the dialect.</param>
/// <param name="Outcome">The outcome decided before publishing the result or detaching the player.</param>
/// <param name="Knockdowns">Accepted bubble credits.</param>
/// <param name="Kills">Accepted finishing blows.</param>
/// <param name="Rescues">Accepted completed rescues.</param>
/// <param name="Deaths">Accepted deaths.</param>
/// <param name="Score">The server's personal score at the end of participation.</param>
public sealed record RoundBattleRecord(int ModeId, BattleRecordOutcome Outcome,
    int Knockdowns, int Kills, int Rescues, int Deaths, int Score)
{
    /// <summary>Rejects invalid identifiers, outcomes and counters before a settlement writes anything.</summary>
    public void Validate()
    {
        if (ModeId <= 0 || !Enum.IsDefined(Outcome) || Knockdowns < 0 || Kills < 0
            || Rescues < 0 || Deaths < 0 || Score < 0)
            throw new ArgumentException("A battle record requires a positive mode, a known outcome and nonnegative counters.");
    }
}

/// <summary>Accumulates only recorded matches for one game mode, without reconstructing missing history.</summary>
/// <param name="ModeId">The shared game mode identifier.</param>
/// <param name="Wins">Completed victories.</param>
/// <param name="Draws">Completed draws.</param>
/// <param name="Losses">Completed defeats.</param>
/// <param name="Escapes">Departures before a terminal result.</param>
/// <param name="Knockdowns">Accepted bubble credits.</param>
/// <param name="Kills">Accepted finishing blows.</param>
/// <param name="Rescues">Accepted completed rescues.</param>
/// <param name="Deaths">Accepted deaths.</param>
/// <param name="Score">Accumulated server personal score.</param>
public sealed record PlayerBattleRecord(int ModeId, long Wins = 0, long Draws = 0, long Losses = 0,
    long Escapes = 0, long Knockdowns = 0, long Kills = 0, long Rescues = 0, long Deaths = 0, long Score = 0)
{
    /// <summary>Gets the number of recorded participations, including departures.</summary>
    public long Matches => checked(Wins + Draws + Losses + Escapes);

    /// <summary>Creates a new aggregate from one validated match; overflow leaves the stored aggregate unchanged.</summary>
    /// <param name="round">The match counters for this mode.</param>
    /// <returns>The replacement immutable aggregate.</returns>
    public PlayerBattleRecord Add(RoundBattleRecord round)
    {
        ArgumentNullException.ThrowIfNull(round);
        round.Validate();
        if (round.ModeId != ModeId) throw new ArgumentException("Battle record modes do not match.", nameof(round));
        checked
        {
            var result = this with
            {
                Wins = Wins + (round.Outcome == BattleRecordOutcome.Won ? 1 : 0),
                Draws = Draws + (round.Outcome == BattleRecordOutcome.Draw ? 1 : 0),
                Losses = Losses + (round.Outcome == BattleRecordOutcome.Lost ? 1 : 0),
                Escapes = Escapes + (round.Outcome == BattleRecordOutcome.Escaped ? 1 : 0),
                Knockdowns = Knockdowns + round.Knockdowns, Kills = Kills + round.Kills,
                Rescues = Rescues + round.Rescues, Deaths = Deaths + round.Deaths, Score = Score + round.Score,
            };
            _ = result.Matches;
            return result;
        }
    }
}

/// <summary>
/// A progress provider that atomically stores battle counters with the reward and its match receipt.
/// Absence of this service means battle history is unavailable, not an all-zero career.
/// </summary>
public interface IPlayerBattleRecordsRepository : IPlayerProgressRepository
{
    /// <summary>Gets whether records survive a process restart.</summary>
    bool IsDurable { get; }

    /// <summary>Returns the recorded modes for this account, or null when no battle has been recorded.</summary>
    /// <param name="userNum">The account identity in this provider's data domain.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A detached snapshot of known records; it makes no claim about earlier unrecorded matches.</returns>
    ValueTask<IReadOnlyList<PlayerBattleRecord>?> ListBattleRecordsAsync(long userNum, CancellationToken ct);
}
