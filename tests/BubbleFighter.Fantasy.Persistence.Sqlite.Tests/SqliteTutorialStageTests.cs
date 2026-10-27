using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

public sealed class SqliteTutorialStageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-sqlite-stages-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "save.sqlite");
    private SqliteOptions Options() => new() { DatabasePath = DatabasePath, MaxPlayers = 4, MaxAccounts = 4, MaxDatabaseBytes = 1048576 };

    [Fact]
    public async Task FirstClearIsReportedOnceAndTheBestRecordsSurviveReopening()
    {
        using (var database = new SqliteDatabase(Options()))
        {
            var store = new SqliteTutorialStages(database);
            Assert.True(await store.RecordClearAsync(7, new TutorialStageClear(16, 0, 95), default));
            Assert.False(await store.RecordClearAsync(7, new TutorialStageClear(16, 3, 120), default));
            Assert.False(await store.RecordClearAsync(7, new TutorialStageClear(16, 1, 80), default));
            Assert.True(await store.RecordClearAsync(7, new TutorialStageClear(4, 25, 0), default));
            Assert.True(await store.RecordClearAsync(8, new TutorialStageClear(16, 0, 200), default));
        }
        using var reopened = new SqliteDatabase(Options());
        var stages = await new SqliteTutorialStages(reopened).ListAsync(7, default);
        Assert.Equal([new TutorialStageClear(4, 25, 0), new TutorialStageClear(16, 3, 80)], stages);
        Assert.Empty(await new SqliteTutorialStages(reopened).ListAsync(9, default));
    }

    [Fact]
    public async Task VersionTwoSaveGainsTheStageTableAndKeepsItsAccounts()
    {
        using (var database = new SqliteDatabase(Options()))
            Assert.True(await new SqliteRepositories(database).RegisterAsync("player", "hash", default));
        using (var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE messenger_requests; DROP TABLE messenger_categories; DROP TABLE messenger_relations; DROP TABLE messenger_users; DROP TABLE round_settlements; DROP TABLE present_claims; DROP TABLE presents; DROP TABLE tutorial_stage_clears; PRAGMA user_version=2;";
            command.ExecuteNonQuery();
        }
        using var upgraded = new SqliteDatabase(Options());
        Assert.Equal("hash", (await new SqliteRepositories(upgraded).FindAsync("player", default))!.PasswordHash);
        Assert.True(await new SqliteTutorialStages(upgraded).RecordClearAsync(1, new TutorialStageClear(1, 0, 30), default));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
