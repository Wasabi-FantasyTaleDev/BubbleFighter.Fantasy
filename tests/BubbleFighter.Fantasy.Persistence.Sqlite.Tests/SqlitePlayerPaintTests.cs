using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>Checks atomic spray use and additive color persistence across SQLite restarts.</summary>
public sealed class SqlitePlayerPaintTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-paint-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "save.sqlite");
    private SqliteDatabase Open() => new(new() { DatabasePath = DatabasePath, MaxPlayers = 4, MaxAccounts = 4, MaxDatabaseBytes = 1048576 });

    /// <summary>A successful paint and its remaining stock survive reopening without changing duplicate rows.</summary>
    [Fact]
    public async Task RestartRestoresColorAndExactlyOneConsumedStack()
    {
        long user;
        using (var db = Open())
        {
            var stores = new SqliteRepositories(db);
            user = (await stores.CreateAsync("paint", "Painter", default)).UserNum;
            var first = new InventorySlotRecord(user, InventoryBag.CharacterBag, 91, 100180, 3, 0x80);
            var selected = first with { SlotKey = 92, Quantity = 2 };
            await stores.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [first, selected], default);
            await stores.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, [selected with { Bag = InventoryBag.EquippedBag }], default);
            Assert.True(await stores.ApplyPaintWithItemAsync(user, 100180, 14, default));
            Assert.False(await stores.ApplyPaintWithItemAsync(user, 100180, 14, default));
        }
        using (var db = Open())
        {
            var stores = new SqliteRepositories(db);
            var player = await stores.FindByUsernameAsync("paint", default);
            Assert.Equal((ushort)14, player!.ColorIndex);
            Assert.Equal("Painter", player.Nickname);
            Assert.Equal(new[] { 3, 1 }, (await stores.ListSlotsAsync(user, InventoryBag.CharacterBag, 32, default)).Select(row => row.Quantity));
            var mirror = Assert.Single(await stores.ListSlotsAsync(user, InventoryBag.EquippedBag, 32, default));
            Assert.Equal(1, mirror.Quantity);
            Assert.Equal(0x80, mirror.Flags);
        }
    }

    /// <summary>Rejected and cancelled requests preserve the state, while concurrent success consumes once.</summary>
    [Fact]
    public async Task RejectionsAndConcurrentUseAreAtomic()
    {
        using var db = Open();
        var stores = new SqliteRepositories(db);
        var player = await stores.CreateAsync("paint", "Painter", default);
        var expired = new InventorySlotRecord(player.UserNum, InventoryBag.CharacterBag, 91, 100180, 1, 0)
            { Lifetime = InventoryLifetime.Until(DateTimeOffset.UtcNow.AddDays(-1)) };
        await stores.ReplaceSlotsAsync(player.UserNum, InventoryBag.CharacterBag, [expired], default);
        Assert.False(await stores.ApplyPaintWithItemAsync(player.UserNum, 100180, 14, default));
        Assert.False(await stores.ApplyPaintWithItemAsync(player.UserNum, 100064, 1, default));
        Assert.Equal(expired, Assert.Single(await stores.ListSlotsAsync(player.UserNum, InventoryBag.CharacterBag, 32, default)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => stores.ApplyPaintWithItemAsync(player.UserNum, 100180, 0, default).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(() => stores.ApplyPaintWithItemAsync(player.UserNum, 100180, 14, new(true)).AsTask());
        Assert.Equal(player, await stores.FindByUsernameAsync("paint", default));
        await stores.ReplaceSlotsAsync(player.UserNum, InventoryBag.CharacterBag, [expired with { Lifetime = InventoryLifetime.Permanent }], default);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            await stores.ApplyPaintWithItemAsync(player.UserNum, 100180, 14, default))));
        Assert.Single(results, result => result);
        Assert.Empty(await stores.ListSlotsAsync(player.UserNum, InventoryBag.CharacterBag, 32, default));
    }

    /// <summary>Snapshots created before color support retain zero and can still be opened.</summary>
    [Fact]
    public async Task OldSnapshotsWithoutColorRetainTheNativeDefault()
    {
        using (var db = Open())
            await new SqliteRepositories(db).CreateAsync("old", "Old", default);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE players SET state=json_remove(state,'$.ColorIndex')";
            command.ExecuteNonQuery();
            command.CommandText = "SELECT json_type(state,'$.ColorIndex') FROM players";
            Assert.Equal(DBNull.Value, command.ExecuteScalar());
        }
        using var reopened = Open();
        Assert.Equal((ushort)0, (await new SqliteRepositories(reopened).FindByUsernameAsync("old", default))!.ColorIndex);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    /// <summary>A rejected database write cannot retain either the new color or the consumed stock.</summary>
    [Fact]
    public async Task FailedSnapshotWriteRollsBackColorAndConsumption()
    {
        long user;
        using (var db = Open())
        {
            var stores = new SqliteRepositories(db);
            user = (await stores.CreateAsync("paint", "Painter", default)).UserNum;
            await stores.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [new(user, InventoryBag.CharacterBag, 91, 100180, 1, 0)], default);
        }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_paint BEFORE UPDATE OF state ON players BEGIN SELECT RAISE(ABORT,'paint rejected'); END";
            command.ExecuteNonQuery();
        }
        using var reopened = Open();
        var repository = new SqliteRepositories(reopened);
        await Assert.ThrowsAsync<SqliteException>(() => repository.ApplyPaintWithItemAsync(user, 100180, 14, default).AsTask());
        Assert.Equal((ushort)0, (await repository.FindByUsernameAsync("paint", default))!.ColorIndex);
        Assert.Equal(1, Assert.Single(await repository.ListSlotsAsync(user, InventoryBag.CharacterBag, 32, default)).Quantity);
    }
}
