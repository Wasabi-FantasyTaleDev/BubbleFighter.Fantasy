using BubbleFighter.Fantasy.Contracts.Progress;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>Tutorial-room stage clears in the save's <c>tutorial_stage_clears</c> table.</summary>
internal sealed class SqliteTutorialStages(SqliteDatabase database) : ITutorialStageStore
{
    public ValueTask<IReadOnlyList<TutorialStageClear>> ListAsync(long userNum, CancellationToken ct) => new(database.Read(db =>
    {
        var rows = db.Orm.Queryable<TutorialStageClearRow>().Where(row => row.UserNum == userNum).OrderBy(row => row.Stage).ToList();
        return (IReadOnlyList<TutorialStageClear>)rows.Select(row => new TutorialStageClear(row.Stage, row.BestCount, row.BestSeconds)).ToList();
    }, ct));

    public ValueTask<bool> RecordClearAsync(long userNum, TutorialStageClear clear, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clear);
        return new(database.Write((db, transaction) =>
        {
            // SqlSugar has no insert-ignore; the primary key makes the first clear the only insert that changes a row.
            if (db.RawExecute("""
                INSERT OR IGNORE INTO tutorial_stage_clears(user_num,stage,best_count,best_seconds,first_cleared_at)
                VALUES($user,$stage,$count,$seconds,$now)
                """, transaction, ("$user", userNum), ("$stage", clear.Stage), ("$count", clear.BestCount),
                ("$seconds", clear.BestSeconds), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) == 1)
                return true;
            // The later clear keeps the better records: the higher count, and the shorter time once a time is known.
            var row = db.Orm.Queryable<TutorialStageClearRow>()
                .Where(stored => stored.UserNum == userNum && stored.Stage == clear.Stage).ToList().FirstOrDefault();
            if (row is null) return false;
            var bestCount = Math.Max(row.BestCount, clear.BestCount);
            var bestSeconds = row.BestSeconds <= 0 ? clear.BestSeconds
                : clear.BestSeconds <= 0 ? row.BestSeconds : Math.Min(row.BestSeconds, clear.BestSeconds);
            db.Orm.Updateable<TutorialStageClearRow>()
                .SetColumns(stored => new TutorialStageClearRow { BestCount = bestCount, BestSeconds = bestSeconds })
                .Where(stored => stored.UserNum == userNum && stored.Stage == clear.Stage).ExecuteCommand();
            return false;
        }, ct));
    }
}
