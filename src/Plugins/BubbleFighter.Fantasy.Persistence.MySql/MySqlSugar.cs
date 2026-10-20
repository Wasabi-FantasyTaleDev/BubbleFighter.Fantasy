using System.Data;
using System.Data.Common;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>
/// One operation's ORM handle: a connection opened through <see cref="IDbConnectionFactory"/>
/// wrapped by a <see cref="SqlSugarClient"/> that uses that exact connection (and therefore its
/// transaction) for every statement. Disposing closes both, so a repository method never leaks a
/// connection.
/// </summary>
internal sealed class SugarSession : IAsyncDisposable
{
    private readonly DbConnection _connection;

    private SugarSession(DbConnection connection)
    {
        _connection = connection;
        Db = Create(connection);
    }

    /// <summary>Gets the client bound to this session's connection.</summary>
    public SqlSugarClient Db { get; }

    /// <summary>Opens a connection through the factory and binds a client to it.</summary>
    /// <param name="connections">The plugin's connection factory.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The session; the caller disposes it.</returns>
    public static async ValueTask<SugarSession> OpenAsync(IDbConnectionFactory connections, CancellationToken ct)
    {
        var connection = await connections.OpenAsync(ct).ConfigureAwait(false);
        try
        {
            return new SugarSession(connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Builds a client that executes on an already-open connection. Also used by tests to render SQL
    /// offline, which needs no server.
    /// </summary>
    /// <param name="connection">The connection every statement runs on; it stays owned by the caller.</param>
    /// <returns>A client configured for MySQL with attribute-mapped entities.</returns>
    internal static SqlSugarClient Create(IDbConnection connection)
    {
        var db = new SqlSugarClient(new ConnectionConfig
        {
            DbType = SqlSugar.DbType.MySql,
            // The real connection is injected below; the string only has to be non-empty.
            ConnectionString = string.IsNullOrEmpty(connection.ConnectionString) ? "Server=unused" : connection.ConnectionString,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        db.Ado.Connection = connection;
        return db;
    }

    /// <summary>
    /// Starts a transaction at the server's default isolation level (REPEATABLE READ), the level every
    /// multi-row write in this plugin was designed against.
    /// </summary>
    /// <returns>The transaction; disposing it without a commit rolls it back.</returns>
    public ValueTask<SugarTransaction> BeginAsync() => BeginAsync(IsolationLevel.RepeatableRead);

    /// <summary>Starts a transaction at the given isolation level.</summary>
    /// <param name="isolation">The isolation level; the launcher credential writes use SERIALIZABLE.</param>
    /// <returns>The transaction; disposing it without a commit rolls it back.</returns>
    public async ValueTask<SugarTransaction> BeginAsync(IsolationLevel isolation)
    {
        await Db.Ado.BeginTranAsync(isolation).ConfigureAwait(false);
        return new SugarTransaction(Db);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Db.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>A started transaction that rolls back on disposal unless it was committed.</summary>
internal sealed class SugarTransaction(SqlSugarClient db) : IAsyncDisposable
{
    private bool _finished;

    /// <summary>Commits every statement run since <see cref="SugarSession.BeginAsync()"/>.</summary>
    public async ValueTask CommitAsync()
    {
        await db.Ado.CommitTranAsync().ConfigureAwait(false);
        _finished = true;
    }

    /// <summary>Rolls back now; later disposal does nothing.</summary>
    public async ValueTask RollbackAsync()
    {
        if (_finished)
            return;
        _finished = true;
        await db.Ado.RollbackTranAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => RollbackAsync();
}

/// <summary>Statements SqlSugar cannot express as entity operations, kept as small parameterized fragments.</summary>
internal static class SugarSql
{
    private const string IdentitySuffix = ";SELECT LAST_INSERT_ID();";

    /// <summary>
    /// Inserts <paramref name="row"/> and, when the key exists, updates it in the same atomic statement
    /// (<c>ON DUPLICATE KEY UPDATE</c>). SqlSugar's own storage helpers read before they write, which
    /// would let two writers race on the same new key; here the INSERT text and its parameters come
    /// from the entity mapping and only the conflict clause is appended.
    /// </summary>
    /// <typeparam name="T">The mapped entity.</typeparam>
    /// <param name="db">The client bound to the operation's connection.</param>
    /// <param name="row">The row to write.</param>
    /// <param name="ignoreOnInsert">Properties left to the column default on insert.</param>
    /// <param name="updateFromInsert">Properties refreshed from the inserted values when the key exists.</param>
    /// <param name="updateWithSql">Properties refreshed from a SQL expression when the key exists.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>MySQL's affected-row count: 1 inserted, 2 updated, 0 unchanged.</returns>
    public static Task<int> UpsertAsync<T>(SqlSugarClient db, T row, string[] ignoreOnInsert,
        string[] updateFromInsert, (string Property, string Sql)[] updateWithSql, CancellationToken ct)
        where T : class, new()
    {
        var (sql, parameters) = BuildUpsert(db, row, ignoreOnInsert, updateFromInsert, updateWithSql);
        return db.Ado.ExecuteCommandAsync(sql, parameters, ct);
    }

    /// <summary>
    /// Runs one parameterized statement directly on the connector, on the operation's connection and current
    /// transaction. SqlSugar wraps driver errors in <c>SqlSugarException</c>, so a caller that must see the
    /// <see cref="MySqlConnector.MySqlException"/> itself (duplicate-key code 1062) runs its statement here.
    /// </summary>
    /// <param name="db">The client bound to the operation's connection.</param>
    /// <param name="sql">The statement text; values travel only in <paramref name="parameters"/>.</param>
    /// <param name="parameters">Parameter names (with the <c>@</c> prefix) and their values; a null value is SQL NULL.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The affected-row count reported by the connector.</returns>
    public static async Task<int> ExecuteOnConnectionAsync(SqlSugarClient db, string sql,
        (string Name, object? Value)[] parameters, CancellationToken ct)
    {
        await using var command = (DbCommand)db.Ado.Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Ado.Transaction as DbTransaction;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Renders the statement <see cref="UpsertAsync{T}"/> runs, without executing it.</summary>
    /// <typeparam name="T">The mapped entity.</typeparam>
    /// <param name="db">The client used for mapping only.</param>
    /// <param name="row">The row to write.</param>
    /// <param name="ignoreOnInsert">Properties left to the column default on insert.</param>
    /// <param name="updateFromInsert">Properties refreshed from the inserted values when the key exists.</param>
    /// <param name="updateWithSql">Properties refreshed from a SQL expression when the key exists.</param>
    /// <returns>The SQL text and its parameters.</returns>
    internal static (string Sql, List<SugarParameter> Parameters) BuildUpsert<T>(SqlSugarClient db, T row,
        string[] ignoreOnInsert, string[] updateFromInsert, (string Property, string Sql)[] updateWithSql)
        where T : class, new()
    {
        var insert = db.Insertable(row);
        if (ignoreOnInsert.Length > 0)
            insert = insert.IgnoreColumns(ignoreOnInsert);
        var statement = insert.ToSql();
        var sql = statement.Key.TrimEnd();
        if (sql.EndsWith(IdentitySuffix, StringComparison.Ordinal))
            sql = sql[..^IdentitySuffix.Length].TrimEnd();
        var columns = db.EntityMaintenance.GetEntityInfo<T>().Columns;
        string Column(string property) => "`" + columns.First(c => c.PropertyName == property).DbColumnName + "`";
        var assignments = updateFromInsert.Select(p => $"{Column(p)} = VALUES({Column(p)})")
            .Concat(updateWithSql.Select(u => $"{Column(u.Property)} = {u.Sql}"));
        sql += " ON DUPLICATE KEY UPDATE " + string.Join(", ", assignments);
        return (sql, statement.Value);
    }
}
