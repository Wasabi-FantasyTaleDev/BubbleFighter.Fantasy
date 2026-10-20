using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Currency-only rewards and complete bundles commit without fake inventory equipment.</summary>
public sealed class LotteryPurchaseTests
{
    /// <summary>Concurrent currency cards add face value exactly once per commit and allocate no inventory rows.</summary>
    [Fact]
    public async Task CurrencyCardsUseLockedWalletAndNoInventorySlots()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryShopPurchaseStore(table);
        var command = new ShopPurchaseCommand(new PlayerWallet(1, 1000, 500), 2100061, 2100061, "lucci", 0, 1000000, [])
            { LucciReward = 100 };
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.PurchaseAsync(command, default).AsTask()));
        var wallet = await new MemoryPlayerWalletRepository(table).FindByUserNumAsync(1, default);
        Assert.Equal(3000, wallet!.Lucci);
        Assert.Equal(500, wallet.Cash);
        Assert.Empty(await new MemoryInventoryRepository(table).ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default));
        Assert.All(await store.ListRecentAsync(1, 20, default), row => { Assert.Equal(0, row.ItemId); Assert.Equal(0, row.SlotKey); });
    }

    /// <summary>A stale bundle component rejects every grant and any accompanying currency reward.</summary>
    [Fact]
    public async Task BundleConflictDoesNotPartiallyGrantOrCredit()
    {
        var table = new MemoryPlayerTable(1);
        var inventory = new MemoryInventoryRepository(table);
        var existing = new InventorySlotRecord(1, InventoryBag.CharacterBag, 10, 100203, 1, 0);
        await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [existing], default);
        var command = new ShopPurchaseCommand(new PlayerWallet(1, 1000, 0), 2100143, 2100143, "lucci", 0, 1000000,
            [new(100204, 1) { Mode = ShopGrantMode.MatchItem }, new(100203, 1) { Mode = ShopGrantMode.MatchItem }])
            { LucciReward = 100 };
        var store = new MemoryShopPurchaseStore(table);
        await Assert.ThrowsAsync<ShopPurchaseConflictException>(() => store.PurchaseAsync(command, default).AsTask());
        Assert.Equal(new[] { existing }, await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default));
        Assert.Empty(await store.ListRecentAsync(1, 10, default));
    }

    /// <summary>Balance overflow rejects a reward before audit or equipment changes.</summary>
    [Fact]
    public async Task RewardOverflowLeavesStorageUnchanged()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryShopPurchaseStore(table);
        var command = new ShopPurchaseCommand(new PlayerWallet(1, int.MaxValue, 0), 2100061, 2100061, "lucci", 0, 1000000, [])
            { LucciReward = 100 };
        await Assert.ThrowsAsync<OverflowException>(() => store.PurchaseAsync(command, default).AsTask());
        Assert.Empty(await store.ListRecentAsync(1, 10, default));
    }
}
