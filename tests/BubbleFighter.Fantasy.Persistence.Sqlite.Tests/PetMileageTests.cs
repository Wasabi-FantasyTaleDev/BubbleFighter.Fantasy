using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>Checks atomic mileage conversion, ordinary rewards and rollback for SQLite.</summary>
public sealed class PetMileageTests : IDisposable
{
    private const int Pet = 301001, OtherPet = 301002, Food = 100643, Meter = 103385, Reward = 200777;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-pet-mileage-" + Guid.NewGuid().ToString("N"));
    private SqliteOptions Options => new() { DatabasePath = Path.Combine(_directory, "save.sqlite") };
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [Pet, OtherPet, Food, Meter, Reward]);
    private static readonly PetProgression Initial = new(0, 0, Now.AddHours(1), null, null, null);

    /// <summary>Existing and newly granted rewards gain Articles only when the pet, food and mileage commit succeeds.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RewardArticlesAndLatePetsShareTheMutationCommit(bool existingReward, bool stale)
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        var user = await Seed(database, store, meter: true);
        if (existingReward)
        {
            var reward = new InventorySlotRecord(user, InventoryBag.CharacterBag, 4, Reward, 4, 0) { Lifetime = InventoryLifetime.Permanent };
            await store.AppendMissingItemsAsync(user, InventoryBag.CharacterBag, [reward], default);
            await store.AppendMissingItemsAsync(user, InventoryBag.EquippedBag, [reward with { Bag = InventoryBag.EquippedBag }], default);
        }
        var before = await store.ReadPetInventoryAsync(user, Scope, default);
        var late = new InventorySlotRecord(user, InventoryBag.CharacterBag, 9, OtherPet, 1, 0) { Lifetime = InventoryLifetime.Permanent };
        await store.AppendMissingItemsAsync(user, InventoryBag.CharacterBag, [late], default);
        await store.AppendMissingItemsAsync(user, InventoryBag.EquippedBag, [late with { Bag = InventoryBag.EquippedBag }], default);
        var expectedReward = before.CharacterBag.SingleOrDefault(row => row.ItemId == Reward);
        var command = Command(user, before) with { ArticleInitialization = new(user, Scope, [Reward], Now) };
        command = command with { Mileage = command.Mileage! with { Conversion = new(2, 2, 5, false, 3,
            [new(Reward, (expectedReward?.Quantity ?? 0) + 2)
                { Mode = ShopGrantMode.MatchItem, ExpectedSlot = expectedReward, Lifetime = InventoryLifetime.Permanent }]) } };
        if (stale) command = command with { ExpectedSlot = command.ExpectedSlot with { SlotKey = 999 } };
        var result = await store.MutatePetWithResultAsync(command, default);
        var saved = await store.ReadPetInventoryAsync(user, Scope, default);
        Assert.Equal(stale, result is null);
        Assert.Equal(stale ? "1" : "3", database.RawScalar("SELECT entitlement_id FROM pet_identity_allocator WHERE singleton=1"));
        Assert.Equal(stale, saved.CharacterBag.Single(row => row.ItemId == OtherPet).PetIdentity is null);
        Assert.Equal(stale ? 2 : 1, saved.CharacterBag.Single(row => row.ItemId == Food).Quantity);
        if (!stale)
        {
            var reward = saved.CharacterBag.Single(row => row.ItemId == Reward);
            Assert.NotNull(reward.ArticleIdentity);
            Assert.All(saved.EquippedBag.Where(row => row.ItemId == Reward), row => Assert.Equal(reward.ArticleIdentity, row.ArticleIdentity));
            Assert.Null(await store.MutatePetWithResultAsync(command, default));
        }
    }

    /// <summary>SQLite reopening retains mileage and rewards without synthetic purchase records or flag changes.</summary>
    [Fact]
    public async Task CommittedMileageAndRewardsSurviveReopenWithoutPurchaseAuditOrFlagMutation()
    {
        long user;
        PetEntitlementIdentity identity;
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            user = await Seed(database, store, meter: true);
            var before = await store.ReadPetInventoryAsync(user, Scope, default);
            identity = before.CharacterBag.Single(row => row.ItemId == Pet).PetIdentity!;
            var result = Assert.IsType<PetMutationResult>(await store.MutatePetWithResultAsync(Command(user, before), default));
            Assert.Equal(new[] { Reward }, result.RewardItemIds);
            Assert.Equal(3, result.Snapshot.CharacterBag.Single(row => row.ItemId == Meter).Quantity);
            Assert.Equal(0x80, result.Snapshot.CharacterBag.Single(row => row.ItemId == Meter).Flags);
            Assert.Equal(6001, result.Snapshot.CharacterBag.Single(row => row.ItemId == Reward).SlotKey);
            Assert.DoesNotContain(result.Snapshot.EquippedBag, row => row.ItemId == Reward);
            Assert.Empty(await store.ListRecentAsync(user, 10, default));
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            var restored = await store.ReadPetInventoryAsync(user, Scope, default);
            Assert.Equal(identity, restored.CharacterBag.Single(row => row.ItemId == Pet).PetIdentity);
            Assert.Equal(1, restored.CharacterBag.Single(row => row.ItemId == Pet).Pet!.Find(Scope.Key)!.Experience);
            Assert.All(restored.CharacterBag.Concat(restored.EquippedBag).Where(row => row.ItemId == Meter),
                row => Assert.Equal(3, row.Quantity));
            Assert.All(restored.CharacterBag.Concat(restored.EquippedBag).Where(row => row.ItemId == Food),
                row => Assert.Equal(1, row.Quantity));
            Assert.Equal(2, restored.CharacterBag.Single(row => row.ItemId == Reward).Quantity);
            Assert.Equal(0x81, (await store.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default)).Single(row => row.ItemId == Meter).Flags);
            Assert.Equal(6001, database.Read(db => db.RequireState(user).LastPurchaseSlotKey, default));
            Assert.Empty(await store.ListRecentAsync(user, 10, default));
            var stale = Command(user, restored) with { Mileage = new(Meter,
                restored.CharacterBag.Single(row => row.ItemId == Meter) with { Quantity = 8 }, 1, 100, 1000, null) };
            Assert.Null(await store.MutatePetWithResultAsync(stale, default));
            Assert.Equal(restored.CharacterBag, (await store.ReadPetInventoryAsync(user, Scope, default)).CharacterBag);
        }
    }

    /// <summary>Cancellation after a SQLite write rolls back the whole mileage transaction and both key allocators.</summary>
    [Fact]
    public async Task CancellationAfterActualWriteRollsBackMileageRewardsFoodGrowthAndBothAllocators()
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        var user = await Seed(database, store, meter: false);
        var beforeTarget = await store.ReadPetInventoryAsync(user, Scope, default);
        await store.AppendMissingItemsAsync(user, InventoryBag.CharacterBag,
            [new(user, InventoryBag.CharacterBag, 4, OtherPet, 1, 0) { Lifetime = InventoryLifetime.Permanent }], default);
        var before = await store.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default);
        var beforeMirror = await store.ListSlotsAsync(user, InventoryBag.EquippedBag, 512, default);
        var command = Command(user, beforeTarget);
        command = command with { Mileage = command.Mileage! with { Gain = 13 },
            ArticleInitialization = new(user, Scope, [Reward], Now) };
        using var cancellation = new CancellationTokenSource();
        var connection = Assert.IsType<SqliteConnection>(database.Orm.Ado.Connection);
        connection.CreateFunction("cancel_pet_mileage", () => { cancellation.Cancel(); return 0; });
        database.Write((db, transaction) => db.RawExecute(
            "CREATE TRIGGER cancel_mileage AFTER UPDATE OF state ON players BEGIN SELECT cancel_pet_mileage(); END;", transaction), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.MutatePetWithResultAsync(command, cancellation.Token).AsTask());
        database.Write((db, transaction) => db.RawExecute("DROP TRIGGER cancel_mileage", transaction), default);
        Assert.Equal(before, await store.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default));
        Assert.Equal(beforeMirror, await store.ListSlotsAsync(user, InventoryBag.EquippedBag, 512, default));
        Assert.Equal(6000, database.Read(db => db.RequireState(user).LastPurchaseSlotKey, default));
        Assert.Equal("1", database.RawScalar("SELECT entitlement_id FROM pet_identity_allocator"));
        Assert.Empty(await store.ListRecentAsync(user, 10, default));
        var result = Assert.IsType<PetMutationResult>(await store.MutatePetWithResultAsync(command, default));
        Assert.Equal(new[] { Reward }, result.RewardItemIds);
        Assert.Equal(6001, result.Snapshot.CharacterBag.Single(row => row.ItemId == Meter).SlotKey);
        Assert.Equal(6002, result.Snapshot.CharacterBag.Single(row => row.ItemId == Reward).SlotKey);
        Assert.NotNull(result.Snapshot.CharacterBag.Single(row => row.ItemId == Reward).ArticleIdentity);
        Assert.NotNull(result.Snapshot.CharacterBag.Single(row => row.ItemId == OtherPet).PetIdentity);
    }

    private static PetMutationCommand Command(long user, PetInventorySnapshot snapshot) =>
        new(snapshot.CharacterBag.Single(row => row.ItemId == Pet), Scope, Initial with { Experience = 1 }, Food, true, Now)
        {
            Initialization = Initialization(user),
            Mileage = new(Meter, snapshot.CharacterBag.SingleOrDefault(row => row.ItemId == Meter), 5, 100, 1000,
                new(2, 2, 5, false, 3, [new(Reward, 2) { Mode = ShopGrantMode.MatchItem, Lifetime = InventoryLifetime.Permanent }])),
        };

    private static PetInitializationCommand Initialization(long user) => new(user, Scope, [Pet, OtherPet], Initial, Now);

    private static async Task<long> Seed(SqliteDatabase database, SqliteRepositories store, bool meter)
    {
        var user = (await store.CreateAsync("mileage", "mileage", default)).UserNum;
        List<InventorySlotRecord> rows = [new(user, InventoryBag.CharacterBag, 1, Pet, 1, 0x81) { Lifetime = InventoryLifetime.Permanent },
            new(user, InventoryBag.CharacterBag, 2, Food, 2, 0x80) { Lifetime = InventoryLifetime.Permanent }];
        if (meter) rows.Add(new(user, InventoryBag.CharacterBag, 3, Meter, 8, 0x81) { Lifetime = InventoryLifetime.Permanent });
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        await store.InitializePetsAsync(Initialization(user), default);
        if (meter) await store.UpdateLoadoutSelectionSnapshotAsync(user, Scope, [], Meter, false, default);
        database.Write((db, _) => { var state = db.RequireState(user); state.LastPurchaseSlotKey = 6000; db.Save(user, state); return true; }, default);
        return user;
    }

    /// <summary>Removes the temporary SQLite save and its journal files after the test releases its repositories.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
