using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Progress;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>Persists successful supplementary practice with the account's current mutation authority.</summary>
public sealed class MySqlTutorialPractice(IDbConnectionFactory connections, IAccountMutationFence? authority = null) : ITutorialPracticeStore
{
    /// <summary>Reads the committed practice identity from its client profile.</summary>
    public async ValueTask<bool> HasClearedAsync(TutorialPracticeKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        return await session.Db.Queryable<TutorialPracticeRow>()
            .Where(row => row.Profile == key.Profile && row.UserNum == key.UserNum && row.MapKey == key.MapKey)
            .AnyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Inserts a clear under the account write fence; a repeated clear leaves the existing identity intact.</summary>
    public async ValueTask<bool> RecordClearAsync(TutorialPracticeKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginWriteAsync(key.UserNum, authority, ct).ConfigureAwait(false);
        var inserted = await SugarSql.ExecuteOnConnectionAsync(session.Db,
            "INSERT IGNORE INTO tutorial_practice_clears(profile,user_num,map_key) VALUES(@profile,@user,@map)",
            [("@profile", key.Profile), ("@user", key.UserNum), ("@map", key.MapKey)], ct).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        return inserted == 1;
    }
}

[SugarTable("tutorial_practice_clears")]
internal sealed class TutorialPracticeRow
{
    [SugarColumn(ColumnName = "profile", IsPrimaryKey = true)]
    public string Profile { get; set; } = "";
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }
    [SugarColumn(ColumnName = "map_key", IsPrimaryKey = true)]
    public string MapKey { get; set; } = "";
}
