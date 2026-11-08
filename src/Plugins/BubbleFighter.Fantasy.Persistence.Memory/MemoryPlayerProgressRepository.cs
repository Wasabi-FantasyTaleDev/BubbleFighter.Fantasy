using BubbleFighter.Fantasy.Contracts.Progress;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// In-process level / experience store with the MySQL store's guards: an untracked player reads
/// as <see langword="null"/>, and a round credit needs the wallet the login created.
/// </summary>
public sealed class MemoryPlayerProgressRepository : IPlayerBattleRecordsRepository
{
    private readonly MemoryPlayerTable table;
    private readonly int _maxBattleSettlements;
    private readonly int _maxBattleModes;

    /// <summary>Creates a bounded progress store with non-evicting battle receipts.</summary>
    /// <param name="table">The shared account table.</param>
    /// <param name="maxBattleSettlements">Maximum battle receipts per player; a full store refuses new matches.</param>
    /// <param name="maxBattleModes">Maximum recorded game modes per player.</param>
    public MemoryPlayerProgressRepository(MemoryPlayerTable table, int maxBattleSettlements = 4096, int maxBattleModes = 128)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBattleSettlements, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBattleModes, 1);
        this.table = table;
        _maxBattleSettlements = maxBattleSettlements;
        _maxBattleModes = maxBattleModes;
    }

    /// <inheritdoc />
    public bool IsDurable => false;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PlayerBattleRecord>?> ListBattleRecordsAsync(long userNum, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(userNum, 1);
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(userNum);
        if (entry is null) return ValueTask.FromResult<IReadOnlyList<PlayerBattleRecord>?>(null);
        lock (entry.Gate)
            return ValueTask.FromResult<IReadOnlyList<PlayerBattleRecord>?>(entry.BattleRecords.Count == 0
                ? null : entry.BattleRecords.Values.OrderBy(record => record.ModeId).ToArray());
    }

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
            if (entry.BattleSettlements.TryGetValue(command.MatchId, out var battleReceipt))
                return ValueTask.FromResult(battleReceipt with
                {
                    Outcome = RoundSettlementOutcome.AlreadySettled,
                    LucciBalance = entry.Money?.Lucci ?? battleReceipt.LucciBalance,
                });
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
            PlayerBattleRecord? battle = null;
            if (command.BattleRecord is { } recorded)
            {
                if (entry.BattleSettlements.Count >= _maxBattleSettlements)
                    throw new InvalidOperationException("The player's battle receipt capacity is full; no settlement was stored.");
                if (!entry.BattleRecords.ContainsKey(recorded.ModeId) && entry.BattleRecords.Count >= _maxBattleModes)
                    throw new InvalidOperationException("The player's battle mode capacity is full; no settlement was stored.");
                battle = (entry.BattleRecords.GetValueOrDefault(recorded.ModeId) ?? new(recorded.ModeId)).Add(recorded);
            }
            var money = wallet with { Lucci = checked(wallet.Lucci + command.LucciEarned) };
            var result = new RoundSettlementResult(RoundSettlementOutcome.Settled, before, after, command.LucciEarned, money.Lucci);
            // Every checked computation precedes the first mutation, including both independent overflow checks.
            entry.Money = money;
            entry.Progress = after;
            if (battle is not null)
            {
                entry.BattleRecords[battle.ModeId] = battle;
                entry.BattleSettlements.Add(command.MatchId, result);
            }
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
