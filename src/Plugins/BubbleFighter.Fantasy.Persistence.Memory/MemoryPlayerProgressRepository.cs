using BubbleFighter.Fantasy.Contracts.Progress;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// In-process level / experience store with the MySQL store's guards: an untracked player reads
/// as <see langword="null"/>, and a round credit needs the wallet the login created.
/// </summary>
public sealed class MemoryPlayerProgressRepository(MemoryPlayerTable table) : IPlayerProgressRepository
{
    /// <summary>
    /// How many settlement receipts each player keeps. A settlement is only ever repeated for the round that is
    /// settling, so the latest few receipts are enough, and the player table's own bound bounds them all.
    /// </summary>
    internal const int KeptSettlements = 8;

    /// <inheritdoc />
    public ValueTask<PlayerProgress?> FindAsync(long userNum, CancellationToken ct)
    {
        if (userNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(userNum), userNum, "user_num must be a positive login id.");
        var entry = table.Find(userNum);
        if (entry is null)
            return ValueTask.FromResult<PlayerProgress?>(null);
        lock (entry.Gate)
            return ValueTask.FromResult(entry.Progress);
    }

    /// <inheritdoc />
    public ValueTask<RoundSettlementResult> SettleRoundAsync(RoundSettlementCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(command.UserNum)
            ?? throw new InvalidOperationException($"No wallet for user_num {command.UserNum}.");
        lock (entry.Gate)
        {
            var known = entry.SettlementMatches.IndexOf(command.MatchId);
            if (known >= 0)
            {
                var first = entry.Settlements[known];
                return ValueTask.FromResult(first with
                {
                    Outcome = RoundSettlementOutcome.AlreadySettled, LucciBalance = entry.Money?.Lucci ?? first.LucciBalance,
                });
            }
            var wallet = entry.Money ?? throw new InvalidOperationException($"No wallet for user_num {command.UserNum}.");
            var before = entry.Progress ?? new PlayerProgress(command.UserNum, entry.Level, 0);
            var after = command.AdvanceChecked(before);
            entry.Money = wallet with { Lucci = checked(wallet.Lucci + command.LucciEarned) };
            entry.Progress = after;
            var result = new RoundSettlementResult(RoundSettlementOutcome.Settled, before, after, command.LucciEarned, entry.Money.Lucci);
            if (entry.Settlements.Count == KeptSettlements)
            {
                entry.Settlements.RemoveAt(0);
                entry.SettlementMatches.RemoveAt(0);
            }
            entry.Settlements.Add(result);
            entry.SettlementMatches.Add(command.MatchId);
            return ValueTask.FromResult(result);
        }
    }
}
