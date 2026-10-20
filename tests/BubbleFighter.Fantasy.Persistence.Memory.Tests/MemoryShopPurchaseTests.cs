using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

public sealed class MemoryShopPurchaseTests
{
    private static ShopPurchaseCommand Command(string currency = "lucci", int price = 10) => new(
        new(1, 100, 100), 1000726, 0, currency, price, 10000, [new(207026, 0)]);

    [Fact]
    public async Task ConcurrentPurchasesCannotOverspendOrLoseGrants()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryShopPurchaseStore(table);
        var results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () => await store.PurchaseAsync(Command(), default))));
        Assert.Equal(10, results.Count(r => r is not null));
        var wallet = await new MemoryPlayerWalletRepository(table).FindByUserNumAsync(1, default);
        Assert.Equal(0, wallet!.Lucci);
        Assert.Equal(100, wallet.Cash);
        var bag = await new MemoryInventoryRepository(table).ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default);
        Assert.Equal(10, bag.Count);
        Assert.Equal(10, bag.Select(s => s.SlotKey).Distinct().Count());
        Assert.Equal(10, (await store.ListRecentAsync(1, 1024, default)).Count);
    }

    [Fact]
    public async Task AppendRacingPurchasesKeepsEveryGrant()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryShopPurchaseStore(table);
        var inventory = new MemoryInventoryRepository(table);
        var starters = Enumerable.Range(0, 8)
            .Select(i => new InventorySlotRecord(1, InventoryBag.CharacterBag, 500 + i, 300001 + i, 0, 0)).ToArray();
        var work = Enumerable.Range(0, 16).Select(i => i % 2 == 0
            ? Task.Run(async () => { await store.PurchaseAsync(Command(), default); })
            : Task.Run(async () => { await inventory.AppendMissingItemsAsync(1, InventoryBag.CharacterBag, starters, default); }));
        await Task.WhenAll(work);

        var bag = await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default);
        Assert.Equal(8, bag.Count(s => s.ItemId == 207026));
        Assert.Equal(8, bag.Count(s => s.ItemId is >= 300001 and <= 300008));
        Assert.Equal(bag.Count, bag.Select(s => s.SlotKey).Distinct().Count());
    }

    [Fact]
    public async Task AppendSkipsHeldItemsAndRefusesToOverfillTheBag()
    {
        var table = new MemoryPlayerTable(1);
        var inventory = new MemoryInventoryRepository(table);
        var held = new InventorySlotRecord(1, InventoryBag.CharacterBag, 1, 300001, 0, 0);
        await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [held], default);
        var after = await inventory.AppendMissingItemsAsync(1, InventoryBag.CharacterBag,
            [held with { SlotKey = 2 }, new InventorySlotRecord(1, InventoryBag.CharacterBag, 3, 300002, 0, 0)], default);
        Assert.Equal(new[] { 1L, 3L }, after.Select(s => s.SlotKey));

        var many = Enumerable.Range(0, 512)
            .Select(i => new InventorySlotRecord(1, InventoryBag.CharacterBag, 1000 + i, 400000 + i, 0, 0)).ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => inventory.AppendMissingItemsAsync(1, InventoryBag.CharacterBag, many, default).AsTask());
        Assert.Equal(2, (await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default)).Count);
        await Assert.ThrowsAsync<ArgumentException>(() => inventory.AppendMissingItemsAsync(1, InventoryBag.CharacterBag,
            [new InventorySlotRecord(2, InventoryBag.CharacterBag, 9, 300009, 0, 0)], default).AsTask());
    }

    [Theory]
    [InlineData("lucci")]
    [InlineData("cash")]
    public async Task CapacityFailureLeavesMoneyAndAuditUntouched(string currency)
    {
        var table = new MemoryPlayerTable(1);
        var inventory = new MemoryInventoryRepository(table);
        var money = new MemoryPlayerWalletRepository(table);
        var initial = Command(currency).SeedWallet;
        await money.UpsertAsync(initial, default);
        await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag,
            Enumerable.Range(0, 512).Select(i => new InventorySlotRecord(1, InventoryBag.CharacterBag, i, 207026, 0, 0)).ToArray(), default);
        var store = new MemoryShopPurchaseStore(table);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PurchaseAsync(Command(currency), default).AsTask());
        Assert.Equal(initial, await money.FindByUserNumAsync(1, default));
        Assert.Empty(await store.ListRecentAsync(1, 1024, default));
        Assert.Equal(512, (await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default)).Count);
    }

    [Fact]
    public async Task CancellationAndInsufficientFundsDoNotCreateWalletOrGrants()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryShopPurchaseStore(table);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PurchaseAsync(Command(), new(true)).AsTask());
        Assert.Null(await store.PurchaseAsync(Command(price: 101), default));
        Assert.Null(await new MemoryPlayerWalletRepository(table).FindByUserNumAsync(1, default));
        Assert.Empty(await store.ListRecentAsync(1, 1024, default));
    }

    [Fact]
    public async Task RemovedBundleDoesNotReuseAllocatedKeys()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryShopPurchaseStore(table);
        var first = await store.PurchaseAsync(Command() with { Grants = [new(207026, 0), new(202012, 0)] }, default);
        await new MemoryInventoryRepository(table).ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [], default);
        var second = await store.PurchaseAsync(Command(), default);
        Assert.True(second!.Grants[0].SlotKey > first!.Grants[^1].SlotKey);
        Assert.Equal(2, (await store.ListRecentAsync(1, 1024, default)).Count);
    }

    [Fact]
    public async Task CashChargePreservesLucciAndAuditRemainsBounded()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryShopPurchaseStore(table);
        var result = await store.PurchaseAsync(Command("cash", 30), default);
        Assert.Equal(100, result!.Wallet.Lucci);
        Assert.Equal(70, result.Wallet.Cash);
        for (var i = 0; i < 260; i++)
            await store.PurchaseAsync(Command(price: 0) with { PurchaseKey = 1000000 + i }, default);
        var recent = await store.ListRecentAsync(1, 1024, default);
        Assert.Equal(MemoryShopPurchaseStore.MaxRowsPerPlayer, recent.Count);
        Assert.Equal(1000259, recent[0].PurchaseKey);
    }
}
