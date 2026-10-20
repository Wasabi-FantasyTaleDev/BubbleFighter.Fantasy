using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Settings;
using BubbleFighter.Fantasy.Persistence.Sqlite;
using BubbleFighter.Fantasy.Persistence.Tests;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>Exercises real SQLite key-map durability, old saves and rollback alongside the shared contract.</summary>
public sealed class SqlitePlayerSettingsTests : PlayerSettingsContractTests, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-settings-" + Guid.NewGuid().ToString("N"));
    private SqliteDatabase? _database;
    private string SavePath => Path.Combine(_directory, "save.sqlite");
    private SqliteDatabase Open(CustomizedKeyLimits? limits = null) => new(new()
    {
        DatabasePath = SavePath, MaxPlayers = 4, MaxAccounts = 4, MaxDatabaseBytes = 1048576,
        CustomizedKeys = limits ?? new(),
    });

    protected override async Task<IPlayerSettingsRepository> CreateAsync(CustomizedKeyLimits limits)
    {
        _database = Open(limits);
        var players = new SqliteRepositories(_database);
        Assert.Equal(1, (await players.CreateAsync("first", "First", default)).UserNum);
        Assert.Equal(2, (await players.CreateAsync("second", "Second", default)).UserNum);
        return new SqlitePlayerSettings(_database);
    }

    [Fact]
    public async Task MapsAndResetSurviveReopenWithoutChangingThePlayer()
    {
        using (var db = Open())
        {
            await new SqliteRepositories(db).CreateAsync("first", "First", default);
            var store = new SqlitePlayerSettings(db);
            await store.ReplaceKeysAsync(1, "a", [new("Jump", -1)], default);
            await store.ReplaceKeysAsync(1, "b", [new("Fire", 32)], default);
            await store.ReplaceKeysAsync(1, "b", [], default);
        }
        using var reopened = Open();
        var settings = new SqlitePlayerSettings(reopened);
        Assert.Equal([new CustomizedKey("Jump", -1)], await settings.ReadKeysAsync(1, "a", default));
        Assert.Empty(await settings.ReadKeysAsync(1, "b", default));
        Assert.Equal("First", (await new SqliteRepositories(reopened).FindByUsernameAsync("first", default))!.Nickname);
    }

    [Fact]
    public async Task AbortedWriteRollsBackAndDoesNotLeaveAnOpenStatement()
    {
        using (var db = Open())
        {
            await new SqliteRepositories(db).CreateAsync("first", "First", default);
            var store = new SqlitePlayerSettings(db);
            await store.ReplaceKeysAsync(1, "a", [new("Jump", 1)], default);
            db.RawExecute("CREATE TRIGGER deny_settings BEFORE UPDATE ON players BEGIN SELECT RAISE(ABORT,'denied'); END");
            await Assert.ThrowsAsync<SqliteException>(() => store.ReplaceKeysAsync(1, "a", [new("Fire", 2)], default).AsTask());
            Assert.Equal([new CustomizedKey("Jump", 1)], await store.ReadKeysAsync(1, "a", default));
            db.RawExecute("DROP TRIGGER deny_settings");
            await store.ReplaceKeysAsync(1, "a", [new("Jump", 3)], default);
        }
        using var reopened = Open();
        Assert.Equal([new CustomizedKey("Jump", 3)], await new SqlitePlayerSettings(reopened).ReadKeysAsync(1, "a", default));
    }

    [Fact]
    public async Task OlderSnapshotsWithoutMapsOpenButMalformedMapsFailLoudly()
    {
        using (var db = Open())
        {
            await new SqliteRepositories(db).CreateAsync("first", "First", default);
            db.RawExecute("UPDATE players SET state=json_remove(state,'$.CustomizedKeys')");
        }
        using (var db = Open())
        {
            Assert.Empty(await new SqlitePlayerSettings(db).ReadKeysAsync(1, "a", default));
            db.RawExecute("UPDATE players SET state=json_set(state,'$.CustomizedKeys',json('null'))");
        }
        Assert.Throws<InvalidDataException>(() => Open());
    }

    [Theory]
    [InlineData("{\"Name\":\"Jump\"}")]
    [InlineData("{\"Name\":\"Jump\",\"Name\":\"Fire\",\"Key\":1}")]
    [InlineData("{\"Name\":\"Jump\",\"Key\":1,\"Key\":2}")]
    [InlineData("{\"Name\":\"Jump\",\"Key\":1,\"Other\":0}")]
    [InlineData("{\"Name\":\"Jump\",\"Key\":\"1\"}")]
    [InlineData("{\"Name\":\"Jump\",\"Key\":2147483648}")]
    public async Task MalformedSavedRowsCannotBecomeValidBindingsOrRetainTheFileLease(string row)
    {
        using (var db = Open())
        {
            await new SqliteRepositories(db).CreateAsync("first", "First", default);
            var map = "{\"a\":[" + row + "]}";
            db.RawExecute("UPDATE players SET state=json_set(state,'$.CustomizedKeys',json('"
                + map.Replace("'", "''", StringComparison.Ordinal) + "'))");
        }
        Assert.Throws<JsonException>(() => { using var unexpected = Open(); });

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = SavePath, Pooling = false,
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE players SET state=json_remove(state,'$.CustomizedKeys')";
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        using var repaired = Open();
        Assert.Empty(await new SqlitePlayerSettings(repaired).ReadKeysAsync(1, "a", default));
    }

    public void Dispose()
    {
        _database?.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
