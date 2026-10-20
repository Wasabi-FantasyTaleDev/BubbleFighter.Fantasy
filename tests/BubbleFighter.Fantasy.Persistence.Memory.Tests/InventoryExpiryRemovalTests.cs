using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>The store-level expiry removal: only the named player's ended rows, in both bags.</summary>
public sealed class InventoryExpiryRemovalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static InventorySlotRecord Row(long user, InventoryBag bag, int itemId, InventoryLifetime lifetime) =>
        new(user, bag, itemId, itemId, 1, 0) { Lifetime = lifetime };

    [Fact]
    public void TheRuleTreatsOnlyAPassedDeadlineAsEnded()
    {
        Assert.True(InventoryExpiry.IsExpired(Row(1, InventoryBag.CharacterBag, 1, InventoryLifetime.Until(Now)), Now));
        Assert.False(InventoryExpiry.IsExpired(Row(1, InventoryBag.CharacterBag, 1, InventoryLifetime.Until(Now.AddMilliseconds(1))), Now));
        Assert.False(InventoryExpiry.IsExpired(Row(1, InventoryBag.CharacterBag, 1, InventoryLifetime.Permanent), Now));
        Assert.False(InventoryExpiry.IsExpired(Row(1, InventoryBag.CharacterBag, 1, default), Now));
    }

    [Fact]
    public async Task OnlyEndedRowsOfTheNamedPlayerAreDeletedAndReturned()
    {
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(4));
        var ended = InventoryLifetime.Until(Now.AddHours(-1));
        var open = InventoryLifetime.Until(Now.AddHours(1));
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            await store.ReplaceSlotsAsync(1, bag, [Row(1, bag, 10, ended), Row(1, bag, 11, open), Row(1, bag, 12, InventoryLifetime.Permanent)], default);
            await store.ReplaceSlotsAsync(2, bag, [Row(2, bag, 10, ended)], default);
        }

        var removed = await store.RemoveExpiredAsync(1, Now, default);

        Assert.Equal([(InventoryBag.CharacterBag, 10), (InventoryBag.EquippedBag, 10)], removed.Select(r => (r.Bag, r.ItemId)));
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            Assert.Equal([11, 12], (await store.ListSlotsAsync(1, bag, 512, default)).Select(r => r.ItemId));
            Assert.Single(await store.ListSlotsAsync(2, bag, 512, default));
        }
        Assert.Empty(await store.RemoveExpiredAsync(1, Now, default));
        Assert.Empty(await store.RemoveExpiredAsync(99, Now, default));
    }
}
