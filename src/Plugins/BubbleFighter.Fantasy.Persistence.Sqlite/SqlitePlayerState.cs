using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Wallet;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>
/// The save's fighter, wallet and round-progress fragment: reads serve the snapshot's stored rows and
/// writes mutate the whole snapshot under one transaction, keeping wallet changes serialized with
/// purchases by construction.
/// </summary>
internal sealed partial class SqliteRepositories : IFighterRepository, IPlayerWalletRepository, IPlayerProgressRepository
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
