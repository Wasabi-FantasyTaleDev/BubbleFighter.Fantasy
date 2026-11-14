using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Checks atomic pet growth, consumable updates and competing inventory changes for memory persistence.</summary>
public sealed class PetMutationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-812", [301001, 100643, 200001]);
    private static readonly PetProgression Initial = new(0, 95, Now.AddHours(1), null, null, null);
    private static readonly PetProgression Grown = new(1, 0, Now.AddHours(8), Now, new(1, 4, 0, 0, 0), null);
    private static readonly InventorySlotRecord Pet = new(1, InventoryBag.CharacterBag, 10, 301001, 1, 0x81)
        { Lifetime = InventoryLifetime.Permanent, Pet = State(Initial) };
    private static readonly InventorySlotRecord Food = new(1, InventoryBag.CharacterBag, 11, 100643, 2, 0x40);
    private static readonly InventorySlotRecord Foreign = new(1, InventoryBag.CharacterBag, 12, 999001, 3, 0x21);

    /// <summary>Feeding updates both bag copies atomically and preserves rows outside the active build catalog.</summary>
    [Fact]
    public async Task FeedingCommitsFoodAndGrowthInBothBagsAndPreservesHiddenRows()
    {
        var store = await SeedAsync(new MemoryPlayerTable(1), [Pet, Food, Foreign]);
        var result = await store.MutatePetAsync(Command(Pet), default);
        Assert.NotNull(result);
        Assert.Equal(State(Grown), result.Single(row => row.ItemId == Pet.ItemId).Pet);
        Assert.Equal(1, result.Single(row => row.ItemId == Food.ItemId).Quantity);
        Assert.DoesNotContain(result, row => row.ItemId == Foreign.ItemId);
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var saved = await store.ListSlotsAsync(1, bag, 512, default);
            Assert.Equal(Pet with { Bag = bag, Pet = State(Grown) }, saved[0]);
            Assert.Equal(Food with { Bag = bag, Quantity = 1 }, saved[1]);
            Assert.Equal(Foreign with { Bag = bag }, saved[2]);
        }
    }

    /// <summary>Missing, depleted, expired and unsupported lifetime food cannot consume inventory or advance a pet.</summary>
    [Fact]
    public async Task MissingEmptyExpiredAndLifeFoodNeverChangePetOrInventory()
    {
        foreach (var rows in new InventorySlotRecord[][]
        {
            [Pet], [Pet, Food with { Quantity = 0 }],
            [Pet, Food with { Lifetime = InventoryLifetime.Until(Now) }],
            [Pet, Food with { Lifetime = InventoryLifetime.Life }],
        })
        {
            var store = await SeedAsync(new MemoryPlayerTable(1), rows);
            Assert.Null(await store.MutatePetAsync(Command(Pet), default));
            Assert.Equal(rows, await store.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default));
        }
    }

    /// <summary>Two plans based on the same pet snapshot have only one successful food consumption.</summary>
    [Fact]
    public async Task ConcurrentPlansConsumeOneFoodAndCommitOneGrowth()
    {
        var store = await SeedAsync(new MemoryPlayerTable(1), [Pet, Food with { Quantity = 1 }]);
        var results = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => Task.Run(async () => await store.MutatePetAsync(Command(Pet), default))));
        Assert.Single(results, result => result is not null);
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            Assert.Equal(State(Grown), Assert.Single(await store.ListSlotsAsync(1, bag, 512, default)).Pet);
    }

    /// <summary>Concurrent item purchase and pet feeding retain both committed changes.</summary>
    [Fact]
    public async Task ConcurrentPurchaseAndFeedPreserveEachOthersChanges()
    {
        var table = new MemoryPlayerTable(1);
        var store = await SeedAsync(table, [Pet, Food]);
        var shop = new MemoryShopPurchaseStore(table);
        await Task.WhenAll(Task.Run(async () => Assert.NotNull(await store.MutatePetAsync(Command(Pet), default))),
            Task.Run(async () => Assert.NotNull(await shop.PurchaseAsync(Purchase([new(200001, 7)]), default))));
        var saved = await store.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default);
        Assert.Equal(State(Grown), saved.Single(row => row.ItemId == Pet.ItemId).Pet);
        Assert.Equal(1, saved.Single(row => row.ItemId == Food.ItemId).Quantity);
        Assert.Equal(7, saved.Single(row => row.ItemId == 200001).Quantity);
    }

    /// <summary>Renewal rejects stale pet metadata and preserves current growth after replanning.</summary>
    [Fact]
    public async Task StaleRenewalCannotOverwriteGrowthAndReplannedRenewalKeepsIt()
    {
        var table = new MemoryPlayerTable(1);
        var store = await SeedAsync(table, [Pet, Food]);
        var shop = new MemoryShopPurchaseStore(table);
        await store.MutatePetAsync(Command(Pet), default);
        await Assert.ThrowsAsync<ShopPurchaseConflictException>(() => shop.PurchaseAsync(Renew(Pet), default).AsTask());
        Assert.Null(await new MemoryPlayerWalletRepository(table).FindByUserNumAsync(1, default));
        var updated = Pet with { Pet = State(Grown) };
        var purchase = await shop.PurchaseAsync(Renew(updated), default);
        Assert.Equal(State(Grown), Assert.Single(purchase!.Grants).Pet);
        Assert.Equal(State(Grown), (await store.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default))[0].Pet);
    }

    /// <summary>Feeding checks the active build selection without changing shared legacy worn flags.</summary>
    [Fact]
    public async Task BuildSelectionIsCheckedWithoutOverwritingSharedWornFlags()
    {
        var raw = Pet with { Flags = 0x80 };
        var store = await SeedAsync(new MemoryPlayerTable(1), [raw, Food]);
        Assert.Null(await store.MutatePetAsync(Command(raw), default));
        await store.UpdateLoadoutSelectionAsync(1, Scope, [new(301000, 301999, 301001)], 301001, true, default);
        Assert.NotNull(await store.MutatePetAsync(Command(Pet), default));
        var saved = (await store.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default))[0];
        Assert.Equal(0x80, saved.Flags);
        Assert.Equal(State(Grown), saved.Pet);
    }

    /// <summary>Cancellation leaves both inventory bag copies unchanged.</summary>
    [Fact]
    public async Task CancelledMutationLeavesBothBagsIntact()
    {
        var store = await SeedAsync(new MemoryPlayerTable(1), [Pet, Food]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.MutatePetAsync(Command(Pet), new CancellationToken(true)).AsTask());
        Assert.Equal([Pet, Food], await store.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default));
        Assert.Equal([Pet with { Bag = InventoryBag.EquippedBag }, Food with { Bag = InventoryBag.EquippedBag }],
            await store.ListSlotsAsync(1, InventoryBag.EquippedBag, 512, default));
    }

    /// <summary>Build-specific growth preserves other progressions while invalidating stale aggregate snapshots.</summary>
    [Fact]
    public async Task GrowthInOneBuildPreservesAnotherAndInvalidatesItsStaleSnapshot()
    {
        var store = await SeedAsync(new MemoryPlayerTable(1), [Pet, Food]);
        var modernScope = new InventoryLoadoutScope("kr-1256", Scope.SupportedItemIds);
        var modern = Initial with { Experience = 17, FullnessExpiresAt = Now.AddHours(2), LastFedAt = Now };
        var stale = Command(Pet) with { Scope = modernScope, Progression = modern };
        var first = await store.MutatePetAsync(Command(Pet), default);
        Assert.Null(await store.MutatePetAsync(stale, default));
        var expected = first!.Single(row => row.ItemId == Pet.ItemId);
        Assert.NotNull(await store.MutatePetAsync(stale with { ExpectedSlot = expected }, default));
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var saved = Assert.Single(await store.ListSlotsAsync(1, bag, 512, default));
            Assert.Equal(Grown, saved.Pet!.Find("kr-812"));
            Assert.Equal(modern, saved.Pet.Find("kr-1256"));
        }
        Assert.Equal(Initial, Pet.Pet!.Find("kr-812"));
        Assert.Null(Pet.Pet.Find("kr-1256"));
    }

    /// <summary>Reaching the configured progression-build limit refuses growth before food is consumed.</summary>
    [Fact]
    public async Task BuildCapacityRefusesGrowthBeforeConsumingFood()
    {
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(1), maxLoadoutsPerPlayer: 1);
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [Pet, Food], default);
        var command = Command(Pet) with { Scope = new("kr-1256", Scope.SupportedItemIds) };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.MutatePetAsync(command, default).AsTask());
        Assert.Equal([Pet, Food], await store.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default));
    }

    private static PetProgressions State(PetProgression progression) => PetProgressions.Empty.With("kr-812", progression);

    private static PetMutationCommand Command(InventorySlotRecord expected) => new(expected, Scope, Grown, 100643, true, Now);

    private static ShopPurchaseCommand Purchase(IReadOnlyList<ShopPurchaseGrant> grants) =>
        new(new(1, 100, 0), 42, 0, "lucci", 10, 1000, grants);

    private static ShopPurchaseCommand Renew(InventorySlotRecord expected) => Purchase([
        new(expected.ItemId, 2) { Mode = ShopGrantMode.MatchItem, ExpectedSlot = expected, Lifetime = InventoryLifetime.Permanent },
    ]);

    private static async Task<MemoryInventoryRepository> SeedAsync(MemoryPlayerTable table, InventorySlotRecord[] rows)
    {
        var store = new MemoryInventoryRepository(table);
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(1, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        return store;
    }
}
