using BubbleFighter.Fantasy.Contracts.Inventory;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>Checks durable joint metadata snapshots and cancellation rollback of both catalogues.</summary>
public sealed class InventoryMetadataTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-metadata-" + Guid.NewGuid().ToString("N"));
    private SqliteOptions Options => new() { DatabasePath = Path.Combine(_directory, "save.sqlite") };
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [300100, 301001]);

    /// <summary>Both catalogue identities and exact mirrors survive a reopen without allocating new identities.</summary>
    [Fact]
    public async Task JointMetadataSnapshotSurvivesReopen()
    {
        long user;
        InventorySnapshot expected;
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            user = await Seed(store);
            expected = await store.PrepareInventorySnapshotAsync(Command(user), default);
            Assert.NotNull(expected.CharacterBag.Single(row => row.ItemId == 300100).ArticleIdentity);
            Assert.NotNull(expected.CharacterBag.Single(row => row.ItemId == 301001).PetIdentity);
        }
        using (var database = new SqliteDatabase(Options))
        {
            var actual = await new SqliteRepositories(database).PrepareInventorySnapshotAsync(Command(user), default);
            Assert.Equal(expected.CharacterBag, actual.CharacterBag);
            Assert.Equal(expected.EquippedBag, actual.EquippedBag);
            Assert.Equal("2", database.RawScalar("SELECT entitlement_id FROM pet_identity_allocator"));
        }
    }

    /// <summary>Cancellation after the state update rolls back both metadata families and the single shared allocator.</summary>
    [Fact]
    public async Task CancellationAfterWriteRollsBackBothCatalogues()
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        var user = await Seed(store);
        using var cancellation = new CancellationTokenSource();
        Assert.IsType<SqliteConnection>(database.Orm.Ado.Connection).CreateFunction("cancel_metadata", () =>
        {
            cancellation.Cancel();
            return 0;
        });
        database.Write((db, tx) => db.RawExecute(
            "CREATE TRIGGER cancel_metadata AFTER UPDATE OF state ON players BEGIN SELECT cancel_metadata(); END;", tx), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PrepareInventorySnapshotAsync(Command(user), cancellation.Token).AsTask());
        database.Write((db, tx) => db.RawExecute("DROP TRIGGER cancel_metadata", tx), default);
        Assert.Equal("0", database.RawScalar("SELECT entitlement_id FROM pet_identity_allocator"));
        var saved = await store.ReadEnhancementInventoryAsync(user, Scope, default);
        Assert.All(saved.CharacterBag.Concat(saved.EquippedBag), row =>
        {
            Assert.Null(row.PetIdentity);
            Assert.Null(row.ArticleIdentity);
            Assert.Null(row.Pet);
        });
        Assert.Empty(database.RequireState(user).Loadouts);
    }

    private static InventoryMetadataPreparationCommand Command(long user) => new(user, Scope)
    {
        ArticleInitialization = new(user, Scope, [300100], Now),
        PetInitialization = new(user, Scope, [301001], new(0, 0, Now, null, null, null), Now),
    };

    private static async Task<long> Seed(SqliteRepositories store)
    {
        var user = (await store.CreateAsync("metadata", "metadata", default)).UserNum;
        InventorySlotRecord[] rows = [
            new(user, InventoryBag.CharacterBag, 1, 300100, 1, 0) { Lifetime = InventoryLifetime.Permanent },
            new(user, InventoryBag.CharacterBag, 2, 301001, 1, 0) { Lifetime = InventoryLifetime.Permanent },
        ];
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        return user;
    }

    /// <summary>Removes the temporary save after all owned connections have closed.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
