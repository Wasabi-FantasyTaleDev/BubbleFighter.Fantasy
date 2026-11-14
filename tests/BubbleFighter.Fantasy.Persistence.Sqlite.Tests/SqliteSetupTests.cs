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

    [Fact]
    public async Task InspectCountsTablesAndAccountsAndClearDeletesOnlyAnUnleasedSave()
    {
        var path = Path.Combine(Path.GetTempPath(), "bff-sqlite-setup-" + Guid.NewGuid().ToString("N"), "game.sqlite");
        var setup = new SqliteSetupPlugin.Database(NullLogger.Instance, new());
        var settings = new InstallationSettings { DatabaseKind = "sqlite", SqlitePath = path };
        try
        {
            Assert.Equal(new InstallationDatabaseContents(false, 0, null), await setup.InspectAsync(settings, default));
            await setup.PrepareAsync(settings, true, default);
            var found = await setup.InspectAsync(settings, default);
            Assert.True(found!.HasData);
            Assert.Equal(0, found.Accounts);
            using (new SqliteDatabase(new() { DatabasePath = path }))
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await setup.ClearAsync(settings, default));
            Assert.True(File.Exists(path));
            await setup.ClearAsync(settings, default);
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }
}
