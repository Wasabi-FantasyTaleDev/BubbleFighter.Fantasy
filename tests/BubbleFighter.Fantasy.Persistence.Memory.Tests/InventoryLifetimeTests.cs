using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Persistence.Memory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

public sealed class InventoryLifetimeTests
{
    [Fact]
    public void UnknownIsNotPermanentAndDeadlinesAreAbsolute()
    {
        Assert.Equal(InventoryLifetimeKind.Unspecified, default(InventoryLifetime).Kind);
        Assert.NotEqual(default, InventoryLifetime.Permanent);
        var instant = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.FromHours(9));
        Assert.Equal(InventoryLifetime.Until(instant), InventoryLifetime.Until(instant.ToUniversalTime()));
    }

    [Theory]
    [InlineData(0, 1L)]
    [InlineData(1, 1L)]
    [InlineData(2, null)]
    [InlineData(3, 1L)]
    [InlineData(4, null)]
    [InlineData(2, long.MaxValue)]
    public void InvalidLifetimeCannotReachAStore(int kind, long? deadline)
    {
        Assert.ThrowsAny<ArgumentException>(() => new InventoryLifetime((InventoryLifetimeKind)kind, deadline));
    }

    [Fact]
    public async Task PurchaseAndEquipmentRoundTripPreserveEachGrantLifetime()
    {
        var table = new MemoryPlayerTable(1);
        var expiry = InventoryLifetime.Until(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        var command = new ShopPurchaseCommand(new(1, 100, 0), 42, 0, "lucci", 10, 1000,
            [new(100001, 1) { Lifetime = expiry }, new(100002, 1) { Lifetime = InventoryLifetime.Permanent }]);
        var result = await new MemoryShopPurchaseStore(table).PurchaseAsync(command, default);
        Assert.Equal(90, result!.Wallet.Lucci);
        Assert.Equal([expiry, InventoryLifetime.Permanent], result.Grants.Select(g => g.Lifetime));

        var inventory = new MemoryInventoryRepository(table);
        Assert.Equal(expiry, (await inventory.SetSlotWornAsync(1, 100001, true, default))!.Lifetime);
        var selection = await inventory.UpdateWornSelectionAsync(1, [new(100001, 100002, 100001)], 100002, true, default);
        Assert.NotNull(selection);
        Assert.Equal([expiry, InventoryLifetime.Permanent], selection.Select(g => g.Lifetime));
        await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, selection, default);
        var reread = await new MemoryInventoryRepository(table).ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default);
        Assert.Equal(selection, reread);

        var merged = await inventory.AppendMissingItemsAsync(1, InventoryBag.CharacterBag,
            [result.Grants[0] with { Lifetime = InventoryLifetime.Permanent }, new(1, InventoryBag.CharacterBag, 2000, 100003, 1, 0)], default);
        Assert.Equal(expiry, merged[0].Lifetime);
        Assert.Equal(InventoryLifetimeKind.Unspecified, merged[2].Lifetime.Kind);
    }

    [Fact]
    public async Task FailedPurchaseLeavesExistingDeadlineWalletAndAuditUntouched()
    {
        var table = new MemoryPlayerTable(1);
        var inventory = new MemoryInventoryRepository(table);
        var expiry = InventoryLifetime.Until(DateTimeOffset.UnixEpoch.AddDays(7));
        var existing = new InventorySlotRecord(1, InventoryBag.CharacterBag, 20, 100001, 1, 0) { Lifetime = expiry };
        await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [existing], default);
        var store = new MemoryShopPurchaseStore(table);
        var command = new ShopPurchaseCommand(new(1, 0, 0), 42, 0, "lucci", 1, 1000,
            [new(100001, 1) { Lifetime = InventoryLifetime.Permanent }]);
        Assert.Null(await store.PurchaseAsync(command, default));
        Assert.Equal(existing, Assert.Single(await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default)));
        Assert.Null(await new MemoryPlayerWalletRepository(table).FindByUserNumAsync(1, default));
        Assert.Empty(await store.ListRecentAsync(1, 1, default));
    }
}
