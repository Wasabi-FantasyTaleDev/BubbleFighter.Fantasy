using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Clans;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>Clan open and membership persistence over the tables from migration 0015.</summary>
public sealed class MySqlClanStore(IDbConnectionFactory connections) : IClanStore
{
    /// <inheritdoc />
    public async ValueTask RememberOpenAsync(ClanOpenRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfNegative(record.UserNum);
        if (!Enum.IsDefined(record.Kind))
            throw new ArgumentOutOfRangeException(nameof(record), record.Kind, "Clan open kind is not defined.");
        if (record.UserNum == 0)
            return;

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var row = new ClanOpenRow { UserNum = record.UserNum, Kind = (int)record.Kind };
        // opened_at takes the column default on insert and CURRENT_TIMESTAMP on update.
        var rows = await SugarSql.UpsertAsync(session.Db, row, [nameof(ClanOpenRow.OpenedAt)],
            [nameof(ClanOpenRow.Kind)], [(nameof(ClanOpenRow.OpenedAt), "CURRENT_TIMESTAMP")], ct).ConfigureAwait(false);
        // A repeated open in the same second can be a no-op with UseAffectedRows enabled.
        if (rows < 0 || rows > 2)
            throw new InvalidOperationException($"clan_opens upsert for user_num {record.UserNum} wrote {rows} row(s).");
    }

    /// <inheritdoc />
    public async ValueTask<ClanMembership> GetMembershipAsync(long userNum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(userNum);
        if (userNum == 0)
            return new ClanMembership(userNum, 0);

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var clanIds = await session.Db.Queryable<ClanMemberRow>()
            .Where(m => m.UserNum == userNum)
            .Select(m => m.ClanId)
            .Take(1)
            .ToListAsync(ct).ConfigureAwait(false);
        return new ClanMembership(userNum, clanIds.Count == 0 ? 0 : clanIds[0]);
    }

    /// <inheritdoc />
    public async ValueTask<ClanOpenRecord?> FindLastOpenAsync(long userNum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(userNum);
        if (userNum == 0)
            return null;
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var kinds = await session.Db.Queryable<ClanOpenRow>()
            .Where(o => o.UserNum == userNum)
            .Select(o => o.Kind)
            .Take(1)
            .ToListAsync(ct).ConfigureAwait(false);
        if (kinds.Count == 0)
            return null;
        return ToRecord(userNum, kinds[0]);
    }

    /// <summary>Maps a stored kind to the contract record, failing on a kind the contract does not define.</summary>
    internal static ClanOpenRecord ToRecord(long userNum, int kind)
    {
        if (!Enum.IsDefined((ClanOpenKind)kind))
            throw new InvalidOperationException($"clan_opens for user_num {userNum} has invalid kind {kind}.");
        return new ClanOpenRecord(userNum, (ClanOpenKind)kind);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<long>> ListClanIdsAsync(int maxCount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateMaxCount(maxCount);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        // One extra row proves the table holds more than maxCount, which fails instead of truncating.
        var ids = await session.Db.Queryable<ClanRow>()
            .OrderBy(c => c.ClanId)
            .Select(c => c.ClanId)
            .Take(maxCount + 1)
            .ToListAsync(ct).ConfigureAwait(false);
        return BoundedSnapshot(ids, maxCount);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<long>> ListMemberUserNumsAsync(long clanId, int maxCount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateMaxCount(maxCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clanId);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var members = await session.Db.Queryable<ClanMemberRow>()
            .Where(m => m.ClanId == clanId)
            .OrderBy(m => m.UserNum)
            .Select(m => m.UserNum)
            .Take(maxCount + 1)
            .ToListAsync(ct).ConfigureAwait(false);
        return BoundedSnapshot(members, maxCount);
    }

    private static void ValidateMaxCount(int maxCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        ArgumentOutOfRangeException.ThrowIfEqual(maxCount, int.MaxValue);
    }

    internal static IReadOnlyList<long> BoundedSnapshot(List<long> values, int maxCount)
    {
        if (values.Count > maxCount)
            throw new InvalidOperationException($"Clan snapshot exceeds maxCount ({maxCount}).");
        return values;
    }
}
