namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>Counts the native bomb match separately from player scores and accepts one winner per round.</summary>
internal sealed class BombSeriesState
{
    private readonly int[] _roundWins = new int[2];
    private readonly int _roundsToWin;

    internal BombSeriesState(int roundsToWin = 2)
    {
        if (roundsToWin is < 2 or > 3)
            throw new ArgumentOutOfRangeException(nameof(roundsToWin));
        _roundsToWin = roundsToWin;
    }

    // kr-812 sub_4FB600 consumes zero-based indices 0..2; index 2 uses the supplied attacker selector.
    internal int RoundIndex { get; private set; }
    internal int AttackingTeam => RoundIndex % 2 + 1;
    internal int? RoundWinner { get; private set; }
    internal int? MatchWinner { get; private set; }
    internal bool Forfeited { get; private set; }
    internal bool CanContinue => RoundWinner is not null && MatchWinner is null;

    /// <summary>Returns two cumulative round-win counts without exposing the mutable match record.</summary>
    internal int[] SnapshotWins() => [.. _roundWins];

    /// <summary>Ends an unfinished match when a whole team leaves, without inventing wins for unplayed rounds.</summary>
    internal bool TryForfeit(int winner)
    {
        if (MatchWinner is not null || winner is not (1 or 2)) return false;
        MatchWinner = winner;
        Forfeited = true;
        return true;
    }

    /// <summary>Records an already-decided round once; stale indices cannot award another win.</summary>
    internal bool TryCompleteRound(int roundIndex, int winner)
    {
        if (roundIndex != RoundIndex || RoundWinner is not null || MatchWinner is not null || winner is not (1 or 2))
            return false;
        RoundWinner = winner;
        if (++_roundWins[winner - 1] == _roundsToWin)
            MatchWinner = winner;
        return true;
    }

    /// <summary>Advances only a completed non-final round, retaining the two cumulative win counts.</summary>
    internal bool TryContinue(int completedRoundIndex)
    {
        if (completedRoundIndex != RoundIndex || !CanContinue)
            return false;
        RoundIndex++;
        RoundWinner = null;
        return true;
    }
}
