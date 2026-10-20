using System.Data.Common;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Settings;
using BubbleFighter.Fantasy.Persistence.MySql;
using MySqlConnector;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed class PlayerSettingsTests
{
    [Fact]
    public async Task InvalidOrCancelledRequestsNeverOpenADatabaseConnection()
    {
        var connections = new UnusedFactory();
        var store = new MySqlPlayerSettings(connections, new() { MaxMappings = 1, MaxNameCharacters = 4 });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadKeysAsync(0, "a", default).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadKeysAsync(1, "Bad Scope", default).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceKeysAsync(1, "a", [new("LongName", 1)], default).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReplaceKeysAsync(1, "a", [new("A", 1), new("B", 2)], default).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ReadKeysAsync(1, "a", new(true)).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ReplaceKeysAsync(1, "a", [], new(true)).AsTask());
        Assert.Equal(0, connections.Opens);
    }

    [Fact]
    public void StoredMapRoundTripsCaseSensitiveNamesSupplementaryUtf16AndAllKeyBits()
    {
        CustomizedKey[] rows = [new("Jump", int.MinValue), new("jump", int.MaxValue), new("\U00010400", -1)];
        var limits = new CustomizedKeyLimits();
        var json = JsonSerializer.Serialize(rows);
        Assert.Equal(rows, MySqlPlayerSettings.Decode(json, limits));
        Assert.True(json.Length <= MySqlPlayerSettings.MaximumJsonCharacters(limits));
        Assert.Empty(MySqlPlayerSettings.Decode("[]", limits));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"Name\":\"Jump\"}]")]
    [InlineData("[{\"Name\":\"Jump\",\"Key\":1,\"Other\":0}]")]
    [InlineData("[{\"Name\":\"A\",\"Name\":\"B\",\"Key\":1}]")]
    [InlineData("[{\"Name\":\"A\",\"Key\":1,\"Key\":2}]")]
    [InlineData("[{\"Name\":\"A\",\"Key\":2147483648}]")]
    [InlineData("[{\"Name\":\"A\",\"Key\":\"1\"}]")]
    [InlineData("[{\"Name\":\"A\",\"Key\":{}}]")]
    [InlineData("[{\"Name\":\"\",\"Key\":1}]")]
    [InlineData("[{\"Name\":\"A\\u0000\",\"Key\":1}]")]
    [InlineData("[{\"Name\":\"A\",\"Key\":1},{\"Name\":\"A\",\"Key\":2}]")]
    public void MalformedStoredMapsFailInsteadOfBecomingAnEmptyReset(string json) =>
        Assert.Throws<InvalidDataException>(() => MySqlPlayerSettings.Decode(json, new()));

    [Fact]
    public void StoredRowCountNameLengthAndJsonLengthAreBounded()
    {
        var limits = new CustomizedKeyLimits { MaxMappings = 1, MaxNameCharacters = 4 };
        Assert.Throws<InvalidDataException>(() => MySqlPlayerSettings.Decode(
            "[{\"Name\":\"A\",\"Key\":1},{\"Name\":\"B\",\"Key\":2}]", limits));
        Assert.Throws<InvalidDataException>(() => MySqlPlayerSettings.Decode("[{\"Name\":\"ABCDE\",\"Key\":1}]", limits));
        Assert.Throws<InvalidDataException>(() => MySqlPlayerSettings.Decode(
            new string(' ', MySqlPlayerSettings.MaximumJsonCharacters(limits) + 1), limits));
    }

    [Fact]
    public void SqlReadBoundsTheJsonBeforeMaterializationAndUsesBothIdentityColumns()
    {
        using var db = SugarSession.Create(new MySqlConnection("Server=unused;Database=bf_game_db"));
        var statement = MySqlPlayerSettings.BoundedQuery(db, 7, "controls-a", 200).ToSql();
        var sql = statement.Key;
        Assert.Contains("CASE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("length", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("`mappings_json`", sql, StringComparison.Ordinal);
        Assert.Contains("`user_num`", sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains("`control_scope`", sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains(statement.Value, p => Equals(p.Value, 7L));
        Assert.Contains(statement.Value, p => Equals(p.Value, "controls-a"));
        Assert.Contains(statement.Value, p => Equals(p.Value, 200));
    }

    [Fact]
    public void SqlReplacementTargetsTheCompositeKeyWithoutChangingIt()
    {
        using var db = SugarSession.Create(new MySqlConnection("Server=unused;Database=bf_game_db"));
        var statement = db.Updateable(new CustomizedKeysRow { UserNum = 7, Scope = "a", Json = "[]" }).ToSql();
        var sql = statement.Key;
        var where = sql.IndexOf("WHERE", StringComparison.Ordinal);
        Assert.Contains("`mappings_json`", sql[..where], StringComparison.Ordinal);
        Assert.DoesNotContain("`user_num`", sql[..where], StringComparison.Ordinal);
        Assert.DoesNotContain("`control_scope`", sql[..where], StringComparison.Ordinal);
        Assert.Contains("`user_num`", sql[where..], StringComparison.Ordinal);
        Assert.Contains("`control_scope`", sql[where..], StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationKeepsScopesCaseSensitiveAndRemovesMapsWithTheirPlayer()
    {
        var assembly = typeof(MySqlPlugin).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("0032_customized_keys.sql", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        var sql = reader.ReadToEnd();
        Assert.Contains("user_num BIGINT NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("control_scope VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (user_num, control_scope)", sql, StringComparison.Ordinal);
        Assert.Contains("REFERENCES players(user_num) ON DELETE CASCADE", sql, StringComparison.Ordinal);
    }

    private sealed class UnusedFactory : IDbConnectionFactory
    {
        public int Opens { get; private set; }
        public ValueTask<DbConnection> OpenAsync(CancellationToken ct)
        {
            Opens++;
            throw new InvalidOperationException("No connection may be opened by a guard test.");
        }
    }
}
