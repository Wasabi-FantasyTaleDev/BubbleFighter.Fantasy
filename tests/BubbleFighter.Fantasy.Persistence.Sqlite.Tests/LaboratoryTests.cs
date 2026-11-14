using BubbleFighter.Fantasy.Contracts.Inventory;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

public sealed class LaboratoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-laboratory-" + Guid.NewGuid().ToString("N"));
    private SqliteOptions Options => new() { DatabasePath = Path.Combine(_directory, "save.sqlite") };
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-812", [11, 101, 104]);

    [Fact]
    public async Task DecompositionAndNewRewardIdentitySurviveReopenAsOneCommit()
    {
        long user;
        InventoryLaboratoryResult committed;
        InventoryLaboratoryCommand command;
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            user = await Seed(store);
            command = Command(await store.ReadLaboratoryInventoryAsync(user, Scope, default));
            committed = Assert.IsType<InventoryLaboratoryResult>(await store.TryLaboratoryAsync(command, default));
            var reward = Assert.Single(committed.GrantedItems);
            Assert.Equal(104, reward.ItemId);
            Assert.NotNull(reward.ArticleIdentity);
            Assert.Equal(PetArticleKeys.First + 1, reward.ArticleIdentity.ArticleKey);
            Assert.Equal(21, reward.SlotKey);
            Assert.DoesNotContain(committed.Inventory.CharacterBag, row => row.ItemId == 101);
            Assert.DoesNotContain(committed.Inventory.EquippedBag, row => row.ItemId == 101);
            Assert.Equal("2", database.RawScalar("SELECT entitlement_id FROM pet_identity_allocator WHERE singleton=1"));
            Assert.Null(await store.TryLaboratoryAsync(command, default));
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            var reopened = await store.ReadLaboratoryInventoryAsync(user, Scope, default);
            Assert.Equal(committed.Inventory.CharacterBag, reopened.CharacterBag);
            Assert.Equal(committed.Inventory.EquippedBag, reopened.EquippedBag);
            Assert.Equal(21, database.RequireState(user).LastPurchaseSlotKey);
            Assert.Null(await store.TryLaboratoryAsync(command, default));
        }
    }

    [Fact]
    public async Task CancellationAfterWriteRollsBackInputsRewardAndIdentityAllocation()
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        var user = await Seed(store);
        var before = await store.ReadLaboratoryInventoryAsync(user, Scope, default);
        using var cancellation = new CancellationTokenSource();
        Assert.IsType<SqliteConnection>(database.Orm.Ado.Connection).CreateFunction("cancel_lab_write", () =>
        { cancellation.Cancel(); return 0; });
        database.Write((db, transaction) => db.RawExecute(
            "CREATE TRIGGER cancel_lab AFTER UPDATE OF state ON players BEGIN SELECT cancel_lab_write(); END;", transaction), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryLaboratoryAsync(Command(before), cancellation.Token).AsTask());
        database.Write((db, transaction) => db.RawExecute("DROP TRIGGER cancel_lab;", transaction), default);
        var after = await store.ReadLaboratoryInventoryAsync(user, Scope, default);
        Assert.Equal(before.CharacterBag, after.CharacterBag);
        Assert.Equal(before.EquippedBag, after.EquippedBag);
        Assert.Equal("1", database.RawScalar("SELECT entitlement_id FROM pet_identity_allocator WHERE singleton=1"));
        Assert.Equal(0, database.RequireState(user).LastPurchaseSlotKey);
        Assert.NotNull(await store.TryLaboratoryAsync(Command(before), default));
    }

    private static InventoryLaboratoryCommand Command(InventorySnapshot snapshot)
    {
        var card = snapshot.CharacterBag.Single(row => row.ItemId == 11);
        var material = snapshot.CharacterBag.Single(row => row.ItemId == 101);
        return new(card.UserNum, Scope, [new(card, 1, false), new(material, 0, true)],
            [new(new(104, InventoryGrantKind.Permanent, 1, 1), null)], Now)
        { ArticleInitialization = new(card.UserNum, Scope, [101, 104], Now) };
    }
    private static async Task<long> Seed(SqliteRepositories store)
    {
        var user = (await store.CreateAsync("laboratory-account", "laboratory-player", default)).UserNum;
        InventorySlotRecord[] rows = [
            new(user, InventoryBag.CharacterBag, 1, 11, 2, 0) { Lifetime = InventoryLifetime.Permanent },
            new(user, InventoryBag.CharacterBag, 20, 101, int.MaxValue, 0) { Lifetime = InventoryLifetime.Permanent },
        ];
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, [rows[1] with { Bag = InventoryBag.EquippedBag }], default);
        await store.InitializeArticlesAsync(new(user, Scope, [101], Now), default);
        return user;
    }
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
