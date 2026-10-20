using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Shop;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>Last-open persistence over the <c>shop_sessions</c> table from migration 0011.</summary>
public sealed class MySqlShopSessionStore(IDbConnectionFactory connections) : IShopSessionStore
{
    /// <inheritdoc />
    public async ValueTask RememberOpenAsync(ShopOpenRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfNegative(record.UserNum);
        if (!Enum.IsDefined(record.Kind))
            throw new ArgumentOutOfRangeException(nameof(record), record.Kind, "Shop kind is not defined.");
        if (record.UserNum == 0)
            return;

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var row = new ShopSessionRow
        {
            UserNum = record.UserNum,
            Token = record.Token,
            ShopType = record.ShopType,
            Kind = (int)record.Kind,
        };
        // opened_at takes the column default on insert and CURRENT_TIMESTAMP on update.
        var rows = await SugarSql.UpsertAsync(session.Db, row, [nameof(ShopSessionRow.OpenedAt)],
            [nameof(ShopSessionRow.Token), nameof(ShopSessionRow.ShopType), nameof(ShopSessionRow.Kind)],
            [(nameof(ShopSessionRow.OpenedAt), "CURRENT_TIMESTAMP")], ct).ConfigureAwait(false);
        // Reopening with identical values in the same second may report zero affected rows.
        if (rows < 0 || rows > 2)
            throw new InvalidOperationException($"shop_sessions upsert for user_num {record.UserNum} wrote {rows} row(s).");
    }

    /// <inheritdoc />
    public async ValueTask<ShopOpenRecord?> FindLastOpenAsync(long userNum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(userNum);
        if (userNum == 0)
            return null;
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var rows = await session.Db.Queryable<ShopSessionRow>()
            .Where(s => s.UserNum == userNum)
            .Take(1)
            .ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count == 0)
            return null;
        return ToRecord(userNum, rows[0]);
    }

    /// <summary>Maps a stored row to the contract record, failing on a kind the contract does not define.</summary>
    internal static ShopOpenRecord ToRecord(long userNum, ShopSessionRow row)
    {
        if (!Enum.IsDefined((ShopKind)row.Kind))
            throw new InvalidOperationException($"shop_sessions for user_num {userNum} has invalid kind {row.Kind}.");
        return new ShopOpenRecord(userNum, row.Token, row.ShopType, (ShopKind)row.Kind);
    }
}
