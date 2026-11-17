using BubbleFighter.Fantasy.Contracts.Progress;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>
/// Each player's current-day plaza mini-game record in the save's <c>plaza_game_records</c> table. A settlement
/// writes the record and the player's snapshot wallet in one write transaction.
/// </summary>
internal sealed class SqlitePlazaGameRecords(SqliteDatabase database) : IPlazaGameRecordStore
{
    public ValueTask<PlazaGameRecord> ReadAsync(long userNum, DateOnly day, CancellationToken ct) => new(database.Read(db =>
        Current(db.Orm.Queryable<PlazaGameRecordRow>().Where(row => row.UserNum == userNum).ToList().FirstOrDefault(), day), ct));

    // The save serializes writes, so reading both values and writing both inside one write transaction is atomic.
    public ValueTask<PlazaGameSettled> SettleAsync(PlazaGameSettlement settlement, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        settlement.Validate();
        var userNum = settlement.SeedWallet.UserNum;
        return new(database.Write((db, transaction) =>
        {
            var state = db.RequireState(userNum);
            var row = db.Orm.Queryable<PlazaGameRecordRow>().Where(stored => stored.UserNum == userNum).ToList().FirstOrDefault();
            var settled = settlement.Apply(Current(row, settlement.Day), state.Wallet ?? settlement.SeedWallet);
            if (settled.Status != PlazaGameSettlementStatus.Settled)
                return settled;
            db.RawExecute("""
                INSERT INTO plaza_game_records(user_num,record_day,fishing_count,fortune_paper) VALUES($user,$day,$count,$paper)
                ON CONFLICT(user_num) DO UPDATE SET record_day=excluded.record_day, fishing_count=excluded.fishing_count,
                    fortune_paper=excluded.fortune_paper
                """, transaction, ("$user", userNum), ("$day", settlement.Day.DayNumber), ("$count", settled.Record.FishingCount),
                ("$paper", unchecked((long)settled.Record.FortunePaper)));
            state.Wallet = settled.Wallet;
            db.Save(userNum, state);
            return settled;
        }, ct));
    }

    // A row of an earlier day is history; the current day starts empty.
    private static PlazaGameRecord Current(PlazaGameRecordRow? row, DateOnly day) =>
        row is not null && row.RecordDay == day.DayNumber
            ? new PlazaGameRecord(day, row.FishingCount, unchecked((ulong)row.FortunePaper)) : PlazaGameRecord.Empty(day);
}
