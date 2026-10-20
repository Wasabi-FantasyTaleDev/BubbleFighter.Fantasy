using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Progress;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>Keeps tutorial-room stage clears in <c>tutorial_stage_clears</c>.</summary>
public sealed class MySqlTutorialStages(IDbConnectionFactory connections) : ITutorialStageStore
{
    // INSERT IGNORE stays raw: SqlSugar has no IGNORE form, and its upsert would change which errors are swallowed.
    private const string InsertIgnoreSql = """
        INSERT IGNORE INTO tutorial_stage_clears (user_num, stage, best_count, best_seconds, first_cleared_at)
        VALUES (@user, @stage, @count, @seconds, @now)
        """;

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<TutorialStageClear>> ListAsync(long userNum, CancellationToken ct)
    {
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var rows = await session.Db.Queryable<TutorialStageRow>()
            .Where(t => t.UserNum == userNum)
            .OrderBy(t => t.Stage)
            .Select(t => new { t.Stage, t.BestCount, t.BestSeconds })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => new TutorialStageClear(r.Stage, r.BestCount, r.BestSeconds)).ToList();
    }

    /// <inheritdoc />
    public async ValueTask<bool> RecordClearAsync(long userNum, TutorialStageClear clear, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clear);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        // INSERT IGNORE reports one row only for the first clear; the UPDATE then keeps the better records.
        var inserted = await SugarSql.ExecuteOnConnectionAsync(db, InsertIgnoreSql,
            [("@user", userNum), ("@stage", clear.Stage), ("@count", clear.BestCount),
             ("@seconds", clear.BestSeconds), ("@now", DateTime.UtcNow)], ct).ConfigureAwait(false);
        if (inserted == 1)
            return true;

        await BestRecordUpdate(db, userNum, clear).ExecuteCommandAsync(ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Keeps the better record of an existing clear: the larger best count, and the smaller positive best time
    /// (a zero or negative stored time yields to a real one). Equal to the GREATEST/LEAST form it replaces.
    /// </summary>
    /// <param name="db">The client bound to the operation's connection.</param>
    /// <param name="userNum">The player.</param>
    /// <param name="clear">The clear whose values are compared with the stored record.</param>
    /// <returns>The update, not yet executed.</returns>
    internal static IUpdateable<TutorialStageRow> BestRecordUpdate(SqlSugarClient db, long userNum, TutorialStageClear clear)
    {
        var count = clear.BestCount;
        var seconds = clear.BestSeconds;
        return db.Updateable<TutorialStageRow>()
            .SetColumns(t => t.BestCount == SqlFunc.IIF(t.BestCount > count, t.BestCount, count))
            .SetColumns(t => t.BestSeconds == (t.BestSeconds <= 0 ? seconds
                : seconds <= 0 ? t.BestSeconds
                : t.BestSeconds < seconds ? t.BestSeconds : seconds))
            .Where(t => t.UserNum == userNum && t.Stage == clear.Stage);
    }
}
