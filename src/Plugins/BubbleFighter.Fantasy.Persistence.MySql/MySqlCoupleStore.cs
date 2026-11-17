using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Couples;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>Stores build-neutral couples under ordered player locks, with unique membership across either partner position.</summary>
internal sealed class MySqlCoupleStore(IDbConnectionFactory connections, CoupleInvitationLimits limits,
    IAccountMutationFence? authority = null) : DurableCoupleStore(limits)
{
    protected override async ValueTask<CoupleRelationship?> FindAsync(CoupleAccount member, CancellationToken ct)
    {
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        return (await Find(session.Db, member, ct).ConfigureAwait(false))?.Relationship();
    }

    // One statement reads membership and relationship together, including while another node breaks up the pair.
    internal static ISugarQueryable<CoupleRow> MemberQuery(SqlSugarClient db, CoupleAccount member) =>
        db.Queryable<CoupleRow, CoupleMemberRow>((pair, entry) => pair.Id == entry.CoupleId)
            .Where((pair, entry) => entry.Authority == member.Authority && entry.UserNum == member.UserNum)
            .Select((pair, entry) => pair);

    private static async ValueTask<CoupleRow?> Find(SqlSugarClient db, CoupleAccount member, CancellationToken ct) =>
        (await MemberQuery(db, member).ToListAsync(ct).ConfigureAwait(false)).SingleOrDefault();

    protected override async ValueTask<CoupleStoreOutcome> FormAsync(CoupleInvitation invitation, DateTimeOffset now, CancellationToken ct)
    {
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginWriteAsync(invitation.Recipient.UserNum, authority,
            System.Data.IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        var db = session.Db;
        await LockPair(db, invitation.Sender.UserNum, invitation.Recipient.UserNum, ct).ConfigureAwait(false);
        var first = await Find(db, invitation.Sender, ct).ConfigureAwait(false);
        var second = await Find(db, invitation.Recipient, ct).ConfigureAwait(false);
        if (first is not null || second is not null)
            return first?.Id == invitation.Id.ToString("D") && second?.Id == first.Id
                ? new(CoupleStoreResult.Accepted, Relationship: first.Relationship()) : new(CoupleStoreResult.AlreadyPaired);
        var row = CoupleRow.From(invitation, now);
        if (await db.Queryable<CoupleRow>().Where(r => r.Id == row.Id).AnyAsync(ct).ConfigureAwait(false))
            return new(CoupleStoreResult.Conflict);
        await db.Insertable(row).ExecuteCommandAsync(ct).ConfigureAwait(false);
        await db.Insertable(new[]
        {
            new CoupleMemberRow { Authority = row.Authority, UserNum = row.FirstUser, CoupleId = row.Id },
            new CoupleMemberRow { Authority = row.Authority, UserNum = row.SecondUser, CoupleId = row.Id },
        }).ExecuteCommandAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return new(CoupleStoreResult.Accepted, Relationship: row.Relationship());
    }

    protected override async ValueTask<CoupleStoreOutcome> ChangeAsync(CoupleAccount member, Guid id, int? ring, CancellationToken ct)
    {
        var observed = await FindAsync(member, ct).ConfigureAwait(false);
        if (observed is null) return new(CoupleStoreResult.NotFound);
        if (observed.Id != id) return new(CoupleStoreResult.Conflict);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginWriteAsync(member.UserNum, authority,
            System.Data.IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        var db = session.Db;
        await LockPair(db, observed.First.UserNum, observed.Second.UserNum, ct).ConfigureAwait(false);
        var row = await Find(db, member, ct).ConfigureAwait(false);
        if (row is null) return new(CoupleStoreResult.NotFound);
        if (row.Id != id.ToString("D")) return new(CoupleStoreResult.Conflict);
        if (ring is null)
            await db.Deleteable<CoupleRow>().Where(r => r.Id == row.Id).ExecuteCommandAsync(ct).ConfigureAwait(false);
        else
        {
            row.RingItem = ring.Value;
            await db.Updateable(row).ExecuteCommandAsync(ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return ring is null ? new(CoupleStoreResult.BrokenUp)
            : new(CoupleStoreResult.Accepted, Relationship: row.Relationship());
    }

    private static async ValueTask LockPair(SqlSugarClient db, long first, long second, CancellationToken ct)
    {
        // Lock existing player rows even when no membership exists yet; competing proposals cannot both see an empty pair.
        await MySqlPlayerLock.AcquireAsync(db, Math.Min(first, second), ct).ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, Math.Max(first, second), ct).ConfigureAwait(false);
    }
}
