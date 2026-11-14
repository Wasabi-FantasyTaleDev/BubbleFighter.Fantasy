using BubbleFighter.Fantasy.Contracts.Inventory;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>Checks durable equipment transactions, allocator compatibility and rollback in SQLite.</summary>
public sealed class ItemEnhancementTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-enhancement-" + Guid.NewGuid().ToString("N"));
    private SqliteOptions Options => new() { DatabasePath = Path.Combine(_directory, "save.sqlite") };
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [300100, 300101, 190106, 190107, 301001]);

    /// <summary>Late grants receive both kinds of metadata only when the surrounding exact ownership operation commits.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LateGrantPreparationAndAllocatorRollBackWithARefusedOperation(bool selection, bool refuse)
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        var (user, snapshot) = await Seed(store);
        InventorySlotRecord[] late = [
            new(user, InventoryBag.CharacterBag, 9, 300101, 1, 0) { Lifetime = InventoryLifetime.Permanent },
            new(user, InventoryBag.CharacterBag, 10, 301001, 1, 0) { Lifetime = InventoryLifetime.Permanent },
        ];
        await store.AppendMissingItemsAsync(user, InventoryBag.CharacterBag, late, default);
        await store.AppendMissingItemsAsync(user, InventoryBag.EquippedBag, late.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        var articles = new ArticleInitializationCommand(user, Scope, [300100, 300101], Now);
        var pets = new PetInitializationCommand(user, Scope, [301001], new(0, 0, Now.AddHours(1), null, null, null), Now);
        var target = snapshot.CharacterBag[0];
        if (refuse) target = target with { SlotKey = 999 };
        var result = selection
            ? await store.SelectArticleAsync(new(user, Scope, target, [], true, Now)
                { ArticleInitialization = articles, PetInitialization = pets }, default)
            : await store.TryEnhanceAsync(new([target], Scope, Command(snapshot, true).MaterialDebits, ItemEnhancementKind.Weapon, true, Now)
                { ArticleInitialization = articles, PetInitialization = pets }, default);
        var saved = await store.ReadEnhancementInventoryAsync(user, Scope, default);
        if (refuse)
        {
            Assert.Null(result);
            Assert.Equal("1", database.RawScalar("SELECT entitlement_id FROM pet_identity_allocator WHERE singleton=1"));
            Assert.Null(saved.CharacterBag.Single(row => row.ItemId == 300101).ArticleIdentity);
            Assert.Null(saved.CharacterBag.Single(row => row.ItemId == 301001).PetIdentity);
            Assert.Null(saved.CharacterBag.Single(row => row.ItemId == 301001).Pet);
            Assert.Equal(4, saved.CharacterBag.Single(row => row.ItemId == 190106).Quantity);
        }
        else
        {
            Assert.NotNull(result);
            Assert.Equal("3", database.RawScalar("SELECT entitlement_id FROM pet_identity_allocator WHERE singleton=1"));
            Assert.NotNull(result.CharacterBag.Single(row => row.ItemId == 300101).ArticleIdentity);
            Assert.NotNull(result.CharacterBag.Single(row => row.ItemId == 301001).PetIdentity);
            Assert.NotNull(result.CharacterBag.Single(row => row.ItemId == 301001).Pet!.Find(Scope.Key));
            Assert.Equal(result.CharacterBag, saved.CharacterBag);
            Assert.Equal(result.EquippedBag, saved.EquippedBag);
        }
    }

    /// <summary>Concurrent copies of one successful or failed plan commit once and retain exact state after reopening.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttemptAndAllDebitsSurviveReopenExactlyOnce(bool success)
    {
        long user;
        InventorySnapshot saved;
        ItemEnhancementCommand command;
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            (user, var initial) = await Seed(store);
            command = Command(initial, success);
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () => await store.TryEnhanceAsync(command, default))));
            saved = Assert.Single(results, result => result is not null)!;
            foreach (var bag in new[] { saved.CharacterBag, saved.EquippedBag })
            {
                Assert.Equal(2, bag.Single(row => row.ItemId == 190106).Quantity);
                Assert.DoesNotContain(bag, row => row.ItemId == 190107);
                Assert.Equal(new ItemEnhancementState(success ? ItemEnhancementKind.Weapon : ItemEnhancementKind.None, 1),
                    bag.Single(row => row.ItemId == 300100).Enhancements!.Find(Scope.Key));
            }
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            Assert.Equal(13L, database.RawScalar("PRAGMA user_version"));
            var reopened = await store.ReadEnhancementInventoryAsync(user, Scope, default);
            Assert.Equal(saved.CharacterBag, reopened.CharacterBag);
            Assert.Equal(saved.EquippedBag, reopened.EquippedBag);
            Assert.Null(await store.TryEnhanceAsync(command, default));
            var target = reopened.CharacterBag.Single(row => row.ItemId == 300100);
            var selected = Assert.IsType<InventorySnapshot>(await store.SelectArticleAsync(new(user, Scope, target, [], true, Now), default));
            Assert.Equal(target.ArticleIdentity, selected.CharacterBag.Single(row => row.ItemId == target.ItemId).ArticleIdentity);
            Assert.Equal(1, selected.CharacterBag.Single(row => row.ItemId == target.ItemId).Flags & 1);
        }
    }

    /// <summary>Cancellation after the coupled player update rolls back target revision and every debit.</summary>
    [Fact]
    public async Task CancellationAfterSnapshotWriteRollsBackAllEnhancementEffects()
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        var (user, snapshot) = await Seed(store);
        using var cancellation = new CancellationTokenSource();
        var connection = Assert.IsType<SqliteConnection>(database.Orm.Ado.Connection);
        connection.CreateFunction("cancel_enhancement_write", () => { cancellation.Cancel(); return 0; });
        database.Write((db, transaction) => db.RawExecute(
            "CREATE TRIGGER cancel_enhancement AFTER UPDATE OF state ON players BEGIN SELECT cancel_enhancement_write(); END;", transaction), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryEnhanceAsync(Command(snapshot, true), cancellation.Token).AsTask());
        database.Write((db, transaction) => db.RawExecute("DROP TRIGGER cancel_enhancement;", transaction), default);
        var saved = await store.ReadEnhancementInventoryAsync(user, Scope, default);
        Assert.Equal(snapshot.CharacterBag, saved.CharacterBag);
        Assert.Equal(snapshot.EquippedBag, saved.EquippedBag);
    }

    /// <summary>Ordinary and pet ownership share the allocator across reopen without changing legacy pet identifiers.</summary>
    [Fact]
    public async Task OrdinaryAndPetArticlesUseOneDurableAllocator()
    {
        long user;
        InventoryArticleIdentity equipment;
        PetEntitlementIdentity petIdentity;
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            (user, var initial) = await Seed(store);
            equipment = initial.CharacterBag[0].ArticleIdentity!;
            var pet = new InventorySlotRecord(user, InventoryBag.CharacterBag, 8, 301001, 1, 0) { Lifetime = InventoryLifetime.Permanent };
            await store.AppendMissingItemsAsync(user, InventoryBag.CharacterBag, [pet], default);
            var prepared = await store.InitializePetsAsync(new(user, Scope, [301001], new(0, 0, Now, null, null, null), Now), default);
            petIdentity = prepared.CharacterBag.Single(row => row.ItemId == 301001).PetIdentity!;
            Assert.NotEqual(equipment.ArticleKey, petIdentity.ArticleKey);
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            var result = await store.InitializeArticlesAsync(new(user, Scope, [300100, 301001], Now), default);
            Assert.Equal(equipment, result.CharacterBag.Single(row => row.ItemId == 300100).ArticleIdentity);
            Assert.Equal(petIdentity, result.CharacterBag.Single(row => row.ItemId == 301001).PetIdentity);
            Assert.Null(result.CharacterBag.Single(row => row.ItemId == 301001).ArticleIdentity);
        }
    }

    private static ItemEnhancementCommand Command(InventorySnapshot snapshot, bool success) => new(
        [snapshot.CharacterBag.Single(row => row.ItemId == 300100)], Scope,
        [new(snapshot.CharacterBag.Single(row => row.ItemId == 190106), 2), new(snapshot.CharacterBag.Single(row => row.ItemId == 190107), 2)],
        ItemEnhancementKind.Weapon, success, Now);

    private static async Task<(long User, InventorySnapshot Snapshot)> Seed(SqliteRepositories store)
    {
        var user = (await store.CreateAsync("equipment-account", "equipment-player", default)).UserNum;
        InventorySlotRecord[] rows = [
            new(user, InventoryBag.CharacterBag, 1, 300100, 1, 0x40) { Lifetime = InventoryLifetime.Permanent },
            new(user, InventoryBag.CharacterBag, 2, 190106, 4, 0),
            new(user, InventoryBag.CharacterBag, 3, 190107, 2, 0),
        ];
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        return (user, await store.InitializeArticlesAsync(new(user, Scope, [300100], Now), default));
    }

    /// <summary>Removes this test's temporary save after every owned connection has been disposed.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
