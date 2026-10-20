using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

public sealed class ConditionalPurchaseTests
{
    private static readonly InventoryLifetime Deadline = InventoryLifetime.Until(DateTimeOffset.UnixEpoch.AddDays(10));
    private static readonly InventorySlotRecord Existing = new(1, InventoryBag.CharacterBag, 10, 100001, 3, 0x81)
        { Lifetime = Deadline };

    [Fact]
    public async Task MixedPurchasePreservesIdentityFlagsOrderAndMonotonicNewKeys()
    {
        var table = new MemoryPlayerTable(1);
        var inventory = new MemoryInventoryRepository(table);
        await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [Existing], default);
        var store = new MemoryShopPurchaseStore(table);
        var result = await store.PurchaseAsync(Command([
            new(100002, 1) { Mode = ShopGrantMode.MatchItem, Lifetime = InventoryLifetime.Permanent },
            Change(Existing, 8) with { Lifetime = InventoryLifetime.Until(DateTimeOffset.UnixEpoch.AddDays(20)) },
        ]), default);
        Assert.Equal(90, result!.Wallet.Lucci);
        Assert.Equal(1000, result.Grants[0].SlotKey);
        Assert.Equal(10, result.Grants[1].SlotKey);
        Assert.Equal(0x81, result.Grants[1].Flags);
        var bag = await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default);
        Assert.Equal([100001, 100002], bag.Select(s => s.ItemId));
        Assert.Equal(8, bag[0].Quantity);
        Assert.Equal(result.Grants[1].Lifetime, bag[0].Lifetime);
        var next = await store.PurchaseAsync(Command([new(100003, 1)]), default);
        Assert.Equal(1001, next!.Grants[0].SlotKey);
        Assert.Equal(2, (await store.ListRecentAsync(1, 10, default)).Count);
    }

    [Fact]
    public async Task TwoPurchasesFromOneSnapshotCannotBothChargeOrOverwriteTheItem()
    {
        var table = new MemoryPlayerTable(1);
        var inventory = new MemoryInventoryRepository(table);
        await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [Existing], default);
        var store = new MemoryShopPurchaseStore(table);
        var command = Command([Change(Existing, 8)]);
        var successes = 0;
        var conflicts = 0;
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            try
            {
                Assert.NotNull(await store.PurchaseAsync(command, default));
                Interlocked.Increment(ref successes);
            }
            catch (ShopPurchaseConflictException ex)
            {
                Assert.Equal(100001, ex.ItemId);
                Interlocked.Increment(ref conflicts);
            }
        })));
        Assert.Equal(1, successes);
        Assert.Equal(15, conflicts);
        Assert.Equal(90, (await new MemoryPlayerWalletRepository(table).FindByUserNumAsync(1, default))!.Lucci);
        Assert.Equal(8, Assert.Single(await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default)).Quantity);
        Assert.Single(await store.ListRecentAsync(1, 16, default));
    }

    [Fact]
    public async Task FullBagCanRenewButCannotAppendAndRenewInOneFailedPurchase()
    {
        var table = new MemoryPlayerTable(1);
        var inventory = new MemoryInventoryRepository(table);
        var full = Enumerable.Range(0, InventoryLimits.MaxInventorySlotsPerBag)
            .Select(i => Existing with { ItemId = 100001 + i, SlotKey = 10 + i }).ToArray();
        await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, full, default);
        var store = new MemoryShopPurchaseStore(table);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PurchaseAsync(
            Command([Change(Existing, 8), new(999999, 1) { Mode = ShopGrantMode.MatchItem }]), default).AsTask());
        Assert.Equal(full, await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default));
        Assert.Null(await new MemoryPlayerWalletRepository(table).FindByUserNumAsync(1, default));
        Assert.Empty(await store.ListRecentAsync(1, 10, default));
        Assert.NotNull(await store.PurchaseAsync(Command([Change(Existing, 8)]), default));
        Assert.Equal(full.Length, (await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default)).Count);
    }

    [Fact]
    public async Task EveryChangedExpectedFieldAndDuplicateItemPreventsACharge()
    {
        foreach (var stored in new InventorySlotRecord[][]
        {
            [], [Existing with { SlotKey = 11 }], [Existing with { Quantity = 4 }],
            [Existing with { Flags = 0 }], [Existing with { Lifetime = InventoryLifetime.Permanent }],
            [Existing, Existing with { SlotKey = 11 }],
        })
        {
            var table = new MemoryPlayerTable(1);
            var inventory = new MemoryInventoryRepository(table);
            await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, stored, default);
            var store = new MemoryShopPurchaseStore(table);
            await Assert.ThrowsAsync<ShopPurchaseConflictException>(() => store.PurchaseAsync(
                Command([Change(Existing, 8)]), default).AsTask());
            Assert.Equal(stored, await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default));
            Assert.Null(await new MemoryPlayerWalletRepository(table).FindByUserNumAsync(1, default));
            Assert.Empty(await store.ListRecentAsync(1, 10, default));
        }
    }

    [Fact]
    public async Task ExpectedAbsenceDetectsConcurrentInsertAndAppendStillAllowsInstances()
    {
        var table = new MemoryPlayerTable(1);
        var inventory = new MemoryInventoryRepository(table);
        await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [Existing], default);
        var store = new MemoryShopPurchaseStore(table);
        await Assert.ThrowsAsync<ShopPurchaseConflictException>(() => store.PurchaseAsync(
            Command([new(100001, 1) { Mode = ShopGrantMode.MatchItem }]), default).AsTask());
        Assert.NotNull(await store.PurchaseAsync(Command([new(100001, 1)]), default));
        Assert.Equal(2, (await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default)).Count);
    }

    [Fact]
    public void InvalidConditionalPlansAreRejectedBeforeStorage()
    {
        foreach (var grant in new[]
        {
            Change(Existing with { UserNum = 2 }, 8), Change(Existing with { Bag = InventoryBag.EquippedBag }, 8),
            Change(Existing with { ItemId = 99 }, 8), Change(Existing with { SlotKey = 0 }, 8),
            Change(Existing, 8) with { Mode = ShopGrantMode.AppendInstance },
            new ShopPurchaseGrant(100001, 1) { Mode = (ShopGrantMode)9 },
        })
            Assert.Throws<ArgumentException>(() => Command([grant]).Validate());
        Assert.Throws<ArgumentException>(() => Command([Change(Existing, 8), new(100001, 1)]).Validate());
    }

    private static ShopPurchaseGrant Change(InventorySlotRecord expected, int quantity) => new(100001, quantity)
        { Mode = ShopGrantMode.MatchItem, ExpectedSlot = expected, Lifetime = expected.Lifetime };

    private static ShopPurchaseCommand Command(IReadOnlyList<ShopPurchaseGrant> grants) =>
        new(new(1, 100, 0), 42, 0, "lucci", 10, 1000, grants);
}
