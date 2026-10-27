using BubbleFighter.Fantasy.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

public sealed class SqliteLauncherSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-sqlite-sessions-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "save.sqlite");
    private SqliteOptions Options() => new() { DatabasePath = DatabasePath, MaxPlayers = 4, MaxAccounts = 4, MaxDatabaseBytes = 1048576 };
    private static readonly DateTime Now = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task SessionSurvivesReopeningAndRotatesWithOneWinner()
    {
        using (var database = new SqliteDatabase(Options()))
            await new SqliteLauncherSessions(database).CreateAsync("s1", "player", "h1", Now.AddDays(30), Now, default);
        using var reopened = new SqliteDatabase(Options());
        var store = new SqliteLauncherSessions(reopened);
        var session = await store.FindAsync("h1", default);
        Assert.Equal(("s1", "player", Now.AddDays(30)), (session!.SessionId, session.Username, session.ExpiresAt));
        Assert.True(await store.TryRotateAsync("s1", "h1", "h2", Now.AddDays(31), Now.AddMinutes(1), default));
        Assert.False(await store.TryRotateAsync("s1", "h1", "h3", Now.AddDays(31), Now.AddMinutes(1), default));
        Assert.Null(await store.FindAsync("h1", default));
        await store.RevokeAsync("s1", Now.AddMinutes(2), default);
        Assert.NotNull((await store.FindAsync("h2", default))!.RevokedAt);
        Assert.False(await store.TryRotateAsync("s1", "h2", "h4", Now.AddDays(31), Now.AddMinutes(3), default));
    }

    [Fact]
    public async Task VersionOneSaveGainsTheSessionTableAndKeepsItsAccounts()
    {
        using (var database = new SqliteDatabase(Options()))
            Assert.True(await new SqliteRepositories(database).RegisterAsync("player", "hash", default));
        using (var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE messenger_requests; DROP TABLE messenger_categories; DROP TABLE messenger_relations; DROP TABLE messenger_users; DROP TABLE round_settlements; DROP TABLE present_claims; DROP TABLE presents; DROP TABLE launcher_sessions; DROP TABLE tutorial_stage_clears; PRAGMA user_version=1;";
            command.ExecuteNonQuery();
        }
        using var upgraded = new SqliteDatabase(Options());
        Assert.Equal("hash", (await new SqliteRepositories(upgraded).FindAsync("player", default))!.PasswordHash);
        var store = new SqliteLauncherSessions(upgraded);
        await store.CreateAsync("s1", "player", "h1", Now.AddDays(30), Now, default);
        Assert.Equal("player", (await store.FindAsync("h1", default))!.Username);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
