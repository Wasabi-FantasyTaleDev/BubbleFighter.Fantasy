using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Checks atomic consumable ownership, mirrored counts and exhausted-slot removal.</summary>
public sealed class InventoryConsumptionTests
{
    /// <summary>Concurrent uses cannot consume a single entitlement twice or leave a zero-count mirror.</summary>
    [Fact]
    public async Task LastItemIsConsumedExactlyOnceAcrossBothBags()
    {
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(1));
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            await store.ReplaceSlotsAsync(1, bag, [new(1, bag, 1, 100443, 1, 0)], default);
        var uses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () => await store.ConsumeItemAsync(1, 100443, default))));
        Assert.Equal(1, uses.Count(count => count == 0));
        Assert.Equal(7, uses.Count(count => count is null));
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            Assert.Empty(await store.ListSlotsAsync(1, bag, 512, default));
    }

    /// <summary>Expired stock is never consumed; a valid use updates both views without changing other items.</summary>
    [Fact]
    public async Task ExpiryAndMirroredCountsAreRespected()
    {
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(1));
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            await store.ReplaceSlotsAsync(1, bag,
                [new(1, bag, 1, 100443, 3, 0), new(1, bag, 2, 100444, 2, 0) { Lifetime = InventoryLifetime.Until(DateTimeOffset.UnixEpoch) }], default);
        Assert.Null(await store.ConsumeItemAsync(1, 100444, default));
        Assert.Equal(2, await store.ConsumeItemAsync(1, 100443, default));
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            Assert.All(await store.ListSlotsAsync(1, bag, 512, default), row => Assert.Equal(2, row.Quantity));
    }
}
