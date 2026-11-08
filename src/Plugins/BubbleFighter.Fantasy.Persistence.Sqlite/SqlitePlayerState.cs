using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Wallet;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>
/// The save's fighter, wallet and round-progress fragment: reads serve the snapshot's stored rows and
/// writes mutate the whole snapshot under one transaction, keeping wallet changes serialized with
/// purchases by construction.
/// </summary>
internal sealed partial class SqliteRepositories : IFighterRepository, IPlayerWalletRepository, IPlayerBattleRecordsRepository
{
    public ValueTask<IReadOnlyList<FighterRecord>> ListByUserNumAsync(long userNum, CancellationToken ct) =>
        new(database.Read<IReadOnlyList<FighterRecord>>(db => db.State(userNum)?.Fighters.OrderBy(row => row.Slot).ToArray() ?? [], ct));

    public ValueTask UpsertAsync(FighterRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.UserNum <= 0 || record.Slot >= FighterLimits.MaxFighterSlots || record.Level < 1 || record.Nickname is null
            || record.CharacterType < (record.Slot == 0 ? 1 : 0) || record.CharacterType > 31
            || record.EquippedItemId != 0 && record.EquippedItemId - 300000 is not (>= 1 and <= 31))
            throw new ArgumentException("Invalid fighter identity, slot, level or character.", nameof(record));
        Mutate(record.UserNum, state => { state.Fighters.RemoveAll(row => row.Slot == record.Slot); state.Fighters.Add(record); return true; }, ct);
        return ValueTask.CompletedTask;
    }

    public ValueTask<PlayerWallet?> FindByUserNumAsync(long userNum, CancellationToken ct) =>
        new(database.Read(db => db.State(userNum)?.Wallet, ct));

    public ValueTask<PlayerWallet> GetOrCreateAsync(PlayerWallet initialWallet, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(initialWallet);
        if (initialWallet.UserNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialWallet), initialWallet.UserNum, "user_num must be a positive login id.");
        if (initialWallet.Lucci < 0 || initialWallet.Cash < 0)
            throw new ArgumentOutOfRangeException(nameof(initialWallet), "A wallet balance cannot be negative.");

        return new(Mutate(initialWallet.UserNum, state =>
        {
            state.Wallet ??= initialWallet;
            state.Progress ??= new(initialWallet.UserNum, state.Level, 0);
            return state.Wallet;
        }, ct));
    }

    public ValueTask UpsertAsync(PlayerWallet record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        Mutate(record.UserNum, state =>
        {
            state.Wallet = record;
            state.Progress ??= new(record.UserNum, state.Level, 0);
            return true;
        }, ct);
        return ValueTask.CompletedTask;
    }

    public bool IsDurable => true;

    public ValueTask<IReadOnlyList<PlayerBattleRecord>?> ListBattleRecordsAsync(long userNum, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(userNum, 1);
        return new(database.Read<IReadOnlyList<PlayerBattleRecord>?>(db =>
            db.State(userNum)?.BattleRecords is { Count: > 0 } records
                ? records.OrderBy(record => record.ModeId).ToArray() : null, ct));
    }

    public ValueTask<PlayerProgress?> FindAsync(long userNum, CancellationToken ct) =>
        new(database.Read(db => db.State(userNum)?.Progress, ct));

    public ValueTask<RoundSettlementResult> SettleRoundAsync(RoundSettlementCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        return new(database.Write((db, _) =>
        {
            var state = db.RequireState(command.UserNum);
            var wallet = state.Wallet ?? throw new InvalidOperationException("Round credit requires an initialized wallet.");
            // The receipt and the player's snapshot change in one transaction: a receipt means the credit is stored.
            var receipt = db.Orm.Queryable<RoundSettlementRow>()
                .Where(row => row.MatchId == command.MatchId && row.UserNum == command.UserNum).ToList().FirstOrDefault();
            if (receipt is not null)
                return new RoundSettlementResult(RoundSettlementOutcome.AlreadySettled,
                    new(command.UserNum, receipt.BeforeLevel, receipt.BeforeExp),
                    new(command.UserNum, receipt.AfterLevel, receipt.AfterExp), receipt.LucciEarned, wallet.Lucci);
            var before = state.Progress ?? new PlayerProgress(command.UserNum, state.Level, 0);
            var after = command.AdvanceChecked(before);
            if (command.BattleRecord is { } battle)
            {
                var previous = state.BattleRecords.SingleOrDefault(row => row.ModeId == battle.ModeId);
                if (previous is null && state.BattleRecords.Count >= db.Options.MaxBattleModesPerPlayer)
                    throw new InvalidOperationException("The player's battle mode capacity is full; no settlement was stored.");
                var aggregate = (previous ?? new(battle.ModeId)).Add(battle);
                if (previous is not null) state.BattleRecords.Remove(previous);
                state.BattleRecords.Add(aggregate);
            }
            state.Wallet = wallet with { Lucci = checked(wallet.Lucci + command.LucciEarned) };
            state.Progress = after;
            state.Level = after.Level;
            state.Fighters = state.Fighters.Select(row => row with { Level = after.Level }).ToList();
            db.Orm.Insertable(new RoundSettlementRow
            {
                MatchId = command.MatchId, UserNum = command.UserNum, BeforeLevel = before.Level, BeforeExp = before.Exp,
                AfterLevel = after.Level, AfterExp = after.Exp, LucciEarned = command.LucciEarned,
                SettledAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }).ExecuteCommand();
            db.Save(command.UserNum, state);
            return new RoundSettlementResult(RoundSettlementOutcome.Settled, before, after, command.LucciEarned, state.Wallet.Lucci);
        }, ct));
    }
}
