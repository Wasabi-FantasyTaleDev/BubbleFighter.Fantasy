using BubbleFighter.Fantasy.Contracts.Clans;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>
/// Clans, member rows and board posts in the save file's <c>clans</c>, <c>clan_members</c> and <c>clan_posts</c>
/// tables (schema version 15). The save has a single writer, so every write runs as one transaction under its gate
/// and the name, one-row-per-player and capacity rules hold without further locking.
/// </summary>
/// <param name="database">The open save file.</param>
/// <param name="clock">The clock that stamps rows; the system clock when null.</param>
internal sealed class SqliteClanDirectory(SqliteDatabase database, TimeProvider? clock = null) : IClanDirectory
{
    private readonly SqliteDatabase _database = database ?? throw new ArgumentNullException(nameof(database));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private long Now => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <inheritdoc />
    public ValueTask<ClanRecord?> CreateAsync(string name, string introduction, int capacity, long founderUserNum,
        string founderNickname, int founderLevel, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(introduction);
        ArgumentNullException.ThrowIfNull(founderNickname);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(founderUserNum);
        return new(_database.Write((db, _) =>
        {
            if (Member(db, founderUserNum) is not null || ByName(db, name) is not null)
                return null;
            var now = Now;
            var id = db.Orm.Insertable(new ClanRow
            {
                Name = name, MasterUserNum = founderUserNum, Introduction = introduction, Notice = "", Level = 1, Points = 0,
                Mark = 0, Capacity = capacity, Recruitment = 1, CreatedAt = now, DisbandAt = null,
            }).ExecuteReturnBigIdentity();
            db.Orm.Insertable(new ClanMemberRow
            {
                UserNum = founderUserNum, ClanId = id, Nickname = founderNickname, Level = founderLevel, Role = ClanRole.Master,
                JoinedAt = now, SeenAt = now,
            }).ExecuteCommand();
            return Counted(db, Clan(db, id)!);
        }, ct));
    }

    /// <inheritdoc />
    public ValueTask<ClanRecord?> FindAsync(long clanId, CancellationToken ct) =>
        new(_database.Read(db => Clan(db, clanId) is { } row ? Counted(db, row) : null, ct));

    /// <inheritdoc />
    public ValueTask<ClanRecord?> FindByNameAsync(string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new(_database.Read(db => ByName(db, name) is { } row ? Counted(db, row) : null, ct));
    }

    /// <inheritdoc />
    public ValueTask<ClanSearchPage> SearchAsync(string text, ClanSearchOrder order, bool recruitingOnly, int skip, int take, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!Enum.IsDefined(order))
            throw new ArgumentOutOfRangeException(nameof(order), order, "Unknown clan search order.");
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        var needle = text.ToLowerInvariant();
        return new(_database.Read(db =>
        {
            // instr() over lower() needs no LIKE escaping: '%' and '_' in a clan name are ordinary characters.
            var query = db.Orm.Queryable<ClanRow>()
                .WhereIF(needle.Length > 0, row => SqlFunc.CharIndexNew(SqlFunc.ToLower(row.Name), needle) > 0)
                .WhereIF(recruitingOnly, row => row.Recruitment != 0);
            var total = query.Clone().Count();
            var ordered = order == ClanSearchOrder.Points
                ? query.OrderBy(row => row.Points, OrderByType.Desc).OrderBy(row => row.ClanId)
                : query.OrderBy(row => row.ClanId);
            var rows = ordered.Skip(skip).Take(take).ToList();
            return new ClanSearchPage(total, rows.Select(row => Counted(db, row)).ToArray());
        }, ct));
    }

    /// <inheritdoc />
    public ValueTask<bool> UpdateAsync(ClanRecord clan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clan);
        long? disband = clan.DisbandAt is { } at ? ToUnix(at) : null;
        return new(_database.Write((db, _) => db.Orm.Updateable<ClanRow>().SetColumns(row => new ClanRow
        {
            MasterUserNum = clan.MasterUserNum, Introduction = clan.Introduction, Notice = clan.Notice, Level = clan.Level,
            Points = clan.Points, Mark = clan.Mark, Capacity = clan.Capacity, Recruitment = clan.Recruitment, DisbandAt = disband,
        }).Where(row => row.ClanId == clan.ClanId).ExecuteCommand() == 1, ct));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ClanMemberRecord>> DeleteDueAsync(DateTime now, CancellationToken ct) =>
        new(_database.Write<IReadOnlyList<ClanMemberRecord>>((db, _) =>
        {
            var cutoff = ToUnix(now);
            var due = db.Orm.Queryable<ClanRow>().Where(row => row.DisbandAt != null && row.DisbandAt <= cutoff).Select(row => row.ClanId).ToList();
            if (due.Count == 0) return [];
            var rows = db.Orm.Queryable<ClanMemberRow>().Where(row => due.Contains(row.ClanId)).OrderBy(row => row.ClanId).OrderBy(row => row.JoinedAt)
                .ToList().Select(ToRecord).ToArray();
            db.Orm.Deleteable<ClanPostRow>().Where(row => due.Contains(row.ClanId)).ExecuteCommand();
            db.Orm.Deleteable<ClanMemberRow>().Where(row => due.Contains(row.ClanId)).ExecuteCommand();
            db.Orm.Deleteable<ClanRow>().Where(row => due.Contains(row.ClanId)).ExecuteCommand();
            return rows;
        }, ct));

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(long clanId, CancellationToken ct) => new(_database.Write((db, _) =>
    {
        if (Clan(db, clanId) is null) return false;
        db.Orm.Deleteable<ClanPostRow>().Where(row => row.ClanId == clanId).ExecuteCommand();
        db.Orm.Deleteable<ClanMemberRow>().Where(row => row.ClanId == clanId).ExecuteCommand();
        return db.Orm.Deleteable<ClanRow>().Where(row => row.ClanId == clanId).ExecuteCommand() == 1;
    }, ct));

    /// <inheritdoc />
    public ValueTask<ClanMemberRecord?> FindMemberAsync(long userNum, CancellationToken ct) =>
        new(_database.Read(db => Member(db, userNum) is { } row ? ToRecord(row) : null, ct));

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ClanMemberRecord>> ListMembersAsync(long clanId, CancellationToken ct) =>
        new(_database.Read<IReadOnlyList<ClanMemberRecord>>(db => db.Orm.Queryable<ClanMemberRow>().Where(row => row.ClanId == clanId)
            .OrderBy(row => row.JoinedAt).OrderBy(row => row.UserNum).ToList().Select(ToRecord).ToArray(), ct));

    /// <inheritdoc />
    public ValueTask<bool> AddMemberAsync(long clanId, long userNum, string nickname, int level, int role, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(nickname);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        return new(_database.Write((db, _) =>
        {
            if (Clan(db, clanId) is null || Member(db, userNum) is not null) return false;
            var now = Now;
            db.Orm.Insertable(new ClanMemberRow
            {
                UserNum = userNum, ClanId = clanId, Nickname = nickname, Level = level, Role = role, JoinedAt = now, SeenAt = now,
            }).ExecuteCommand();
            return true;
        }, ct));
    }

    /// <inheritdoc />
    public ValueTask<bool> SeatAsync(long clanId, long userNum, int role, CancellationToken ct)
    {
        if (role is not (ClanRole.Member or ClanRole.Deputy))
            throw new ArgumentOutOfRangeException(nameof(role), role, "A seat is given as member or deputy.");
        return new(_database.Write((db, _) =>
        {
            // The count and the change share the save's one write transaction, so the clan cannot be overfilled.
            if (Clan(db, clanId) is not { } clan || Member(db, userNum) is not { } member || member.ClanId != clanId
                || member.Role != ClanRole.Applicant || Seated(db, clanId) >= clan.Capacity)
                return false;
            return SetRole(db, clanId, userNum, role);
        }, ct));
    }

    /// <inheritdoc />
    public ValueTask<bool> TransferMasterAsync(long clanId, long fromUserNum, long toUserNum, CancellationToken ct) =>
        new(_database.Write((db, _) =>
        {
            if (fromUserNum == toUserNum || Clan(db, clanId) is null
                || Member(db, fromUserNum) is not { } source || source.ClanId != clanId || source.Role != ClanRole.Master
                || Member(db, toUserNum) is not { } target || target.ClanId != clanId || !ClanRole.IsMember(target.Role))
                return false;
            SetRole(db, clanId, fromUserNum, ClanRole.Member);
            SetRole(db, clanId, toUserNum, ClanRole.Master);
            db.Orm.Updateable<ClanRow>().SetColumns(row => new ClanRow { MasterUserNum = toUserNum })
                .Where(row => row.ClanId == clanId).ExecuteCommand();
            return true;
        }, ct));

    /// <inheritdoc />
    public ValueTask<bool> SetRoleAsync(long clanId, long userNum, int role, CancellationToken ct) =>
        new(_database.Write((db, _) => SetRole(db, clanId, userNum, role), ct));

    /// <inheritdoc />
    public ValueTask<bool> RemoveMemberAsync(long clanId, long userNum, CancellationToken ct) =>
        new(_database.Write((db, _) => db.Orm.Deleteable<ClanMemberRow>()
            .Where(row => row.UserNum == userNum && row.ClanId == clanId).ExecuteCommand() == 1, ct));

    /// <inheritdoc />
    public ValueTask RefreshMemberAsync(long userNum, string nickname, int level, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(nickname);
        var now = Now;
        _database.Write((db, _) => db.Orm.Updateable<ClanMemberRow>()
            .SetColumns(row => new ClanMemberRow { Nickname = nickname, Level = level, SeenAt = now })
            .Where(row => row.UserNum == userNum).ExecuteCommand(), ct);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<ClanPostRecord> AddPostAsync(long clanId, long userNum, string nickname, int level, string body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(nickname);
        ArgumentNullException.ThrowIfNull(body);
        return new(_database.Write((db, _) =>
        {
            if (Clan(db, clanId) is null)
                throw new InvalidOperationException($"Clan {clanId} does not exist.");
            var row = new ClanPostRow { ClanId = clanId, UserNum = userNum, Nickname = nickname, Level = level, Body = body, CreatedAt = Now };
            row.PostId = db.Orm.Insertable(row).ExecuteReturnBigIdentity();
            return ToRecord(row);
        }, ct));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ClanPostRecord>> ListPostsAsync(long clanId, long beforePostId, int take, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(beforePostId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        return new(_database.Read<IReadOnlyList<ClanPostRecord>>(db => db.Orm.Queryable<ClanPostRow>()
            .Where(row => row.ClanId == clanId).WhereIF(beforePostId > 0, row => row.PostId < beforePostId)
            .OrderBy(row => row.PostId, OrderByType.Desc).Take(take).ToList().Select(ToRecord).ToArray(), ct));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ClanPostRecord>> ListPostsAfterAsync(long clanId, long afterPostId, int take, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterPostId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        return new(_database.Read<IReadOnlyList<ClanPostRecord>>(db => db.Orm.Queryable<ClanPostRow>()
            .Where(row => row.ClanId == clanId && row.PostId > afterPostId)
            .OrderBy(row => row.PostId, OrderByType.Asc).Take(take).ToList().Select(ToRecord).ToArray(), ct));
    }

    /// <inheritdoc />
    public ValueTask<ClanPostRecord?> FindPostAsync(long clanId, long postId, CancellationToken ct) =>
        new(_database.Read(db => db.Orm.Queryable<ClanPostRow>().Where(row => row.PostId == postId && row.ClanId == clanId)
            .ToList().FirstOrDefault() is { } row ? ToRecord(row) : null, ct));

    /// <inheritdoc />
    public ValueTask<bool> DeletePostAsync(long clanId, long postId, CancellationToken ct) =>
        new(_database.Write((db, _) => db.Orm.Deleteable<ClanPostRow>()
            .Where(row => row.PostId == postId && row.ClanId == clanId).ExecuteCommand() == 1, ct));

    private static ClanRow? Clan(SqliteDatabase db, long clanId) =>
        db.Orm.Queryable<ClanRow>().Where(row => row.ClanId == clanId).ToList().FirstOrDefault();

    // The name column is COLLATE NOCASE, so equality here matches the unique key's case folding.
    private static ClanRow? ByName(SqliteDatabase db, string name) =>
        db.Orm.Queryable<ClanRow>().Where(row => row.Name == name).ToList().FirstOrDefault();

    private static ClanMemberRow? Member(SqliteDatabase db, long userNum) =>
        db.Orm.Queryable<ClanMemberRow>().Where(row => row.UserNum == userNum).ToList().FirstOrDefault();

    private static int Seated(SqliteDatabase db, long clanId) => db.Orm.Queryable<ClanMemberRow>()
        .Where(row => row.ClanId == clanId && (row.Role == ClanRole.Member || row.Role == ClanRole.Master || row.Role == ClanRole.Deputy))
        .Count();

    private static bool SetRole(SqliteDatabase db, long clanId, long userNum, int role) =>
        db.Orm.Updateable<ClanMemberRow>().SetColumns(row => new ClanMemberRow { Role = role })
            .Where(row => row.UserNum == userNum && row.ClanId == clanId).ExecuteCommand() == 1;

    private static ClanRecord Counted(SqliteDatabase db, ClanRow row) => new(row.ClanId, row.Name, row.MasterUserNum, row.Introduction,
        row.Notice, row.Level, row.Points, row.Mark, row.Capacity, row.Recruitment, FromUnix(row.CreatedAt),
        row.DisbandAt is { } disband ? FromUnix(disband) : null, Seated(db, row.ClanId));

    private static ClanMemberRecord ToRecord(ClanMemberRow row) =>
        new(row.ClanId, row.UserNum, row.Nickname, row.Level, row.Role, FromUnix(row.JoinedAt), FromUnix(row.SeenAt));

    private static ClanPostRecord ToRecord(ClanPostRow row) =>
        new(row.PostId, row.ClanId, row.UserNum, row.Nickname, row.Level, row.Body, FromUnix(row.CreatedAt));

    private static long ToUnix(DateTime value) =>
        new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromUnix(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;
}
