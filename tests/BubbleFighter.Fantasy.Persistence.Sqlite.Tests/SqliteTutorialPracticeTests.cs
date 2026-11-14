using BubbleFighter.Fantasy.Contracts.Progress;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

public sealed class SqliteTutorialPracticeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-practice-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "save.sqlite");
    private SqliteOptions Options() => new() { DatabasePath = DatabasePath, MaxPlayers = 4, MaxAccounts = 4, MaxDatabaseBytes = 1048576 };

    [Fact]
    public async Task SuccessfulClearSurvivesReopeningAndVersionNineUpgrade()
    {
        var key = new TutorialPracticeKey("cn-279", 7, "rel_Tutorial_01");
        using (var database = new SqliteDatabase(Options()))
            Assert.True(await new SqliteRepositories(database).RegisterAsync("player", "hash", default));
        using (var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE tutorial_practice_clears; DROP TABLE IF EXISTS pet_identity_allocator; PRAGMA user_version=9;";
            command.ExecuteNonQuery();
        }
        using (var upgraded = new SqliteDatabase(Options()))
        {
            Assert.Equal("hash", (await new SqliteRepositories(upgraded).FindAsync("player", default))!.PasswordHash);
            var store = new SqliteTutorialPractice(upgraded);
            Assert.False(await store.HasClearedAsync(key, default));
            Assert.True(await store.RecordClearAsync(key, default));
            Assert.False(await store.RecordClearAsync(key, default));
        }
        using var reopened = new SqliteDatabase(Options());
        var restored = new SqliteTutorialPractice(reopened);
        Assert.True(await restored.HasClearedAsync(new("CN-279", 7, "REL_TUTORIAL_01"), default));
        Assert.False(await restored.HasClearedAsync(new("cn-324", 7, "rel_Tutorial_01"), default));
        Assert.False(await restored.HasClearedAsync(new("cn-279", 8, "rel_Tutorial_01"), default));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
