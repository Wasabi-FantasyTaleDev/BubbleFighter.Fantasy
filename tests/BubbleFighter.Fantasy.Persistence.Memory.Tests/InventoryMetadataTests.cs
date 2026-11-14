using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Checks one-snapshot preparation of both native metadata catalogues and shared allocator rollback.</summary>
public sealed class InventoryMetadataTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [300100, 301001]);

    /// <summary>Both catalogues initialize once from the same rows, retaining exact ownership across repeated preparation.</summary>
    [Fact]
    public async Task JointPreparationReturnsCompleteIdempotentSnapshots()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        await Seed(store);
        var first = await store.PrepareInventorySnapshotAsync(Command(), default);
        Assert.NotNull(first.CharacterBag.Single(row => row.ItemId == 300100).ArticleIdentity);
        Assert.NotNull(first.CharacterBag.Single(row => row.ItemId == 301001).PetIdentity);
        Assert.Equal(2UL, table.LastPetEntitlementId);
        var second = await store.PrepareInventorySnapshotAsync(Command(), default);
        Assert.Equal(first.CharacterBag, second.CharacterBag);
        Assert.Equal(first.EquippedBag, second.EquippedBag);
        Assert.Equal(2UL, table.LastPetEntitlementId);
        await Assert.ThrowsAsync<ArgumentException>(() => store.PrepareInventorySnapshotAsync(Command() with
            { ArticleInitialization = new(2, Scope, [300100], Now) }, default).AsTask());
    }

    /// <summary>Failure while allocating the second metadata family retains neither the first identity nor its counter.</summary>
    [Fact]
    public async Task PartialAllocatorExhaustionCommitsNeitherCatalogue()
    {
        var table = new MemoryPlayerTable(1) { LastPetArticleKey = PetArticleKeys.Last - 1 };
        var store = new MemoryInventoryRepository(table);
        await Seed(store);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PrepareInventorySnapshotAsync(Command(), default).AsTask());
        var saved = await store.ReadEnhancementInventoryAsync(1, Scope, default);
        Assert.All(saved.CharacterBag.Concat(saved.EquippedBag), row =>
        {
            Assert.Null(row.ArticleIdentity);
            Assert.Null(row.PetIdentity);
            Assert.Null(row.Pet);
        });
        Assert.Equal(0UL, table.LastPetEntitlementId);
        Assert.Equal(PetArticleKeys.Last - 1, table.LastPetArticleKey);
        Assert.Empty(table.Find(1)!.Loadouts);
    }

    private static InventoryMetadataPreparationCommand Command() => new(1, Scope)
    {
        ArticleInitialization = new(1, Scope, [300100], Now),
        PetInitialization = new(1, Scope, [301001], new(0, 0, Now, null, null, null), Now),
    };

    private static async Task Seed(MemoryInventoryRepository store)
    {
        InventorySlotRecord[] rows = [
            new(1, InventoryBag.CharacterBag, 1, 300100, 1, 0) { Lifetime = InventoryLifetime.Permanent },
            new(1, InventoryBag.CharacterBag, 2, 301001, 1, 0) { Lifetime = InventoryLifetime.Permanent },
        ];
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(1, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
    }
}
