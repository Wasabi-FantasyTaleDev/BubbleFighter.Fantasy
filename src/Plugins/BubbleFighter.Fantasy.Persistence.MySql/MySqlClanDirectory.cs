using System.Data;
using System.Data.Common;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Clans;
using MySqlConnector;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>
/// Clans, member rows and board posts over the tables of migrations 0015 and 0049. The unique keys on
/// <c>clans.name</c> and <c>clan_members.user_num</c> decide the two races several nodes can run (the same
/// name, the same player joining twice), so those statements stay raw: the duplicate-key code must reach here.
/// Times are written and read as UTC.
/// </summary>
public sealed class MySqlClanDirectory(IDbConnectionFactory connections) : IClanDirectory
{
    private const int DuplicateKey = 1062;
    private const int MissingParent = 1452;

    private const string ClanColumns = """
        c.clan_id, c.name, c.master_user_num, c.introduction, c.notice, c.level, c.points, c.mark, c.capacity,
        c.recruitment, c.created_at, c.disband_at,
        (SELECT COUNT(*) FROM clan_members m WHERE m.clan_id = c.clan_id AND m.role >= 100) AS member_count
        """;

    private const string MemberColumns = "clan_id, user_num, nickname, level, role, joined_at, seen_at";
    private const string PostColumns = "post_id, clan_id, user_num, nickname, level, body, created_at";

    /// <inheritdoc />
    public async ValueTask<ClanRecord?> CreateAsync(string name, string introduction, int capacity, long founderUserNum,
        string founderNickname, int founderLevel, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(introduction);
        ArgumentNullException.ThrowIfNull(founderNickname);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(founderUserNum);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginAsync(IsolationLevel.ReadCommitted).ConfigureAwait(false);
        var db = session.Db;
        long clanId;
        try
        {
            await SugarSql.ExecuteOnConnectionAsync(db, """
                INSERT INTO clans (created_at, name, master_user_num, introduction, capacity)
                VALUES (UTC_TIMESTAMP(), @name, @master, @introduction, @capacity)
                """, [("@name", name), ("@master", founderUserNum), ("@introduction", introduction), ("@capacity", capacity)], ct).ConfigureAwait(false);
            clanId = Convert.ToInt64(await ScalarAsync(db, "SELECT LAST_INSERT_ID()", [], ct).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            await SugarSql.ExecuteOnConnectionAsync(db, """
                INSERT INTO clan_members (clan_id, user_num, joined_at, nickname, level, role, seen_at)
                VALUES (@clan, @user, UTC_TIMESTAMP(), @nickname, @level, @role, UTC_TIMESTAMP())
                """, [("@clan", clanId), ("@user", founderUserNum), ("@nickname", founderNickname), ("@level", founderLevel),
                ("@role", ClanRole.Master)], ct).ConfigureAwait(false);
        }
        catch (MySqlException e) when (e.Number == DuplicateKey)
        {
            // Either the name or the founder's row exists; the transaction rolls back on disposal.
            return null;
        }
        await transaction.CommitAsync().ConfigureAwait(false);
        return await FindOnAsync(db, clanId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ClanRecord?> FindAsync(long clanId, CancellationToken ct)
    {
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        return await FindOnAsync(session.Db, clanId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ClanRecord?> FindByNameAsync(string name, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var rows = await QueryAsync(session.Db, $"SELECT {ClanColumns} FROM clans c WHERE c.name = @name LIMIT 1",
            [("@name", name)], ReadClan, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async ValueTask<ClanSearchPage> SearchAsync(string text, ClanSearchOrder order, bool recruitingOnly, int skip, int take, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        var orderBy = order switch
        {
            ClanSearchOrder.ClanId => "c.clan_id",
            ClanSearchOrder.Points => "c.points DESC, c.clan_id",
            _ => throw new ArgumentOutOfRangeException(nameof(order), order, "Unknown clan search order."),
        };
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        // The text is a literal fragment: LIKE's own wildcards and escape character are escaped.
        var pattern = "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";
        var where = "c.name IS NOT NULL AND c.name LIKE @pattern" + (recruitingOnly ? " AND c.recruitment <> 0" : "");
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var total = Convert.ToInt32(await ScalarAsync(session.Db,
            $"SELECT COUNT(*) FROM clans c WHERE {where}", [("@pattern", pattern)], ct).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        var clans = await QueryAsync(session.Db,
            $"SELECT {ClanColumns} FROM clans c WHERE {where} ORDER BY {orderBy} LIMIT @take OFFSET @skip",
            [("@pattern", pattern), ("@take", take), ("@skip", skip)], ReadClan, ct).ConfigureAwait(false);
        return new ClanSearchPage(total, clans);
    }

    /// <inheritdoc />
    public async ValueTask<bool> UpdateAsync(ClanRecord clan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clan);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await SugarSql.ExecuteOnConnectionAsync(session.Db, """
            UPDATE clans SET master_user_num = @master, introduction = @introduction, notice = @notice, level = @level,
                points = @points, mark = @mark, capacity = @capacity, recruitment = @recruitment, disband_at = @disband
            WHERE clan_id = @clan
            """, [("@clan", clan.ClanId), ("@master", clan.MasterUserNum), ("@introduction", clan.Introduction), ("@notice", clan.Notice),
            ("@level", clan.Level), ("@points", clan.Points), ("@mark", clan.Mark), ("@capacity", clan.Capacity),
            ("@recruitment", clan.Recruitment), ("@disband", clan.DisbandAt)], ct).ConfigureAwait(false);
        // An unchanged row reports no affected row, so the clan itself answers whether it still exists.
        return await FindOnAsync(session.Db, clan.ClanId, ct).ConfigureAwait(false) is not null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ClanMemberRecord>> DeleteDueAsync(DateTime now, CancellationToken ct)
    {
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginAsync(IsolationLevel.ReadCommitted).ConfigureAwait(false);
        var db = session.Db;
        // Locking the due clan rows makes a second node wait here and then find them gone.
        var due = await QueryAsync(db, "SELECT clan_id FROM clans WHERE disband_at IS NOT NULL AND disband_at <= @now FOR UPDATE",
            [("@now", now)], reader => reader.GetInt64(0), ct).ConfigureAwait(false);
        var rows = new List<ClanMemberRecord>();
        foreach (var clanId in due)
        {
            rows.AddRange(await QueryAsync(db, $"SELECT {MemberColumns} FROM clan_members WHERE clan_id = @clan ORDER BY joined_at, user_num",
                [("@clan", clanId)], ReadMember, ct).ConfigureAwait(false));
            // Member rows first: their foreign key does not cascade. Posts cascade with the clan row.
            await SugarSql.ExecuteOnConnectionAsync(db, "DELETE FROM clan_members WHERE clan_id = @clan", [("@clan", clanId)], ct).ConfigureAwait(false);
            await SugarSql.ExecuteOnConnectionAsync(db, "DELETE FROM clans WHERE clan_id = @clan", [("@clan", clanId)], ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync().ConfigureAwait(false);
        return rows;
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeleteAsync(long clanId, CancellationToken ct)
    {
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginAsync(IsolationLevel.ReadCommitted).ConfigureAwait(false);
        var db = session.Db;
        // Member rows first: their foreign key does not cascade. Posts cascade with the clan row.
        await SugarSql.ExecuteOnConnectionAsync(db, "DELETE FROM clan_members WHERE clan_id = @clan", [("@clan", clanId)], ct).ConfigureAwait(false);
        var deleted = await SugarSql.ExecuteOnConnectionAsync(db, "DELETE FROM clans WHERE clan_id = @clan", [("@clan", clanId)], ct).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        return deleted > 0;
    }

    /// <inheritdoc />
    public async ValueTask<ClanMemberRecord?> FindMemberAsync(long userNum, CancellationToken ct)
    {
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var rows = await QueryAsync(session.Db, $"SELECT {MemberColumns} FROM clan_members WHERE user_num = @user",
            [("@user", userNum)], ReadMember, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ClanMemberRecord>> ListMembersAsync(long clanId, CancellationToken ct)
    {
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        return await QueryAsync(session.Db, $"SELECT {MemberColumns} FROM clan_members WHERE clan_id = @clan ORDER BY joined_at, user_num",
            [("@clan", clanId)], ReadMember, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> AddMemberAsync(long clanId, long userNum, string nickname, int level, int role, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(nickname);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        try
        {
            await SugarSql.ExecuteOnConnectionAsync(session.Db, """
                INSERT INTO clan_members (clan_id, user_num, joined_at, nickname, level, role, seen_at)
                VALUES (@clan, @user, UTC_TIMESTAMP(), @nickname, @level, @role, UTC_TIMESTAMP())
                """, [("@clan", clanId), ("@user", userNum), ("@nickname", nickname), ("@level", level), ("@role", role)], ct).ConfigureAwait(false);
            return true;
        }
        catch (MySqlException e) when (e.Number is DuplicateKey or MissingParent)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> SeatAsync(long clanId, long userNum, int role, CancellationToken ct)
    {
        if (role is not (ClanRole.Member or ClanRole.Deputy))
            throw new ArgumentOutOfRangeException(nameof(role), role, "A seat is given as member or deputy.");
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginAsync(IsolationLevel.ReadCommitted).ConfigureAwait(false);
        var db = session.Db;
        // The clan row is the lock every seating of this clan takes, on whichever node it runs: the count below
        // cannot change between being read and the role being written.
        var capacity = await ScalarAsync(db, "SELECT capacity FROM clans WHERE clan_id = @clan FOR UPDATE", [("@clan", clanId)], ct).ConfigureAwait(false);
        if (capacity is null or DBNull)
            return false;
        var seated = Convert.ToInt64(await ScalarAsync(db, "SELECT COUNT(*) FROM clan_members WHERE clan_id = @clan AND role >= 100",
            [("@clan", clanId)], ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        if (seated >= Convert.ToInt64(capacity, System.Globalization.CultureInfo.InvariantCulture))
            return false;
        var changed = await SugarSql.ExecuteOnConnectionAsync(db,
            "UPDATE clan_members SET role = @role WHERE clan_id = @clan AND user_num = @user AND role = @applicant",
            [("@clan", clanId), ("@user", userNum), ("@role", role), ("@applicant", ClanRole.Applicant)], ct).ConfigureAwait(false);
        if (changed != 1)
            return false;
        await transaction.CommitAsync().ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TransferMasterAsync(long clanId, long fromUserNum, long toUserNum, CancellationToken ct)
    {
        if (fromUserNum == toUserNum)
            return false;
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginAsync(IsolationLevel.ReadCommitted).ConfigureAwait(false);
        var db = session.Db;
        // Same lock as SeatAsync, so a transfer and a seating of one clan never interleave. A step that changes
        // no row returns before the commit, and the transaction rolls back on disposal.
        var locked = await ScalarAsync(db, "SELECT clan_id FROM clans WHERE clan_id = @clan FOR UPDATE", [("@clan", clanId)], ct).ConfigureAwait(false);
        if (locked is null or DBNull)
            return false;
        if (await SugarSql.ExecuteOnConnectionAsync(db,
                "UPDATE clan_members SET role = @member WHERE clan_id = @clan AND user_num = @user AND role = @master",
                [("@clan", clanId), ("@user", fromUserNum), ("@member", ClanRole.Member), ("@master", ClanRole.Master)], ct).ConfigureAwait(false) != 1)
            return false;
        if (await SugarSql.ExecuteOnConnectionAsync(db,
                "UPDATE clan_members SET role = @master WHERE clan_id = @clan AND user_num = @user AND role >= 100",
                [("@clan", clanId), ("@user", toUserNum), ("@master", ClanRole.Master)], ct).ConfigureAwait(false) != 1)
            return false;
        await SugarSql.ExecuteOnConnectionAsync(db, "UPDATE clans SET master_user_num = @user WHERE clan_id = @clan",
            [("@clan", clanId), ("@user", toUserNum)], ct).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask<bool> SetRoleAsync(long clanId, long userNum, int role, CancellationToken ct)
    {
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await SugarSql.ExecuteOnConnectionAsync(session.Db, "UPDATE clan_members SET role = @role WHERE clan_id = @clan AND user_num = @user",
            [("@clan", clanId), ("@user", userNum), ("@role", role)], ct).ConfigureAwait(false);
        // An unchanged role reports no affected row, so the row itself answers whether the member exists.
        var rows = await QueryAsync(session.Db, $"SELECT {MemberColumns} FROM clan_members WHERE clan_id = @clan AND user_num = @user",
            [("@clan", clanId), ("@user", userNum)], ReadMember, ct).ConfigureAwait(false);
        return rows.Count == 1 && rows[0].Role == role;
    }

    /// <inheritdoc />
    public async ValueTask<bool> RemoveMemberAsync(long clanId, long userNum, CancellationToken ct)
    {
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        return await SugarSql.ExecuteOnConnectionAsync(session.Db, "DELETE FROM clan_members WHERE clan_id = @clan AND user_num = @user",
            [("@clan", clanId), ("@user", userNum)], ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async ValueTask RefreshMemberAsync(long userNum, string nickname, int level, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(nickname);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await SugarSql.ExecuteOnConnectionAsync(session.Db,
            "UPDATE clan_members SET nickname = @nickname, level = @level, seen_at = UTC_TIMESTAMP() WHERE user_num = @user",
            [("@user", userNum), ("@nickname", nickname), ("@level", level)], ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ClanPostRecord> AddPostAsync(long clanId, long userNum, string nickname, int level, string body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(nickname);
        ArgumentNullException.ThrowIfNull(body);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await SugarSql.ExecuteOnConnectionAsync(db, """
            INSERT INTO clan_posts (clan_id, user_num, nickname, level, body, created_at)
            VALUES (@clan, @user, @nickname, @level, @body, UTC_TIMESTAMP())
            """, [("@clan", clanId), ("@user", userNum), ("@nickname", nickname), ("@level", level), ("@body", body)], ct).ConfigureAwait(false);
        var rows = await QueryAsync(db, $"SELECT {PostColumns} FROM clan_posts WHERE post_id = LAST_INSERT_ID()", [], ReadPost, ct).ConfigureAwait(false);
        return rows.Single();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ClanPostRecord>> ListPostsAsync(long clanId, long beforePostId, int take, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(beforePostId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        return await QueryAsync(session.Db,
            $"SELECT {PostColumns} FROM clan_posts WHERE clan_id = @clan AND (@before = 0 OR post_id < @before) ORDER BY post_id DESC LIMIT @take",
            [("@clan", clanId), ("@before", beforePostId), ("@take", take)], ReadPost, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ClanPostRecord>> ListPostsAfterAsync(long clanId, long afterPostId, int take, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterPostId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        return await QueryAsync(session.Db,
            $"SELECT {PostColumns} FROM clan_posts WHERE clan_id = @clan AND post_id > @after ORDER BY post_id LIMIT @take",
            [("@clan", clanId), ("@after", afterPostId), ("@take", take)], ReadPost, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<ClanPostRecord?> FindPostAsync(long clanId, long postId, CancellationToken ct)
    {
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var rows = await QueryAsync(session.Db, $"SELECT {PostColumns} FROM clan_posts WHERE clan_id = @clan AND post_id = @post",
            [("@clan", clanId), ("@post", postId)], ReadPost, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async ValueTask<bool> DeletePostAsync(long clanId, long postId, CancellationToken ct)
    {
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        return await SugarSql.ExecuteOnConnectionAsync(session.Db, "DELETE FROM clan_posts WHERE clan_id = @clan AND post_id = @post",
            [("@clan", clanId), ("@post", postId)], ct).ConfigureAwait(false) > 0;
    }

    private static async ValueTask<ClanRecord?> FindOnAsync(SqlSugarClient db, long clanId, CancellationToken ct)
    {
        var rows = await QueryAsync(db, $"SELECT {ClanColumns} FROM clans c WHERE c.clan_id = @clan AND c.name IS NOT NULL",
            [("@clan", clanId)], ReadClan, ct).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    private static ClanRecord ReadClan(DbDataReader row) => new(
        row.GetInt64(0), row.GetString(1), row.GetInt64(2), row.GetString(3), row.GetString(4), row.GetInt32(5), row.GetInt32(6),
        row.GetInt32(7), row.GetInt32(8), row.GetInt32(9), Utc(row.GetDateTime(10)),
        row.IsDBNull(11) ? null : Utc(row.GetDateTime(11)), Convert.ToInt32(row.GetValue(12), System.Globalization.CultureInfo.InvariantCulture));

    private static ClanMemberRecord ReadMember(DbDataReader row) => new(
        row.GetInt64(0), row.GetInt64(1), row.GetString(2), row.GetInt32(3), row.GetInt32(4), Utc(row.GetDateTime(5)), Utc(row.GetDateTime(6)));

    private static ClanPostRecord ReadPost(DbDataReader row) => new(
        row.GetInt64(0), row.GetInt64(1), row.GetInt64(2), row.GetString(3), row.GetInt32(4), row.GetString(5), Utc(row.GetDateTime(6)));

    private static DateTime Utc(DateTime stored) => DateTime.SpecifyKind(stored, DateTimeKind.Utc);

    private static DbCommand Command(SqlSugarClient db, string sql, (string Name, object? Value)[] parameters)
    {
        var command = (DbCommand)db.Ado.Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Ado.Transaction as DbTransaction;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private static async ValueTask<object?> ScalarAsync(SqlSugarClient db, string sql, (string Name, object? Value)[] parameters, CancellationToken ct)
    {
        await using var command = Command(db, sql, parameters);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static async ValueTask<List<T>> QueryAsync<T>(SqlSugarClient db, string sql, (string Name, object? Value)[] parameters,
        Func<DbDataReader, T> read, CancellationToken ct)
    {
        await using var command = Command(db, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(read(reader));
        return rows;
    }
}
