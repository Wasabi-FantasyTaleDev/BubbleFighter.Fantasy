using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Messenger;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>A <c>messenger_users</c> row (migration 0034): one messenger account of one tenant.</summary>
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
    public uint GameCode { get; set; }
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
    public byte Blocked { get; set; }
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
    public uint GameCode { get; set; }
}

/// <summary>A <c>messenger_requests</c> row: one pending friend request; <c>created_at</c> is Unix milliseconds.</summary>
[SugarTable("messenger_requests")]
internal sealed class MessengerRequestRow
{
    [SugarColumn(ColumnName = "handle", IsPrimaryKey = true, IsIdentity = true)]
    public int Handle { get; set; }

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
/// Keeps the messenger in the <c>messenger_*</c> tables (migration 0034). A change that involves two accounts — creating
/// or resolving a request — takes both account rows' locks in id order inside one transaction before it reads the
/// state it checks, so two opposite requests cannot both be created and two accepts cannot pass a friend bound
/// together; a category change takes its owner's row lock, so a move and a delete of the same category serialize.
/// </summary>
public sealed class MySqlMessengerStore(IDbConnectionFactory connections) : IMessengerStore
{
    /// <inheritdoc />
    public async ValueTask<MessengerUser> UpsertUserAsync(MessengerIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        var db = session.Db;
        // The unique (tenant, subject) key makes a concurrent first sign-in update the row the other one inserted.
        await db.Ado.ExecuteCommandAsync("""
            INSERT INTO messenger_users (tenant, subject, nickname, nickname_key, game_code)
            VALUES (@tenant, @subject, @nickname, @key, @game)
            ON DUPLICATE KEY UPDATE nickname = VALUES(nickname), nickname_key = VALUES(nickname_key), game_code = VALUES(game_code)
            """,
            new SugarParameter("@tenant", identity.Tenant), new SugarParameter("@subject", identity.Subject),
            new SugarParameter("@nickname", identity.Nickname), new SugarParameter("@key", NicknameKey(identity.Nickname)),
            new SugarParameter("@game", identity.GameCode)).ConfigureAwait(false);
        var rows = await db.Queryable<MessengerUserRow>().Where(u => u.Tenant == identity.Tenant && u.Subject == identity.Subject)
            .Take(1).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count == 1 ? ToUser(rows[0]) : throw new InvalidOperationException("messenger_users upsert left no row.");
    }

    /// <inheritdoc />
    public async ValueTask<MessengerUser?> FindUserAsync(MessengerUserId id, CancellationToken cancellationToken)
    {
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        var rows = await session.Db.Queryable<MessengerUserRow>().Where(u => u.Id == id.Value).Take(1).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : ToUser(rows[0]);
    }

    /// <inheritdoc />
    public async ValueTask<MessengerUser?> FindUserByNicknameAsync(string tenant, string nickname, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nickname);
        var key = NicknameKey(nickname);
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        var rows = await session.Db.Queryable<MessengerUserRow>().Where(u => u.Tenant == tenant && u.NicknameKey == key).Take(2)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count == 1 ? ToUser(rows[0]) : null;
    }

    /// <inheritdoc />
    public async ValueTask<MessengerRelations> LoadRelationsAsync(MessengerUserId owner, CancellationToken cancellationToken)
    {
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        var db = session.Db;
        var relations = await db.Queryable<MessengerRelationRow>().Where(r => r.Owner == owner.Value).OrderBy(r => r.Friend)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var friendIds = relations.Select(r => r.Friend).ToList();
        var users = friendIds.Count == 0 ? [] : (await db.Queryable<MessengerUserRow>().Where(u => friendIds.Contains(u.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.Id);
        var blockers = (await db.Queryable<MessengerRelationRow>().Where(r => r.Friend == owner.Value && r.Blocked == 1)
            .Select(r => r.Owner).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var categories = await db.Queryable<MessengerCategoryRow>().Where(c => c.Owner == owner.Value).OrderBy(c => c.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new MessengerRelations(
            relations.Select(r => new MessengerRelation(ToUser(users[r.Friend]), r.Category, r.Blocked == 1, blockers.Contains(r.Friend))).ToArray(),
            categories.Select(c => new MessengerCategory(c.Id, c.Name, c.GameCode)).ToArray());
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MessengerStoredRequest>> ListIncomingRequestsAsync(MessengerUserId recipient, CancellationToken cancellationToken)
    {
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        var rows = await session.Db.Queryable<MessengerRequestRow>().Where(r => r.Recipient == recipient.Value).OrderBy(r => r.Handle)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToRequest).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<MessengerRequestOutcome> CreateRequestAsync(
        MessengerUserId from, MessengerUserId to, string message, DateTime now, MessengerLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(limits);
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginAsync().ConfigureAwait(false);
        await LockUsersAsync(db, cancellationToken, from.Value, to.Value).ConfigureAwait(false);
        if (await db.Queryable<MessengerRelationRow>().Where(r => r.Owner == from.Value && r.Friend == to.Value).CountAsync(cancellationToken).ConfigureAwait(false) != 0)
            return new MessengerRequestOutcome(MessengerResult.AlreadyFriends, null);
        var pending = await db.Queryable<MessengerRequestRow>()
            .Where(r => r.Sender == from.Value && r.Recipient == to.Value || r.Sender == to.Value && r.Recipient == from.Value)
            .Take(1).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Count != 0) return new MessengerRequestOutcome(MessengerResult.AlreadyPending, ToRequest(pending[0]));
        if (await db.Queryable<MessengerRequestRow>().Where(r => r.Recipient == to.Value).CountAsync(cancellationToken).ConfigureAwait(false) >= limits.MaxPendingRequests)
            return new MessengerRequestOutcome(MessengerResult.LimitReached, null);
        var row = new MessengerRequestRow
        {
            Sender = from.Value, Recipient = to.Value, Message = message,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
        };
        row.Handle = await db.Insertable(row).ExecuteReturnIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (row.Handle <= 0) throw new InvalidOperationException("messenger_requests insert returned no handle.");
        await tx.CommitAsync().ConfigureAwait(false);
        return new MessengerRequestOutcome(MessengerResult.Ok, ToRequest(row));
    }

    /// <inheritdoc />
    public async ValueTask<MessengerRequestOutcome> ResolveRequestAsync(
        int handle, MessengerUserId recipient, bool accept, MessengerLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginAsync().ConfigureAwait(false);
        var found = await db.Queryable<MessengerRequestRow>().Where(r => r.Handle == handle && r.Recipient == recipient.Value)
            .Take(1).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (found.Count == 0) return new MessengerRequestOutcome(MessengerResult.NotFound, null);
        var row = found[0];
        await LockUsersAsync(db, cancellationToken, row.Sender, row.Recipient).ConfigureAwait(false);
        // Re-read under the locks: a concurrent resolve of the same request removes it first.
        if (await db.Queryable<MessengerRequestRow>().Where(r => r.Handle == handle).CountAsync(cancellationToken).ConfigureAwait(false) == 0)
            return new MessengerRequestOutcome(MessengerResult.NotFound, null);
        var request = ToRequest(row);
        if (accept)
        {
            if (await FriendCountAsync(db, row.Sender, cancellationToken).ConfigureAwait(false) >= limits.MaxFriends ||
                await FriendCountAsync(db, row.Recipient, cancellationToken).ConfigureAwait(false) >= limits.MaxFriends)
                return new MessengerRequestOutcome(MessengerResult.LimitReached, request);
            var written = await db.Insertable(new[]
            {
                new MessengerRelationRow { Owner = row.Sender, Friend = row.Recipient },
                new MessengerRelationRow { Owner = row.Recipient, Friend = row.Sender },
            }).ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);
            if (written != 2) throw new InvalidOperationException($"messenger_relations insert wrote {written} row(s), expected 2.");
        }
        await db.Deleteable<MessengerRequestRow>().Where(r => r.Handle == handle).ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync().ConfigureAwait(false);
        return new MessengerRequestOutcome(MessengerResult.Ok, request);
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteFriendshipAsync(MessengerUserId owner, MessengerUserId friend, CancellationToken cancellationToken)
    {
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        // One statement removes both directions atomically.
        return await session.Db.Deleteable<MessengerRelationRow>()
            .Where(r => r.Owner == owner.Value && r.Friend == friend.Value || r.Owner == friend.Value && r.Friend == owner.Value)
            .ExecuteCommandAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async ValueTask<bool> SetBlockedAsync(MessengerUserId owner, MessengerUserId friend, bool blocked, CancellationToken cancellationToken)
    {
        var flag = (byte)(blocked ? 1 : 0);
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        // A matched row whose flag already has the value reports zero affected rows, so existence is checked first.
        var exists = await session.Db.Queryable<MessengerRelationRow>().Where(r => r.Owner == owner.Value && r.Friend == friend.Value)
            .CountAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (!exists) return false;
        await session.Db.Updateable<MessengerRelationRow>().SetColumns(r => new MessengerRelationRow { Blocked = flag })
            .Where(r => r.Owner == owner.Value && r.Friend == friend.Value).ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<MessengerResult> SetFriendCategoryAsync(MessengerUserId owner, MessengerUserId friend, int expected, int target, CancellationToken cancellationToken)
    {
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginAsync().ConfigureAwait(false);
        await LockUsersAsync(db, cancellationToken, owner.Value).ConfigureAwait(false);
        if (target != 0 && await db.Queryable<MessengerCategoryRow>().Where(c => c.Owner == owner.Value && c.Id == target)
                .CountAsync(cancellationToken).ConfigureAwait(false) == 0)
            return MessengerResult.NotFound;
        var current = await db.Queryable<MessengerRelationRow>().Where(r => r.Owner == owner.Value && r.Friend == friend.Value && r.Category == expected)
            .CountAsync(cancellationToken).ConfigureAwait(false);
        if (current != 1) return MessengerResult.NotFound;
        await db.Updateable<MessengerRelationRow>().SetColumns(r => new MessengerRelationRow { Category = target })
            .Where(r => r.Owner == owner.Value && r.Friend == friend.Value).ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync().ConfigureAwait(false);
        return MessengerResult.Ok;
    }

    /// <inheritdoc />
    public async ValueTask<MessengerCategoryOutcome> AddCategoryAsync(
        MessengerUserId owner, string name, uint gameCode, MessengerLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(limits);
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginAsync().ConfigureAwait(false);
        await LockUsersAsync(db, cancellationToken, owner.Value).ConfigureAwait(false);
        var used = (await db.Queryable<MessengerCategoryRow>().Where(c => c.Owner == owner.Value).Select(c => c.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        if (used.Count >= limits.MaxCategories) return new MessengerCategoryOutcome(MessengerResult.LimitReached, null);
        var id = 1;
        while (used.Contains(id)) id++;
        await db.Insertable(new MessengerCategoryRow { Owner = owner.Value, Id = id, Name = name, GameCode = gameCode })
            .ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync().ConfigureAwait(false);
        return new MessengerCategoryOutcome(MessengerResult.Ok, new MessengerCategory(id, name, gameCode));
    }

    /// <inheritdoc />
    public async ValueTask<MessengerResult> RenameCategoryAsync(MessengerUserId owner, int categoryId, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        var exists = await session.Db.Queryable<MessengerCategoryRow>().Where(c => c.Owner == owner.Value && c.Id == categoryId)
            .CountAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (!exists) return MessengerResult.NotFound;
        await session.Db.Updateable<MessengerCategoryRow>().SetColumns(c => new MessengerCategoryRow { Name = name })
            .Where(c => c.Owner == owner.Value && c.Id == categoryId).ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);
        return MessengerResult.Ok;
    }

    /// <inheritdoc />
    public async ValueTask<MessengerResult> DeleteCategoryAsync(MessengerUserId owner, int categoryId, CancellationToken cancellationToken)
    {
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginAsync().ConfigureAwait(false);
        await LockUsersAsync(db, cancellationToken, owner.Value).ConfigureAwait(false);
        if (await db.Queryable<MessengerCategoryRow>().Where(c => c.Owner == owner.Value && c.Id == categoryId).CountAsync(cancellationToken).ConfigureAwait(false) == 0)
            return MessengerResult.NotFound;
        if (await db.Queryable<MessengerRelationRow>().Where(r => r.Owner == owner.Value && r.Category == categoryId).CountAsync(cancellationToken).ConfigureAwait(false) != 0)
            return MessengerResult.NotEmpty;
        await db.Deleteable<MessengerCategoryRow>().Where(c => c.Owner == owner.Value && c.Id == categoryId).ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync().ConfigureAwait(false);
        return MessengerResult.Ok;
    }

    /// <inheritdoc />
    public async ValueTask<int> PruneRequestsAsync(DateTime cutoff, CancellationToken cancellationToken)
    {
        var limit = new DateTimeOffset(DateTime.SpecifyKind(cutoff, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        await using var session = await SugarSession.OpenAsync(connections, cancellationToken).ConfigureAwait(false);
        return await session.Db.Deleteable<MessengerRequestRow>().Where(r => r.CreatedAt <= limit).ExecuteCommandAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The invariant upper-case form a nickname is matched by, so every store ignores case the same way.</summary>
    internal static string NicknameKey(string nickname) => nickname.ToUpperInvariant();

    // Locks the given accounts' rows in ascending id order (one ordered locking read), the order every two-account change uses.
    private static async Task LockUsersAsync(SqlSugarClient db, CancellationToken cancellationToken, params long[] ids)
    {
        var distinct = ids.Distinct().Order().ToList();
        var locked = await db.Queryable<MessengerUserRow>().Where(u => distinct.Contains(u.Id)).OrderBy(u => u.Id).Select(u => u.Id)
            .TranLock(DbLockType.Wait).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (locked.Count != distinct.Count) throw new InvalidOperationException("A messenger account in the change does not exist.");
    }

    private static Task<int> FriendCountAsync(SqlSugarClient db, long owner, CancellationToken cancellationToken) =>
        db.Queryable<MessengerRelationRow>().Where(r => r.Owner == owner).CountAsync(cancellationToken);

    private static MessengerUser ToUser(MessengerUserRow row) => new(new(row.Id), row.Tenant, row.Subject, row.Nickname, row.GameCode);

    private static MessengerStoredRequest ToRequest(MessengerRequestRow row) =>
        new(row.Handle, new(row.Sender), new(row.Recipient), row.Message, DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAt).UtcDateTime);
}
