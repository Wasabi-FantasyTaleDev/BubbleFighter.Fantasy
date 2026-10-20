using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Settings;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>Persists bounded key maps with the common player-row lock for atomic scope admission and replacement.</summary>
public sealed class MySqlPlayerSettings : IPlayerSettingsRepository
{
    private readonly IDbConnectionFactory _connections;

    /// <summary>Creates the store with deployment-specific scope, row and string budgets.</summary>
    public MySqlPlayerSettings(IDbConnectionFactory connections, CustomizedKeyLimits limits)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        _connections = connections;
        Limits = limits;
    }

    /// <inheritdoc />
    public CustomizedKeyLimits Limits { get; }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CustomizedKey>> ReadKeysAsync(long userNum, string scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CustomizedKeyLimits.ValidateIdentity(userNum, scope);
        await using var session = await SugarSession.OpenAsync(_connections, ct).ConfigureAwait(false);
        var rows = await BoundedQuery(session.Db, userNum, scope, MaximumJsonCharacters(Limits))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Count == 0 ? [] : Decode(rows[0].Json, Limits);
    }

    /// <inheritdoc />
    public async ValueTask ReplaceKeysAsync(long userNum, string scope, IReadOnlyList<CustomizedKey> mappings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CustomizedKeyLimits.ValidateIdentity(userNum, scope);
        var copy = Limits.Copy(mappings);
        await using var session = await SugarSession.OpenAsync(_connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginAsync().ConfigureAwait(false);
        var db = session.Db;
        await MySqlPlayerLock.AcquireAsync(db, userNum, ct).ConfigureAwait(false);
        var selected = db.Queryable<CustomizedKeysRow>().Where(row => row.UserNum == userNum && row.Scope == scope);
        if (copy.Length == 0)
            await db.Deleteable<CustomizedKeysRow>().Where(row => row.UserNum == userNum && row.Scope == scope)
                .ExecuteCommandAsync(ct).ConfigureAwait(false);
        else
        {
            var exists = await selected.AnyAsync(ct).ConfigureAwait(false);
            if (!exists && await db.Queryable<CustomizedKeysRow>().Where(row => row.UserNum == userNum)
                    .CountAsync(ct).ConfigureAwait(false) >= Limits.MaxScopes)
                throw new InvalidOperationException("The player's customized-key scope capacity has been reached.");
            var stored = new CustomizedKeysRow { UserNum = userNum, Scope = scope, Json = JsonSerializer.Serialize(copy) };
            if (exists)
                await db.Updateable(stored).ExecuteCommandAsync(ct).ConfigureAwait(false);
            else
                await db.Insertable(stored).ExecuteCommandAsync(ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    // SQL bounds the selected string before the connector materializes it; an oversized stored row fails decoding.
    internal static ISugarQueryable<CustomizedKeysRow> BoundedQuery(SqlSugarClient db, long userNum, string scope, int maximum) =>
        db.Queryable<CustomizedKeysRow>().Where(row => row.UserNum == userNum && row.Scope == scope)
            .Select(row => new CustomizedKeysRow
            {
                UserNum = row.UserNum, Scope = row.Scope,
                Json = SqlFunc.IIF(SqlFunc.Length(row.Json) <= maximum, row.Json, ""),
            });

    internal static int MaximumJsonCharacters(CustomizedKeyLimits limits) =>
        checked(2 + limits.MaxMappings * (64 + 6 * limits.MaxNameCharacters));

    internal static CustomizedKey[] Decode(string json, CustomizedKeyLimits limits)
    {
        if (json.Length > MaximumJsonCharacters(limits))
            throw new InvalidDataException("Stored customized-key JSON exceeds its configured budget.");
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 3 });
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.GetArrayLength() > limits.MaxMappings)
                throw new InvalidDataException("Stored customized-key rows exceed their configured budget.");
            var rows = new CustomizedKey[document.RootElement.GetArrayLength()];
            var i = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != 2
                    || !element.TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String
                    || !element.TryGetProperty("Key", out var key) || !key.TryGetInt32(out var value))
                    throw new InvalidDataException("Stored customized-key row is malformed.");
                rows[i++] = new(name.GetString()!, value);
            }
            return limits.Copy(rows);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("Stored customized-key map is malformed.", ex);
        }
    }
}

[SugarTable("player_customized_keys")]
internal sealed class CustomizedKeysRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }
    [SugarColumn(ColumnName = "control_scope", IsPrimaryKey = true)]
    public string Scope { get; set; } = "";
    [SugarColumn(ColumnName = "mappings_json")]
    public string Json { get; set; } = "";
}
