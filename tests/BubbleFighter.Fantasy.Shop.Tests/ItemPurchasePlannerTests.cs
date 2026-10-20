using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Purchasing;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

public sealed class ItemPurchasePlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly ShopPurchaseCommand Command = new(new(1, 1000, 0), 1234, 0, "lucci", 100, 10000, []);

    [Fact]
    public async Task CountDaysAndPermanentTermsCommitTogetherAndReconnectKeepsDeadlines()
    {
        var table = new MemoryPlayerTable(1);
        var inventory = new MemoryInventoryRepository(table);
        var store = new MemoryShopPurchaseStore(table);
        var purchaser = new ConditionalShopPurchaser(store, inventory, new Clock(), 2);
        var result = await purchaser.CommitAsync(Command,
            [new(1, PurchaseGrantKind.Count, 3, 10, false), new(2, PurchaseGrantKind.Days, 7, 360, false),
             new(3, PurchaseGrantKind.Permanent, 1, 360, false)], default);
        Assert.Equal(900, result!.Wallet.Lucci);
        Assert.Equal([3, 1, 1], result.Grants.Select(g => g.Quantity));
        Assert.Equal(InventoryLifetime.Until(Now.AddDays(7)), result.Grants[1].Lifetime);
        Assert.Equal(InventoryLifetime.Permanent, result.Grants[2].Lifetime);
        var reread = await new MemoryInventoryRepository(table).ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default);
        Assert.Equal(result.Grants, reread);
        Assert.Single(await store.ListRecentAsync(1, 10, default));
    }

    [Theory]
    [InlineData(4.5, 11.5)]
    [InlineData(0, 7)]
    [InlineData(-3, 7)]
    public void RenewalExtendsUnexpiredTimeAndRestartsExpiredTime(double oldDays, double newDays)
    {
        var existing = Slot(2) with { Lifetime = InventoryLifetime.Until(Now.AddDays(oldDays)), Flags = 1 };
        var grant = Assert.Single(ItemPurchasePlanner.Plan(1, [new(2, PurchaseGrantKind.Days, 7, 360, false)], [existing], Now));
        Assert.Equal(existing, grant.ExpectedSlot);
        Assert.Equal(InventoryLifetime.Until(Now.AddDays(newDays)), grant.Lifetime);
        Assert.Equal(ShopGrantMode.MatchItem, grant.Mode);
    }

    [Fact]
    public void CountStacksPackageDuplicatesOnceAndDoesNotInterpretAmountAsDays()
    {
        var existing = Slot(1) with { Quantity = 2 };
        var grant = Assert.Single(ItemPurchasePlanner.Plan(1,
            [new(1, PurchaseGrantKind.Count, 3, 10, false), new(1, PurchaseGrantKind.Count, 4, 10, false)], [existing], Now));
        Assert.Equal(9, grant.Quantity);
        Assert.Equal(InventoryLifetime.Permanent, grant.Lifetime);
        Assert.Equal(existing, grant.ExpectedSlot);
    }

    [Fact]
    public void DayCapUsesClientWholeDaysWithHalfSecondRounding()
    {
        var terms = new PurchaseGrantTerm[] { new(2, PurchaseGrantKind.Days, 1, 360, false) };
        var below = Slot(2) with { Lifetime = InventoryLifetime.Until(Now.AddDays(360).AddMilliseconds(-501)) };
        Assert.Single(ItemPurchasePlanner.Plan(1, terms, [below], Now));
        var above = below with { Lifetime = InventoryLifetime.Until(Now.AddDays(360).AddMilliseconds(-499)) };
        Assert.Throws<PurchaseRejectedException>(() => ItemPurchasePlanner.Plan(1, terms, [above], Now));
    }

    [Fact]
    public async Task RejectedItemInASetLeavesMoneyInventoryAndAuditUnchanged()
    {
        var table = new MemoryPlayerTable(1);
        var inventory = new MemoryInventoryRepository(table);
        var existing = Slot(2) with { Lifetime = InventoryLifetime.Permanent };
        await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [existing], default);
        var store = new MemoryShopPurchaseStore(table);
        var purchaser = new ConditionalShopPurchaser(store, inventory, new Clock(), 2);
        await Assert.ThrowsAsync<PurchaseRejectedException>(() => purchaser.CommitAsync(Command,
            [new(1, PurchaseGrantKind.Count, 3, 10, false), new(2, PurchaseGrantKind.Days, 7, 360, false)], default).AsTask());
        Assert.Equal(existing, Assert.Single(await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default)));
        Assert.Empty(await store.ListRecentAsync(1, 10, default));
        Assert.Null(await new MemoryPlayerWalletRepository(table).FindByUserNumAsync(1, default));
    }

    [Fact]
    public void RefusesUnknownFiniteLifetimeDuplicateOwnershipAndExcessCounts()
    {
        Assert.Throws<PurchaseRejectedException>(() => ItemPurchasePlanner.Plan(1,
            [new(1, PurchaseGrantKind.Days, 7, 360, false)], [Slot(1)], Now));
        Assert.Throws<PurchaseRejectedException>(() => ItemPurchasePlanner.Plan(1,
            [new(1, PurchaseGrantKind.Permanent, 1, 360, false)], [Slot(1) with { Lifetime = InventoryLifetime.Until(Now.AddDays(7)) }], Now));
        Assert.Throws<PurchaseRejectedException>(() => ItemPurchasePlanner.Plan(1,
            [new(1, PurchaseGrantKind.Count, 3, 10, false)], [Slot(1) with { Quantity = 9 }], Now));
        Assert.Throws<PurchaseRejectedException>(() => ItemPurchasePlanner.Plan(1,
            [new(1, PurchaseGrantKind.Count, 1, 10, true)], [Slot(1)], Now));
        Assert.Throws<PurchaseRejectedException>(() => ItemPurchasePlanner.Plan(1,
            [new(1, PurchaseGrantKind.Count, 1, 10, false)], [Slot(1), Slot(1) with { SlotKey = 50 }], Now));
    }

    [Fact]
    public async Task ConflictRereadsAndReplansInsteadOfLosingConcurrentGrant()
    {
        var table = new MemoryPlayerTable(1);
        var inventory = new MemoryInventoryRepository(table);
        var store = new MemoryShopPurchaseStore(table);
        var racing = new RacingStore(store, async () =>
            await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [Slot(1) with { Quantity = 4 }], default));
        var result = await new ConditionalShopPurchaser(racing, inventory, new Clock(), 2).CommitAsync(Command,
            [new(1, PurchaseGrantKind.Count, 3, 10, false)], default);
        Assert.Equal(2, racing.Attempts);
        Assert.Equal(7, Assert.Single(result!.Grants).Quantity);
        Assert.Equal(900, result.Wallet.Lucci);
        Assert.Single(await store.ListRecentAsync(1, 10, default));
    }

    [Fact]
    public async Task ConflictBudgetAndStorageErrorsDoNotRetryIndefinitely()
    {
        var table = new MemoryPlayerTable(1);
        var inventory = new MemoryInventoryRepository(table);
        var store = new MemoryShopPurchaseStore(table);
        var racing = new RacingStore(store, async () =>
            await inventory.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, [Slot(1)], default));
        var terms = new PurchaseGrantTerm[] { new(1, PurchaseGrantKind.Count, 3, 10, false) };
        await Assert.ThrowsAsync<ShopPurchaseConflictException>(() =>
            new ConditionalShopPurchaser(racing, inventory, new Clock(), 1).CommitAsync(Command, terms, default).AsTask());
        Assert.Equal(1, racing.Attempts);
        Assert.Empty(await store.ListRecentAsync(1, 10, default));
        var failing = new RacingStore(store, () => throw new IOException("storage unavailable"));
        await Assert.ThrowsAsync<IOException>(() =>
            new ConditionalShopPurchaser(failing, inventory, new Clock(), 3).CommitAsync(Command, terms, default).AsTask());
        Assert.Equal(1, failing.Attempts);
    }

    [Theory]
    [InlineData("count", 1, 7, 0, 7)]
    [InlineData("day", 1, 7, 1, 7)]
    [InlineData("day", 9, -1, 2, 1)]
    public void CatalogUsageSelectsCountDaysOrExplicitPermanent(string usage, ushort type, int duration,
        int expected, int amount)
    {
        var term = new ItemTerms_Legacy(1, usage, 360, 0, 0)
            .ForStock(new(123, 1, true, type, duration, 10, 10, 0, 0));
        Assert.Equal((PurchaseGrantKind)expected, term.Kind);
        Assert.Equal(amount, term.Amount);
    }

    private static InventorySlotRecord Slot(int item) => new(1, InventoryBag.CharacterBag, 10, item, 1, 0);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class RacingStore(IShopPurchaseStore inner, Func<Task> race) : IShopPurchaseStore
    {
        public int Attempts { get; private set; }
        public async ValueTask<ShopPurchaseResult?> PurchaseAsync(ShopPurchaseCommand command, CancellationToken ct)
        {
            if (++Attempts == 1)
                await race();
            return await inner.PurchaseAsync(command, ct);
        }
        public ValueTask<IReadOnlyList<ShopPurchaseRecord>> ListRecentAsync(long userNum, int maxCount, CancellationToken ct) =>
            inner.ListRecentAsync(userNum, maxCount, ct);
    }
}
