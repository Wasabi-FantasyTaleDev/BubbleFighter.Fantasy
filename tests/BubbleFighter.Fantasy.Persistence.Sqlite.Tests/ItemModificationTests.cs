using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>Checks durable coupled modification transactions and cancellation rollback in SQLite.</summary>
public sealed class ItemModificationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-modification-" + Guid.NewGuid().ToString("N"));
    private SqliteOptions Options => new() { DatabasePath = Path.Combine(_directory, "save.sqlite") };
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [300100, 300101, 190200, 190201]);

    /// <summary>Concurrent exact apply plans charge once and deletion retains its revision after another database reopen.</summary>
    [Fact]
    public async Task ApplyAndDeletePersistWalletInventoryAndReplayProtectionAcrossReopen()
    {
        long user;
        ItemModificationCommand apply;
        ItemModificationCommand deletion;
        ItemModificationResult saved;
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            (user, var initial) = await Seed(store);
            apply = Apply(initial);
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () => await store.TryModifyAsync(apply, default))));
            saved = Assert.Single(results, result => result is not null)!;
            Assert.Equal(new PlayerWallet(user, 900, 50), saved.Wallet);
            Assert.Equal(4, saved.Inventory.CharacterBag.Single(row => row.ItemId == 190200).Quantity);
            Assert.DoesNotContain(saved.Inventory.CharacterBag, row => row.ItemId == 190201);
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            var reopened = await store.ReadEnhancementInventoryAsync(user, Scope, default);
            Assert.Equal(saved.Inventory.CharacterBag, reopened.CharacterBag);
            Assert.Equal(saved.Inventory.EquippedBag, reopened.EquippedBag);
            Assert.Equal(saved.Wallet, await store.FindByUserNumAsync(user, default));
            Assert.Null(await store.TryModifyAsync(apply, default));
            deletion = new(Target(reopened), Scope, [], null, 0, 25, Now);
            saved = Assert.IsType<ItemModificationResult>(await store.TryModifyAsync(deletion, default));
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            var reopened = await store.ReadEnhancementInventoryAsync(user, Scope, default);
            Assert.Equal(saved.Inventory.CharacterBag, reopened.CharacterBag);
            Assert.Equal(saved.Inventory.EquippedBag, reopened.EquippedBag);
            Assert.Equal(new PlayerWallet(user, 875, 50), await store.FindByUserNumAsync(user, default));
            Assert.Equal(new ItemModificationState(2, []), Target(reopened).Modifications!.Find(Scope.Key));
            Assert.Null(await store.TryModifyAsync(apply, default));
            Assert.Null(await store.TryModifyAsync(deletion, default));
        }
    }

    /// <summary>Cancellation after writing the coupled snapshot rolls back wallet, materials, state and late identity allocation.</summary>
    [Fact]
    public async Task CancellationAfterSnapshotWriteRollsBackEveryModificationEffect()
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        var (user, snapshot) = await Seed(store);
        var late = new InventorySlotRecord(user, InventoryBag.CharacterBag, 9, 300101, 1, 0) { Lifetime = InventoryLifetime.Permanent };
        await store.AppendMissingItemsAsync(user, InventoryBag.CharacterBag, [late], default);
        var before = await store.ReadEnhancementInventoryAsync(user, Scope, default);
        var command = Apply(snapshot) with { ArticleInitialization = new(user, Scope, [300100, 300101], Now) };
        using var cancellation = new CancellationTokenSource();
        var connection = Assert.IsType<SqliteConnection>(database.Orm.Ado.Connection);
        connection.CreateFunction("cancel_modification_write", () => { cancellation.Cancel(); return 0; });
        database.Write((db, transaction) => db.RawExecute(
            "CREATE TRIGGER cancel_modification AFTER UPDATE OF state ON players BEGIN SELECT cancel_modification_write(); END;", transaction), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryModifyAsync(command, cancellation.Token).AsTask());
        database.Write((db, transaction) => db.RawExecute("DROP TRIGGER cancel_modification;", transaction), default);
        var saved = await store.ReadEnhancementInventoryAsync(user, Scope, default);
        Assert.Equal(before.CharacterBag, saved.CharacterBag);
        Assert.Equal(before.EquippedBag, saved.EquippedBag);
        Assert.Equal(new PlayerWallet(user, 1000, 50), await store.FindByUserNumAsync(user, default));
        Assert.Equal("1", database.RawScalar("SELECT entitlement_id FROM pet_identity_allocator WHERE singleton=1"));
    }

    /// <summary>Insufficient Lucci and stale exact material quantities preserve all saved state.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedTransactionPreservesItsWalletAndBothBags(bool currency)
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        var (user, snapshot) = await Seed(store);
        var command = Apply(snapshot);
        if (currency) await store.UpsertAsync(new PlayerWallet(user, 99, 50), default);
        else await store.ConsumeItemAsync(user, 190201, default);
        var before = await store.ReadEnhancementInventoryAsync(user, Scope, default);
        var wallet = await store.FindByUserNumAsync(user, default);
        Assert.Null(await store.TryModifyAsync(command, default));
        var saved = await store.ReadEnhancementInventoryAsync(user, Scope, default);
        Assert.Equal(before.CharacterBag, saved.CharacterBag);
        Assert.Equal(before.EquippedBag, saved.EquippedBag);
        Assert.Equal(wallet, await store.FindByUserNumAsync(user, default));
    }

    private static InventorySlotRecord Target(InventorySnapshot snapshot) => snapshot.CharacterBag.Single(row => row.ItemId == 300100);
    private static ItemModificationCommand Apply(InventorySnapshot snapshot) => new(Target(snapshot), Scope,
        [new(snapshot.CharacterBag.Single(row => row.ItemId == 190200), 2), new(snapshot.CharacterBag.Single(row => row.ItemId == 190201), 1)],
        new(101, 160, -40, 0, 0), null, 100, Now);

    private static async Task<(long User, InventorySnapshot Snapshot)> Seed(SqliteRepositories store)
    {
        var user = (await store.CreateAsync("modification-account", "modification-player", default)).UserNum;
        await store.GetOrCreateAsync(new PlayerWallet(user, 1000, 50), default);
        InventorySlotRecord[] rows = [
            new(user, InventoryBag.CharacterBag, 1, 300100, 1, 0x40) { Lifetime = InventoryLifetime.Permanent },
            new(user, InventoryBag.CharacterBag, 2, 190200, 6, 0),
            new(user, InventoryBag.CharacterBag, 3, 190201, 1, 0),
        ];
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        return (user, await store.InitializeArticlesAsync(new(user, Scope, [300100], Now), default));
    }

    /// <summary>Removes this test's temporary save after every owned connection is disposed.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
