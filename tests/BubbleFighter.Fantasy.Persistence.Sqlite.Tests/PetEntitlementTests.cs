using System.Globalization;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>Checks ownership allocation, retirement and complete transactional pet snapshots for SQLite.</summary>
public sealed class PetEntitlementTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-pet-identities-" + Guid.NewGuid().ToString("N"));
    private SqliteOptions Options => new() { DatabasePath = Path.Combine(_directory, "save.sqlite") };
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [301001, 301002, 100643]);
    private static readonly PetProgression Initial = new(0, 0, Now.AddHours(1), null, null, null);
    private static readonly InventoryWearGroup[] Groups = [new(301001, 301002, 301001)];

    /// <summary>Successful selection or growth returns identities for pets granted after the original target snapshot.</summary>
    [Theory]
    [InlineData("ordinary")]
    [InlineData("article")]
    [InlineData("mutation")]
    public async Task CompleteMutationInitializesOtherPetsGrantedAfterTheTargetWasRead(string operation)
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        var user = await Seed(store, "late-" + operation, mirror: true);
        var expected = (await store.InitializePetsAsync(Command(user), default)).CharacterBag[0];
        InventorySlotRecord[] additions = [Pet(user) with { ItemId = 301002, SlotKey = 2, Flags = 0 },
            new(user, InventoryBag.CharacterBag, 3, 100643, 2, 0)];
        await store.AppendMissingItemsAsync(user, InventoryBag.CharacterBag, additions, default);
        await store.AppendMissingItemsAsync(user, InventoryBag.EquippedBag,
            additions.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        var result = operation switch
        {
            "ordinary" => await store.UpdateLoadoutSelectionSnapshotAsync(user, Scope, [], 100643, true, default, Command(user)),
            "article" => await store.SelectPetAsync(new(user, Scope, [], new(301001, expected.PetIdentity!), false, Now)
                { Initialization = Command(user) }, default),
            _ => await store.MutatePetSnapshotAsync(new(expected, Scope, Initial with { Experience = 1 }, 100643, true, Now)
                { Initialization = Command(user) }, default),
        };
        Assert.NotNull(result);
        Assert.Equal(3, result.CharacterBag.Count);
        Assert.Equal(3, result.EquippedBag.Count);
        Assert.Equal(expected.PetIdentity, result.CharacterBag.Single(row => row.ItemId == 301001).PetIdentity);
        foreach (var pet in result.CharacterBag.Where(row => row.ItemId is 301001 or 301002))
        {
            Assert.NotNull(pet.PetIdentity);
            Assert.NotNull(pet.Pet!.Find(Scope.Key));
            var mirror = result.EquippedBag.Single(row => row.ItemId == pet.ItemId);
            Assert.Equal(pet.PetIdentity, mirror.PetIdentity);
            Assert.Equal(pet.Pet, mirror.Pet);
            Assert.Equal(pet.Flags & 1, mirror.Flags & 1);
        }
        Assert.Equal("2", database.RawScalar("SELECT entitlement_id FROM pet_identity_allocator"));
        if (operation == "ordinary")
        {
            Assert.All(result.CharacterBag.Concat(result.EquippedBag).Where(row => row.ItemId == 100643),
                row => Assert.Equal(1, row.Flags & 1));
            await store.UpdateLoadoutSelectionSnapshotAsync(user, Scope, [], 100643, false, default, Command(user));
            Assert.Equal(1, result.CharacterBag.Single(row => row.ItemId == 100643).Flags & 1);
        }
        if (operation == "mutation") Assert.All(result.CharacterBag.Concat(result.EquippedBag).Where(row => row.ItemId == 100643),
            row => Assert.Equal(1, row.Quantity));
    }

    /// <summary>Rejected selection or growth leaves newly observed pets and the shared allocator unchanged.</summary>
    [Theory]
    [InlineData("ordinary")]
    [InlineData("article")]
    [InlineData("mutation")]
    public async Task RefusedMutationDiscardsOtherPetInitializationAndAllocatorChanges(string operation)
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        var user = await Seed(store, "refused-" + operation, mirror: true);
        var expected = (await store.InitializePetsAsync(Command(user), default)).CharacterBag[0];
        await store.AppendMissingItemsAsync(user, InventoryBag.CharacterBag,
            [Pet(user) with { ItemId = 301002, SlotKey = 2, Flags = 0 }], default);
        var before = await store.ReadPetInventoryAsync(user, Scope, default);
        var result = operation switch
        {
            "ordinary" => await store.UpdateLoadoutSelectionSnapshotAsync(user, Scope, [], 100643, true, default, Command(user)),
            "article" => await store.SelectPetAsync(new(user, Scope, [], new(301001, new(99, PetArticleKeys.First + 99)), true, Now)
                { Initialization = Command(user) }, default),
            _ => await store.MutatePetSnapshotAsync(new(expected with { Quantity = 2 }, Scope, Initial, 0, false, Now)
                { Initialization = Command(user) }, default),
        };
        Assert.Null(result);
        var after = await store.ReadPetInventoryAsync(user, Scope, default);
        Assert.Equal(before.CharacterBag, after.CharacterBag);
        Assert.Equal(before.EquippedBag, after.EquippedBag);
        Assert.Equal("1", database.RawScalar("SELECT entitlement_id FROM pet_identity_allocator"));
        Assert.Equal(PetArticleKeys.First.ToString(CultureInfo.InvariantCulture), database.RawScalar("SELECT article_key FROM pet_identity_allocator"));
    }

    /// <summary>Preparing initialized SQLite ownership does not read the allocator or write an unchanged save.</summary>
    [Fact]
    public async Task ExistingIdentityPreparationDoesNotReadAllocatorOrSaveUnchangedState()
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        var user = await Seed(store, "no-allocator", mirror: true);
        var initialized = await store.InitializePetsAsync(Command(user), default);
        database.Write((db, transaction) =>
        {
            db.RawExecute("ALTER TABLE pet_identity_allocator RENAME TO unavailable_pet_allocator", transaction);
            return db.RawExecute("CREATE TRIGGER reject_pet_save BEFORE UPDATE OF state ON players BEGIN SELECT RAISE(ABORT, 'Unexpected player save'); END;", transaction);
        }, default);
        var repeated = await store.InitializePetsAsync(Command(user), default);
        Assert.Equal(initialized.CharacterBag, repeated.CharacterBag);
        Assert.Equal(initialized.EquippedBag, repeated.EquippedBag);
        database.Write((db, transaction) => db.RawExecute("DROP TRIGGER reject_pet_save", transaction), default);
        var legacy = new InventoryLoadoutScope("kr-812", Scope.SupportedItemIds);
        var progressed = await store.InitializePetsAsync(Command(user) with { Scope = legacy }, default);
        Assert.Equal(initialized.CharacterBag[0].PetIdentity, progressed.CharacterBag[0].PetIdentity);
        Assert.Equal(Initial, progressed.CharacterBag[0].Pet!.Find(legacy.Key));
        Assert.Equal("1", database.RawScalar("SELECT entitlement_id FROM unavailable_pet_allocator"));
        Assert.Equal(PetArticleKeys.First.ToString(CultureInfo.InvariantCulture),
            database.RawScalar("SELECT article_key FROM unavailable_pet_allocator"));
    }

    /// <summary>SQLite restarts retain exact identities and selections while regrant retires the previous Article.</summary>
    [Fact]
    public async Task InitializationAndSelectionRemainExactAcrossRestartAndRegrant()
    {
        long user;
        PetEntitlementIdentity original;
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            user = await Seed(store, "first", mirror: true);
            var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
                await store.InitializePetsAsync(Command(user), default))));
            original = results[0].CharacterBag[0].PetIdentity!;
            Assert.Equal(PetArticleKeys.First, original.ArticleKey);
            Assert.Equal(100000000UL, original.ArticleKey);
            Assert.All(results, result =>
            {
                Assert.Equal(original, result.CharacterBag[0].PetIdentity);
                Assert.Equal(original, result.EquippedBag[0].PetIdentity);
            });
            var legacy = new InventoryLoadoutScope("kr-812", Scope.SupportedItemIds);
            var older = await store.InitializePetsAsync(Command(user) with
                { Scope = legacy, InitialProgression = Initial with { Experience = 17 } }, default);
            Assert.Equal(17, older.CharacterBag[0].Pet!.Find(legacy.Key)!.Experience);
            var expected = older.CharacterBag[0];
            var mutated = await store.MutatePetSnapshotAsync(new(expected, Scope, Initial with { Experience = 4 }, 0, true, Now), default);
            Assert.NotNull(mutated);
            Assert.Equal(mutated.CharacterBag[0].Pet, mutated.EquippedBag[0].Pet);
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            var restored = await store.ReadPetInventoryAsync(user, Scope, default);
            Assert.Equal(original, restored.CharacterBag[0].PetIdentity);
            Assert.Equal(4, restored.CharacterBag[0].Pet!.Find(Scope.Key)!.Experience);
            Assert.Equal(17, restored.CharacterBag[0].Pet!.Find("kr-812")!.Experience);
            await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [], default);
            await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [Pet(user)], default);
            var replacement = await store.InitializePetsAsync(Command(user), default);
            Assert.True(replacement.CharacterBag[0].PetIdentity!.ArticleKey > original.ArticleKey);
            Assert.Equal(0, replacement.CharacterBag[0].Flags & 1);
            Assert.Equal(replacement.CharacterBag[0].PetIdentity, replacement.EquippedBag[0].PetIdentity);
            Assert.Null(await store.SelectPetAsync(new(user, Scope, Groups, new(301001, original), true, Now), default));
            var target = new PetEquipmentTarget(301001, replacement.CharacterBag[0].PetIdentity!);
            Assert.NotNull(await store.SelectPetAsync(new(user, Scope, Groups, target, true, Now), default));
            var loadout = await store.GetLoadoutAsync(user, Scope, default);
            Assert.NotNull(loadout.WornPetEntitlementIds);
            Assert.Equal([target.Identity.EntitlementId], loadout.WornPetEntitlementIds);
        }
    }

    /// <summary>Cancellation after the SQLite inventory write rolls back identities and their allocator together.</summary>
    [Fact]
    public async Task CancellationAfterActualInventoryWriteRollsBackIdentitiesAndAllocator()
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        var user = await Seed(store, "cancel", mirror: true);
        using var cancellation = new CancellationTokenSource();
        var connection = Assert.IsType<SqliteConnection>(database.Orm.Ado.Connection);
        connection.CreateFunction("cancel_pet_identity", () => { cancellation.Cancel(); return 0; });
        database.Write((db, transaction) => db.RawExecute(
            "CREATE TRIGGER cancel_identity AFTER UPDATE OF state ON players BEGIN SELECT cancel_pet_identity(); END;", transaction), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.InitializePetsAsync(Command(user), cancellation.Token).AsTask());
        database.Write((db, transaction) => db.RawExecute("DROP TRIGGER cancel_identity", transaction), default);
        Assert.Equal("0", database.RawScalar("SELECT entitlement_id FROM pet_identity_allocator"));
        Assert.Equal((PetArticleKeys.First - 1).ToString(CultureInfo.InvariantCulture), database.RawScalar("SELECT article_key FROM pet_identity_allocator"));
        var unchanged = await store.ReadPetInventoryAsync(user, Scope, default);
        Assert.All(unchanged.CharacterBag.Concat(unchanged.EquippedBag), row => { Assert.Null(row.PetIdentity); Assert.Null(row.Pet); });
        Assert.Equal(PetArticleKeys.First, (await store.InitializePetsAsync(Command(user), default)).CharacterBag[0].PetIdentity!.ArticleKey);
    }

    /// <summary>The last native-addressable key survives reopening and the following allocation fails without reuse.</summary>
    [Fact]
    public async Task LastAddressableKeyCommitsAndFollowingAllocationFailsAfterReopen()
    {
        long user;
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            user = await Seed(store, "last", mirror: false);
            database.Write((db, transaction) => db.RawExecute("UPDATE pet_identity_allocator SET article_key=$key", transaction,
                ("$key", (PetArticleKeys.Last - 1).ToString(CultureInfo.InvariantCulture))), default);
            var final = await store.InitializePetsAsync(Command(user), default);
            Assert.Equal(PetArticleKeys.Last, final.CharacterBag[0].PetIdentity!.ArticleKey);
            Assert.Equal(9223372036854775807UL, final.CharacterBag[0].PetIdentity!.ArticleKey);
            Assert.Empty(final.EquippedBag);
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [], default);
            await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [Pet(user)], default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializePetsAsync(Command(user), default).AsTask());
            Assert.Null((await store.ReadPetInventoryAsync(user, Scope, default)).CharacterBag[0].PetIdentity);
            Assert.Equal(PetArticleKeys.Last.ToString(CultureInfo.InvariantCulture), database.RawScalar("SELECT article_key FROM pet_identity_allocator"));
        }
    }

    /// <summary>SQLite JSON preserves unsigned identity bits and refuses ownership cloning through external writes.</summary>
    [Fact]
    public async Task UnsignedStateSurvivesJsonAndExternalWritesCannotCopyItToAnotherOwner()
    {
        long user, other;
        var identity = new PetEntitlementIdentity(ulong.MaxValue, ulong.MaxValue);
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            user = await Seed(store, "wide", mirror: false);
            other = await Seed(store, "other", mirror: false);
            database.Write((db, _) =>
            {
                var state = db.RequireState(user);
                state.Slots[0] = state.Slots[0] with { PetIdentity = identity };
                db.Save(user, state);
                return true;
            }, default);
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            var row = (await store.ReadPetInventoryAsync(user, Scope, default)).CharacterBag[0];
            Assert.Equal(identity, row.PetIdentity);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReplaceSlotsAsync(other, InventoryBag.CharacterBag,
                [row with { UserNum = other }], default).AsTask());
            await Assert.ThrowsAsync<InvalidDataException>(() => store.AppendMissingItemsAsync(other, InventoryBag.CharacterBag,
                [row with { UserNum = other, ItemId = 301002, SlotKey = 2 }], default).AsTask());
            Assert.Null((await store.ReadPetInventoryAsync(other, Scope, default)).CharacterBag[0].PetIdentity);
        }
    }

    private static PetInitializationCommand Command(long user) => new(user, Scope, [301001, 301002], Initial, Now);
    private static InventorySlotRecord Pet(long user) => new(user, InventoryBag.CharacterBag, 1, 301001, 1, 0x81)
    { Lifetime = InventoryLifetime.Permanent };
    private static async Task<long> Seed(SqliteRepositories store, string name, bool mirror)
    {
        var user = (await store.CreateAsync("pet-" + name, "pet-" + name, default)).UserNum;
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [Pet(user)], default);
        if (mirror) await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, [Pet(user) with { Bag = InventoryBag.EquippedBag }], default);
        return user;
    }

    /// <summary>Removes the temporary SQLite save and its journal files after the test releases its repositories.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
