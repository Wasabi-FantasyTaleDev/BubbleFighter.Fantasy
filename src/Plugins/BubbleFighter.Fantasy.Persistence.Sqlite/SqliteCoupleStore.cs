using BubbleFighter.Fantasy.Contracts.Couples;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>Commits both couple memberships in the save transaction; pending consent is process-local.</summary>
internal sealed class SqliteCoupleStore(SqliteDatabase database) : DurableCoupleStore(database.Options.CoupleInvitations)
{
    protected override ValueTask<CoupleRelationship?> FindAsync(CoupleAccount member, CancellationToken ct) =>
        new(database.Read(db => Find(db, member)?.Relationship(), ct));

    private static CoupleRow? Find(SqliteDatabase db, CoupleAccount member)
    {
        var membership = db.Orm.Queryable<CoupleMemberRow>()
            .Where(row => row.Authority == member.Authority && row.UserNum == member.UserNum).ToList().SingleOrDefault();
        return membership is null ? null : db.Orm.Queryable<CoupleRow>()
            .Where(row => row.Id == membership.CoupleId).ToList().Single();
    }

    protected override ValueTask<CoupleStoreOutcome> FormAsync(CoupleInvitation invitation, DateTimeOffset now, CancellationToken ct) =>
        new(database.Write((db, _) =>
        {
            var first = Find(db, invitation.Sender);
            var second = Find(db, invitation.Recipient);
            if (first is not null || second is not null)
                return first?.Id == invitation.Id.ToString("D") && second?.Id == first.Id
                    ? new CoupleStoreOutcome(CoupleStoreResult.Accepted, Relationship: first.Relationship())
                    : new CoupleStoreOutcome(CoupleStoreResult.AlreadyPaired);
            var row = CoupleRow.From(invitation, now);
            if (db.Orm.Queryable<CoupleRow>().Where(r => r.Id == row.Id).Any()) return new(CoupleStoreResult.Conflict);
            db.Orm.Insertable(row).ExecuteCommand();
            db.Orm.Insertable(new[]
            {
                new CoupleMemberRow { Authority = row.Authority, UserNum = row.FirstUser, CoupleId = row.Id },
                new CoupleMemberRow { Authority = row.Authority, UserNum = row.SecondUser, CoupleId = row.Id },
            }).ExecuteCommand();
            return new(CoupleStoreResult.Accepted, Relationship: row.Relationship());
        }, ct));

    protected override ValueTask<CoupleStoreOutcome> ChangeAsync(CoupleAccount member, Guid id, int? ring, CancellationToken ct) =>
        new(database.Write((db, _) =>
        {
            var row = Find(db, member);
            if (row is null) return new CoupleStoreOutcome(CoupleStoreResult.NotFound);
            if (row.Id != id.ToString("D")) return new(CoupleStoreResult.Conflict);
            if (ring is null)
            {
                db.Orm.Deleteable<CoupleRow>().Where(r => r.Id == row.Id).ExecuteCommand();
                return new(CoupleStoreResult.BrokenUp);
            }
            row.RingItem = ring.Value;
            db.Orm.Updateable(row).ExecuteCommand();
            return new(CoupleStoreResult.Accepted, Relationship: row.Relationship());
        }, ct));
}
