using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Persistence.Sqlite;
using BubbleFighter.Fantasy.Tests.Shared;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

public sealed class SqliteLauncherSessionLimitTests : LauncherSessionLimitTests, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-session-limit-" + Guid.NewGuid().ToString("N"));
    private SqliteDatabase? _database;

    protected override ILauncherSessionStore CreateStore()
    {
        _database = new SqliteDatabase(new SqliteOptions
        {
            DatabasePath = Path.Combine(_directory, "save.sqlite"), MaxAccounts = 32, MaxPlayers = 4, MaxDatabaseBytes = 1048576,
        });
        return new SqliteLauncherSessions(_database);
    }

    public void Dispose()
    {
        _database?.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
