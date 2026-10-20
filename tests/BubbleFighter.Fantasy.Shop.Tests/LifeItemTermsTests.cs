using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Purchasing;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Life-counted stock: a ShopItem of durationType endurance sells as N life with no time limit when the build
/// has the item kind (kr-812 sub_993080), and an owned item can be bought again only once its life is used up
/// (kr-812 sub_978350).
/// </summary>
public sealed class LifeItemTermsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
    private static readonly ItemTermRules LifeRules = new([9], RejectRepeatFunction10001: true, EnduranceIsLife: true);

    private static ShopStockRow Stock(int duration) => new(1005676, 201350, true, 1, duration, 999999, 999999, 0, 0);

    private static InventorySlotRecord Owned(int quantity, InventoryLifetime lifetime) =>
        new(1, InventoryBag.CharacterBag, 10, 201350, quantity, 0) { Lifetime = lifetime };

    [Fact]
    public void EnduranceStockGrantsItsDurationAsLifeOnlyWhereTheBuildHasTheKind()
    {
        var terms = new ItemTerms_Legacy(201350, "endurance", 2000, 0, 0);

        var term = terms.ForStock(Stock(15), LifeRules);

        Assert.Equal(PurchaseGrantKind.Life, term.Kind);
        Assert.Equal(15, term.Amount);
        Assert.Equal(2000, term.Maximum);
        // Without the rule (the default, and every build without the kind) the stock is refused before charging.
        Assert.Throws<PurchaseRejectedException>(() => terms.ForStock(Stock(15)));
        Assert.Throws<PurchaseRejectedException>(() => terms.ForStock(Stock(0), LifeRules));
        Assert.Throws<PurchaseRejectedException>(() => terms.ForStock(Stock(-1), LifeRules));
    }

    [Fact]
    public void ABoughtLifeItemIsAPermanentFreeOfDeadlineRowWhoseQuantityIsTheLife()
    {
        var grant = Assert.Single(ItemPurchasePlanner.Plan(1, [new(201350, PurchaseGrantKind.Life, 15, 2000, false)], [], Now));

        Assert.Equal(15, grant.Quantity);
        Assert.Equal(InventoryLifetime.Life, grant.Lifetime);
        Assert.Null(grant.ExpectedSlot);
    }

    [Fact]
    public void AnOwnedLifeItemWithLifeLeftCannotBeBoughtAgainButAnEmptyOneCan()
    {
        var term = new PurchaseGrantTerm(201350, PurchaseGrantKind.Life, 15, 2000, false);

        Assert.Throws<PurchaseRejectedException>(() => ItemPurchasePlanner.Plan(1, [term], [Owned(3, InventoryLifetime.Life)], Now));
        var grant = Assert.Single(ItemPurchasePlanner.Plan(1, [term], [Owned(0, InventoryLifetime.Life)], Now));
        Assert.Equal(15, grant.Quantity);
        Assert.Equal(InventoryLifetime.Life, grant.Lifetime);
    }

    [Fact]
    public void ALifeGrantNeverReplacesAnotherKindAndALifeRowIsNeverTreatedAsACount()
    {
        Assert.Throws<PurchaseRejectedException>(() => ItemPurchasePlanner.Plan(1,
            [new(201350, PurchaseGrantKind.Life, 15, 2000, false)], [Owned(0, InventoryLifetime.Permanent)], Now));
        Assert.Throws<PurchaseRejectedException>(() => ItemPurchasePlanner.Plan(1,
            [new(201350, PurchaseGrantKind.Count, 1, 10, false)], [Owned(3, InventoryLifetime.Life)], Now));
        Assert.Throws<PurchaseRejectedException>(() => ItemPurchasePlanner.Plan(1,
            [new(201350, PurchaseGrantKind.Days, 7, 360, false)], [Owned(3, InventoryLifetime.Life)], Now));
    }

    [Fact]
    public void ALifeGrantAboveTheCatalogMaximumIsRefused()
    {
        Assert.Throws<PurchaseRejectedException>(() => ItemPurchasePlanner.Plan(1,
            [new(201350, PurchaseGrantKind.Life, 2001, 2000, false)], [], Now));
        Assert.Throws<PurchaseRejectedException>(() => ItemPurchasePlanner.Plan(1,
            [new(201350, PurchaseGrantKind.Life, 10, 2000, false), new(201350, PurchaseGrantKind.Life, 1995, 2000, false)], [], Now));
    }

    [Fact]
    public async Task ThePurchaseStoreKeepsTheLifeKindAndTheLifeInTheQuantity()
    {
        var table = new MemoryPlayerTable(1);
        var inventory = new MemoryInventoryRepository(table);
        var store = new MemoryShopPurchaseStore(table);
        var purchaser = new ConditionalShopPurchaser(store, inventory, new Clock(), 2);

        var result = await purchaser.CommitAsync(new(new(1, 1000, 0), 1234, 0, "lucci", 100, 10000, []),
            [new(201350, PurchaseGrantKind.Life, 15, 2000, false)], default);

        Assert.Equal(900, result!.Wallet.Lucci);
        var row = Assert.Single(await new MemoryInventoryRepository(table).ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default));
        Assert.Equal((201350, 15, InventoryLifetime.Life), (row.ItemId, row.Quantity, row.Lifetime));
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
