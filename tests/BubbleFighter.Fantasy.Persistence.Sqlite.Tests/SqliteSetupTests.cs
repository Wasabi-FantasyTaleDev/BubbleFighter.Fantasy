using BubbleFighter.Fantasy.Contracts.Installation;
using BubbleFighter.Fantasy.Persistence.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

public sealed class SqliteSetupTests
{
    [Fact]
    public async Task ReconfiguringALiveInstallationProbesTheDatabaseTheRunningProviderOwns()
    {
        var path = Path.Combine(Path.GetTempPath(), "bff-sqlite-setup-" + Guid.NewGuid().ToString("N"), "game.sqlite");
        var setup = new SqliteSetupPlugin.Database(NullLogger.Instance, new());
        var settings = new InstallationSettings { DatabaseKind = "sqlite", SqlitePath = path };
        try
        {
            // First installation: nothing holds the lease, so preparation creates the schema itself.
            await setup.PrepareAsync(settings, true, default);
            using (var running = new SqliteDatabase(new() { DatabasePath = path }))
            {
                var config = await setup.PrepareAsync(settings, true, default);
                Assert.Equal(path, config["database_path"]);
            }
            await setup.PrepareAsync(settings, true, default);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }
}
