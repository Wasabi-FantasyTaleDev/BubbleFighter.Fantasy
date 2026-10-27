using BubbleFighter.Fantasy.Contracts.Messenger;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>A <c>messenger_users</c> row (schema version 7): one messenger account of one tenant.</summary>
[SugarTable("messenger_users")]
internal sealed class MessengerUserRow
{
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    [SugarColumn(ColumnName = "tenant")]
    public string Tenant { get; set; } = "";

    [SugarColumn(ColumnName = "subject")]
    public string Subject { get; set; } = "";

    [SugarColumn(ColumnName = "nickname")]
    public string Nickname { get; set; } = "";

    [SugarColumn(ColumnName = "nickname_key")]
    public string NicknameKey { get; set; } = "";

    [SugarColumn(ColumnName = "game_code")]
    public long GameCode { get; set; }
}

/// <summary>A <c>messenger_relations</c> row: one direction of a friendship, owned by <see cref="Owner"/>.</summary>
[SugarTable("messenger_relations")]
internal sealed class MessengerRelationRow
{
    [SugarColumn(ColumnName = "owner", IsPrimaryKey = true)]
    public long Owner { get; set; }

    [SugarColumn(ColumnName = "friend", IsPrimaryKey = true)]
    public long Friend { get; set; }

    [SugarColumn(ColumnName = "category")]
    public int Category { get; set; }

    [SugarColumn(ColumnName = "blocked")]
    public int Blocked { get; set; }
}

/// <summary>A <c>messenger_categories</c> row: one category of one owner.</summary>
[SugarTable("messenger_categories")]
internal sealed class MessengerCategoryRow
{
    [SugarColumn(ColumnName = "owner", IsPrimaryKey = true)]
    public long Owner { get; set; }

    [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
    public int Id { get; set; }

    [SugarColumn(ColumnName = "name")]
    public string Name { get; set; } = "";

    [SugarColumn(ColumnName = "game_code")]
    public long GameCode { get; set; }
}

/// <summary>A <c>messenger_requests</c> row: one pending friend request; <c>created_at</c> is Unix milliseconds.</summary>
[SugarTable("messenger_requests")]
internal sealed class MessengerRequestRow
{
    [SugarColumn(ColumnName = "handle", IsPrimaryKey = true, IsIdentity = true)]
    public long Handle { get; set; }

    [SugarColumn(ColumnName = "sender")]
    public long Sender { get; set; }

    [SugarColumn(ColumnName = "recipient")]
    public long Recipient { get; set; }

    [SugarColumn(ColumnName = "message")]
    public string Message { get; set; } = "";

    [SugarColumn(ColumnName = "created_at")]
    public long CreatedAt { get; set; }
}

/// <summary>
/// The messenger's state in the save (schema version 7). Every method is one transaction of the owned connection, so a
/// friendship is created or ended for both accounts in the same change and every bound is checked inside the change it
/// limits. Accounts are bounded by the save's player capacity.
/// </summary>
internal sealed class SqliteMessengerStore(SqliteDatabase database) : IMessengerStore
{
    public ValueTask<MessengerUser> UpsertUserAsync(MessengerIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new(database.Write((db, _) =>
        {
            var row = db.Orm.Queryable<MessengerUserRow>().Where(u => u.Tenant == identity.Tenant && u.Subject == identity.Subject).ToList().FirstOrDefault();
            if (row is null)
            {
                if (db.Orm.Queryable<MessengerUserRow>().Count() >= database.Options.MaxPlayers)
                    throw new InvalidOperationException("Messenger account storage is full.");
                row = new MessengerUserRow { Tenant = identity.Tenant, Subject = identity.Subject };
                Apply(row, identity);
                row.Id = db.Orm.Insertable(row).ExecuteReturnBigIdentity();
            }
            else
            {
                Apply(row, identity);
                db.Orm.Updateable(row).ExecuteCommand();
            }
            return ToUser(row);
        }, cancellationToken));
    }

    public ValueTask<MessengerUser?> FindUserAsync(MessengerUserId id, CancellationToken cancellationToken) => new(database.Read(db =>
    {
        var row = db.Orm.Queryable<MessengerUserRow>().Where(u => u.Id == id.Value).ToList().FirstOrDefault();
        return row is null ? null : ToUser(row);
    }, cancellationToken));

    public ValueTask<MessengerUser?> FindUserByNicknameAsync(string tenant, string nickname, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nickname);
        var key = NicknameKey(nickname);
        return new(database.Read(db =>
        {
            var rows = db.Orm.Queryable<MessengerUserRow>().Where(u => u.Tenant == tenant && u.NicknameKey == key).Take(2).ToList();
            return rows.Count == 1 ? ToUser(rows[0]) : null;
        }, cancellationToken));
    }

    public ValueTask<MessengerRelations> LoadRelationsAsync(MessengerUserId owner, CancellationToken cancellationToken) => new(database.Read(db =>
    {
        var relations = db.Orm.Queryable<MessengerRelationRow>().Where(r => r.Owner == owner.Value).OrderBy(r => r.Friend).ToList();
        var friendIds = relations.Select(r => r.Friend).ToList();
        var users = db.Orm.Queryable<MessengerUserRow>().Where(u => friendIds.Contains(u.Id)).ToList().ToDictionary(u => u.Id);
        var blockers = db.Orm.Queryable<MessengerRelationRow>().Where(r => r.Friend == owner.Value && r.Blocked == 1).ToList()
            .Select(r => r.Owner).ToHashSet();
        var categories = db.Orm.Queryable<MessengerCategoryRow>().Where(c => c.Owner == owner.Value).OrderBy(c => c.Id).ToList();
        return new MessengerRelations(
            relations.Select(r => new MessengerRelation(ToUser(users[r.Friend]), r.Category, r.Blocked == 1, blockers.Contains(r.Friend))).ToArray(),
            categories.Select(c => new MessengerCategory(c.Id, c.Name, checked((uint)c.GameCode))).ToArray());
    }, cancellationToken));

    public ValueTask<IReadOnlyList<MessengerStoredRequest>> ListIncomingRequestsAsync(MessengerUserId recipient, CancellationToken cancellationToken) =>
        new(database.Read(db => (IReadOnlyList<MessengerStoredRequest>)db.Orm.Queryable<MessengerRequestRow>()
            .Where(r => r.Recipient == recipient.Value).OrderBy(r => r.Handle).ToList().Select(ToRequest).ToArray(), cancellationToken));

    public ValueTask<MessengerRequestOutcome> CreateRequestAsync(
        MessengerUserId from, MessengerUserId to, string message, DateTime now, MessengerLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(limits);
        var createdAt = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        return new(database.Write<MessengerRequestOutcome>((db, _) =>
        {
            if (db.Orm.Queryable<MessengerRelationRow>().Where(r => r.Owner == from.Value && r.Friend == to.Value).Count() != 0)
                return new MessengerRequestOutcome(MessengerResult.AlreadyFriends, null);
            var pending = db.Orm.Queryable<MessengerRequestRow>()
                .Where(r => r.Sender == from.Value && r.Recipient == to.Value || r.Sender == to.Value && r.Recipient == from.Value)
                .ToList().FirstOrDefault();
            if (pending is not null) return new MessengerRequestOutcome(MessengerResult.AlreadyPending, ToRequest(pending));
            if (db.Orm.Queryable<MessengerRequestRow>().Where(r => r.Recipient == to.Value).Count() >= limits.MaxPendingRequests)
                return new MessengerRequestOutcome(MessengerResult.LimitReached, null);
            var row = new MessengerRequestRow { Sender = from.Value, Recipient = to.Value, Message = message, CreatedAt = createdAt };
            row.Handle = db.Orm.Insertable(row).ExecuteReturnBigIdentity();
            if (row.Handle is <= 0 or > int.MaxValue) throw new InvalidOperationException("Messenger request handles are exhausted.");
            return new MessengerRequestOutcome(MessengerResult.Ok, ToRequest(row));
        }, cancellationToken));
    }

    public ValueTask<MessengerRequestOutcome> ResolveRequestAsync(
        int handle, MessengerUserId recipient, bool accept, MessengerLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return new(database.Write<MessengerRequestOutcome>((db, _) =>
        {
            var row = db.Orm.Queryable<MessengerRequestRow>().Where(r => r.Handle == handle && r.Recipient == recipient.Value).ToList().FirstOrDefault();
            if (row is null) return new MessengerRequestOutcome(MessengerResult.NotFound, null);
            var request = ToRequest(row);
            if (accept)
            {
                if (FriendCount(db, row.Sender) >= limits.MaxFriends || FriendCount(db, row.Recipient) >= limits.MaxFriends)
                    return new MessengerRequestOutcome(MessengerResult.LimitReached, request);
                db.Orm.Insertable(new[]
                {
                    new MessengerRelationRow { Owner = row.Sender, Friend = row.Recipient },
                    new MessengerRelationRow { Owner = row.Recipient, Friend = row.Sender },
                }).ExecuteCommand();
            }
            db.Orm.Deleteable<MessengerRequestRow>().Where(r => r.Handle == row.Handle).ExecuteCommand();
            return new MessengerRequestOutcome(MessengerResult.Ok, request);
        }, cancellationToken));
    }

    public ValueTask<bool> DeleteFriendshipAsync(MessengerUserId owner, MessengerUserId friend, CancellationToken cancellationToken) =>
        new(database.Write((db, _) => db.Orm.Deleteable<MessengerRelationRow>()
            .Where(r => r.Owner == owner.Value && r.Friend == friend.Value || r.Owner == friend.Value && r.Friend == owner.Value)
            .ExecuteCommand() > 0, cancellationToken));

    public ValueTask<bool> SetBlockedAsync(MessengerUserId owner, MessengerUserId friend, bool blocked, CancellationToken cancellationToken)
    {
        var flag = blocked ? 1 : 0;
        return new(database.Write((db, _) => db.Orm.Updateable<MessengerRelationRow>()
            .SetColumns(r => new MessengerRelationRow { Blocked = flag })
            .Where(r => r.Owner == owner.Value && r.Friend == friend.Value).ExecuteCommand() == 1, cancellationToken));
    }

    public ValueTask<MessengerResult> SetFriendCategoryAsync(MessengerUserId owner, MessengerUserId friend, int expected, int target, CancellationToken cancellationToken) =>
        new(database.Write((db, _) =>
        {
            if (target != 0 && db.Orm.Queryable<MessengerCategoryRow>().Where(c => c.Owner == owner.Value && c.Id == target).Count() == 0)
                return MessengerResult.NotFound;
            return db.Orm.Updateable<MessengerRelationRow>().SetColumns(r => new MessengerRelationRow { Category = target })
                .Where(r => r.Owner == owner.Value && r.Friend == friend.Value && r.Category == expected).ExecuteCommand() == 1
                ? MessengerResult.Ok : MessengerResult.NotFound;
        }, cancellationToken));

    public ValueTask<MessengerCategoryOutcome> AddCategoryAsync(
        MessengerUserId owner, string name, uint gameCode, MessengerLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(limits);
        return new(database.Write<MessengerCategoryOutcome>((db, _) =>
        {
            var used = db.Orm.Queryable<MessengerCategoryRow>().Where(c => c.Owner == owner.Value).Select(c => c.Id).ToList().ToHashSet();
            if (used.Count >= limits.MaxCategories) return new MessengerCategoryOutcome(MessengerResult.LimitReached, null);
            var id = 1;
            while (used.Contains(id)) id++;
            db.Orm.Insertable(new MessengerCategoryRow { Owner = owner.Value, Id = id, Name = name, GameCode = gameCode }).ExecuteCommand();
            return new MessengerCategoryOutcome(MessengerResult.Ok, new MessengerCategory(id, name, gameCode));
        }, cancellationToken));
    }

    public ValueTask<MessengerResult> RenameCategoryAsync(MessengerUserId owner, int categoryId, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new(database.Write((db, _) => db.Orm.Updateable<MessengerCategoryRow>().SetColumns(c => new MessengerCategoryRow { Name = name })
            .Where(c => c.Owner == owner.Value && c.Id == categoryId).ExecuteCommand() == 1 ? MessengerResult.Ok : MessengerResult.NotFound, cancellationToken));
    }

    public ValueTask<MessengerResult> DeleteCategoryAsync(MessengerUserId owner, int categoryId, CancellationToken cancellationToken) =>
        new(database.Write((db, _) =>
        {
            if (db.Orm.Queryable<MessengerCategoryRow>().Where(c => c.Owner == owner.Value && c.Id == categoryId).Count() == 0) return MessengerResult.NotFound;
            if (db.Orm.Queryable<MessengerRelationRow>().Where(r => r.Owner == owner.Value && r.Category == categoryId).Count() != 0) return MessengerResult.NotEmpty;
            db.Orm.Deleteable<MessengerCategoryRow>().Where(c => c.Owner == owner.Value && c.Id == categoryId).ExecuteCommand();
            return MessengerResult.Ok;
        }, cancellationToken));

    public ValueTask<int> PruneRequestsAsync(DateTime cutoff, CancellationToken cancellationToken)
    {
        var limit = new DateTimeOffset(DateTime.SpecifyKind(cutoff, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        return new(database.Write((db, _) => db.Orm.Deleteable<MessengerRequestRow>().Where(r => r.CreatedAt <= limit).ExecuteCommand(), cancellationToken));
    }

    /// <summary>The invariant upper-case form a nickname is matched by, so every store ignores case the same way.</summary>
    internal static string NicknameKey(string nickname) => nickname.ToUpperInvariant();

    private static int FriendCount(SqliteDatabase db, long owner) => db.Orm.Queryable<MessengerRelationRow>().Where(r => r.Owner == owner).Count();

    private static void Apply(MessengerUserRow row, MessengerIdentity identity)
    {
        row.Nickname = identity.Nickname;
        row.NicknameKey = NicknameKey(identity.Nickname);
        row.GameCode = identity.GameCode;
    }

    private static MessengerUser ToUser(MessengerUserRow row) =>
        new(new(row.Id), row.Tenant, row.Subject, row.Nickname, checked((uint)row.GameCode));

    private static MessengerStoredRequest ToRequest(MessengerRequestRow row) =>
        new(checked((int)row.Handle), new(row.Sender), new(row.Recipient), row.Message, DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAt).UtcDateTime);
}
