using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Wallet;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>
/// Keeps each player's current-day plaza mini-game record in <c>plaza_game_records</c> (migration 0048); a settlement
/// writes the record and <c>player_wallets</c> in one transaction.
/// </summary>
public sealed class MySqlPlazaGameRecords(IDbConnectionFactory connections, IAccountMutationFence? authority = null) : IPlazaGameRecordStore
{
    // INSERT IGNORE stays raw: SqlSugar has no IGNORE form. It makes sure the row exists before it is locked.
    private const string EnsureRowSql = """
        INSERT IGNORE INTO plaza_game_records (user_num, record_day, fishing_count, fortune_paper)
        VALUES (@user, @day, 0, 0)
        """;

    /// <inheritdoc />
    public async ValueTask<PlazaGameRecord> ReadAsync(long userNum, DateOnly day, CancellationToken ct)
    {
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var rows = await session.Db.Queryable<PlazaGameRecordRow>().Where(r => r.UserNum == userNum)
            .Take(1).ToListAsync(ct).ConfigureAwait(false);
        return Current(rows.FirstOrDefault(), day);
    }

    /// <inheritdoc />
    public async ValueTask<PlazaGameSettled> SettleAsync(PlazaGameSettlement settlement, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        settlement.Validate();
        var userNum = settlement.SeedWallet.UserNum;
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginWriteAsync(userNum, authority, ct).ConfigureAwait(false);
        var db = session.Db;
        // Same lock order as a shop purchase (player lock, then wallet row), so the two never deadlock.
        await MySqlPlayerLock.AcquireAsync(db, userNum, ct).ConfigureAwait(false);
        var seed = new WalletRow { UserNum = userNum, Lucci = settlement.SeedWallet.Lucci, Cash = settlement.SeedWallet.Cash };
        await SugarSql.UpsertAsync(db, seed, [nameof(WalletRow.UpdatedAt)], [nameof(WalletRow.UserNum)], [], ct)
            .ConfigureAwait(false);
        var wallets = await db.Queryable<WalletRow>().Where(w => w.UserNum == userNum).Take(1)
            .TranLock(DbLockType.Wait).ToListAsync(ct).ConfigureAwait(false);
        if (wallets.Count != 1)
            throw new InvalidOperationException($"player_wallets row for user_num {userNum} is missing after the seed insert.");
        var dayValue = settlement.Day.ToDateTime(TimeOnly.MinValue);
        await SugarSql.ExecuteOnConnectionAsync(db, EnsureRowSql, [("@user", userNum), ("@day", dayValue)], ct).ConfigureAwait(false);
        var rows = await db.Queryable<PlazaGameRecordRow>().Where(r => r.UserNum == userNum)
            .TranLock(DbLockType.Wait).ToListAsync(ct).ConfigureAwait(false);
        var settled = settlement.Apply(Current(rows.Single(), settlement.Day),
            new PlayerWallet(wallets[0].UserNum, wallets[0].Lucci, wallets[0].Cash));
        if (settled.Status != PlazaGameSettlementStatus.Settled)
        {
            await transaction.CommitAsync().ConfigureAwait(false);
            return settled;
        }
        var written = await db.Updateable(new PlazaGameRecordRow
        {
            UserNum = userNum, RecordDay = dayValue, FishingCount = settled.Record.FishingCount,
            FortunePaper = settled.Record.FortunePaper,
        }).ExecuteCommandAsync(ct).ConfigureAwait(false);
        if (written > 1)
            throw new InvalidOperationException($"plaza_game_records update for user_num {userNum} wrote {written} rows.");
        await MySqlShopPurchaseStore.ChargeUpdate(db, settled.Wallet).ExecuteCommandAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return settled;
    }

    // A row of an earlier day is history; the current day starts empty.
    private static PlazaGameRecord Current(PlazaGameRecordRow? row, DateOnly day) =>
        row is not null && DateOnly.FromDateTime(row.RecordDay) == day
            ? new PlazaGameRecord(day, row.FishingCount, row.FortunePaper) : PlazaGameRecord.Empty(day);
}
