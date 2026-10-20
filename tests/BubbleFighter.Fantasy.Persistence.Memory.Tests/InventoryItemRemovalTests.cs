using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Protects owned entitlements against repeated, concurrent and stale expired-item requests.</summary>
public sealed class InventoryItemRemovalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    private static readonly InventoryBag[] Bags = [InventoryBag.CharacterBag, InventoryBag.EquippedBag];

    private static InventorySlotRecord Row(long user, InventoryBag bag, int item, InventoryLifetime lifetime) =>
        new(user, bag, item, item, 1, 0) { Lifetime = lifetime };

    /// <summary>Only one concurrent request changes both mirrors; unrelated expired items and users remain intact.</summary>
    [Fact]
    public async Task ConcurrentRequestsRemoveOneIdentityOnceAcrossBothBags()
    {
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(4));
        foreach (var user in new[] { 1L, 2L })
            foreach (var bag in Bags)
                await store.ReplaceSlotsAsync(user, bag,
                    [Row(user, bag, 202001, InventoryLifetime.Until(Now)),
                     Row(user, bag, 202002, InventoryLifetime.Until(Now.AddDays(-1)))], default);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return await store.RemoveExpiredItemAsync(1, 202001, Now, default);
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(requests);
        var removed = Assert.Single(results, result => result is { Count: > 0 });
        Assert.NotNull(removed);
        Assert.Equal(Bags, removed.Select(row => row.Bag));
        Assert.Equal(7, results.Count(result => result is { Count: 0 }));
        foreach (var bag in Bags)
        {
            Assert.Equal(202002, Assert.Single(await store.ListSlotsAsync(1, bag, 512, default)).ItemId);
            Assert.Equal(2, (await store.ListSlotsAsync(2, bag, 512, default)).Count);
        }
    }

    /// <summary>A renewed mirror prevents a partial delete even when the character-bag row already expired.</summary>
    [Fact]
    public async Task AnyActiveMirrorRejectsTheEntireRemoval()
    {
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(1));
        var expired = Row(1, InventoryBag.CharacterBag, 202001, InventoryLifetime.Until(Now));
        var renewed = Row(1, InventoryBag.EquippedBag, 202001, InventoryLifetime.Until(Now.AddMilliseconds(1)));
        await store.ReplaceSlotsAsync(1, expired.Bag, [expired], default);
        await store.ReplaceSlotsAsync(1, renewed.Bag, [renewed], default);
        Assert.Null(await store.RemoveExpiredItemAsync(1, 202001, Now, default));
        Assert.Equal(expired, Assert.Single(await store.ListSlotsAsync(1, expired.Bag, 512, default)));
        Assert.Equal(renewed, Assert.Single(await store.ListSlotsAsync(1, renewed.Bag, 512, default)));
    }

    /// <summary>Permanent, unspecified and positive-life entitlements cannot be discarded as expired.</summary>
    [Theory]
    [InlineData(InventoryLifetimeKind.Unspecified)]
    [InlineData(InventoryLifetimeKind.Permanent)]
    [InlineData(InventoryLifetimeKind.Life)]
    public async Task ActiveKindsAreNeverRemoved(InventoryLifetimeKind kind)
    {
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(1));
        var row = Row(1, InventoryBag.CharacterBag, 202001, new(kind, null));
        await store.ReplaceSlotsAsync(1, row.Bag, [row], default);
        Assert.Null(await store.RemoveExpiredItemAsync(1, row.ItemId, Now, default));
        Assert.Equal(row, Assert.Single(await store.ListSlotsAsync(1, row.Bag, 512, default)));
    }

    /// <summary>A real purchase renewal remains owned after an old discard request arrives.</summary>
    [Fact]
    public async Task PurchaseRenewalWinsOverAStaleExpiredItemRequest()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        var expired = Row(1, InventoryBag.CharacterBag, 202001, InventoryLifetime.Until(Now));
        foreach (var bag in Bags)
            await store.ReplaceSlotsAsync(1, bag, [expired with { Bag = bag }], default);
        var command = new ShopPurchaseCommand(new(1, 100, 0), 42, 0, "lucci", 10, 1000,
            [new(202001, 1) { Mode = ShopGrantMode.MatchItem, ExpectedSlot = expired,
                Lifetime = InventoryLifetime.Until(Now.AddDays(3)) }]);
        Assert.NotNull(await new MemoryShopPurchaseStore(table).PurchaseAsync(command, default));
        Assert.Null(await store.RemoveExpiredItemAsync(1, 202001, Now, default));
        Assert.Equal(Now.AddDays(3).ToUnixTimeMilliseconds(),
            Assert.Single(await store.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default)).Lifetime.ExpiresAtUnixMilliseconds);
        Assert.Single(await store.ListSlotsAsync(1, InventoryBag.EquippedBag, 512, default));
    }

    /// <summary>A racing renewal either commits and stays owned, or detects deletion before charging the wallet.</summary>
    [Fact]
    public async Task RenewalAndRemovalHaveOnlySerializedOutcomes()
    {
        var table = new MemoryPlayerTable(1);
        var inventory = new MemoryInventoryRepository(table);
        var purchases = new MemoryShopPurchaseStore(table);
        var expired = Row(1, InventoryBag.CharacterBag, 202001, InventoryLifetime.Until(Now));
        foreach (var bag in Bags)
            await inventory.ReplaceSlotsAsync(1, bag, [expired with { Bag = bag }], default);
        var command = new ShopPurchaseCommand(new(1, 100, 0), 42, 0, "lucci", 10, 1000,
            [new(202001, 1) { Mode = ShopGrantMode.MatchItem, ExpectedSlot = expired,
                Lifetime = InventoryLifetime.Until(Now.AddDays(3)) }]);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewal = Task.Run(async () =>
        {
            await start.Task;
            try { return await purchases.PurchaseAsync(command, default); }
            catch (ShopPurchaseConflictException) { return null; }
        });
        var removal = Task.Run(async () =>
        {
            await start.Task;
            return await inventory.RemoveExpiredItemAsync(1, 202001, Now, default);
        });
        start.SetResult();
        await Task.WhenAll(renewal, removal);
        if (await renewal is { } committed)
        {
            Assert.Null(await removal);
            Assert.Equal(90, committed.Wallet.Lucci);
            Assert.Equal(Now.AddDays(3).ToUnixTimeMilliseconds(),
                Assert.Single(await inventory.ListSlotsAsync(1, InventoryBag.CharacterBag, 512, default)).Lifetime.ExpiresAtUnixMilliseconds);
            Assert.Single(await inventory.ListSlotsAsync(1, InventoryBag.EquippedBag, 512, default));
            Assert.Single(await purchases.ListRecentAsync(1, 10, default));
        }
        else
        {
            Assert.Equal(2, (await removal)!.Count);
            foreach (var bag in Bags)
                Assert.Empty(await inventory.ListSlotsAsync(1, bag, 512, default));
            Assert.Null(await new MemoryPlayerWalletRepository(table).FindByUserNumAsync(1, default));
            Assert.Empty(await purchases.ListRecentAsync(1, 10, default));
        }
    }

    /// <summary>Pre-cancelled requests leave both views byte-for-byte equivalent to the seeded records.</summary>
    [Fact]
    public async Task CancellationDoesNotRemoveEitherMirror()
    {
        var store = new MemoryInventoryRepository(new MemoryPlayerTable(1));
        foreach (var bag in Bags)
            await store.ReplaceSlotsAsync(1, bag, [Row(1, bag, 202001, InventoryLifetime.Until(Now))], default);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RemoveExpiredItemAsync(1, 202001, Now, cancelled.Token).AsTask());
        foreach (var bag in Bags)
            Assert.Equal(Row(1, bag, 202001, InventoryLifetime.Until(Now)),
                Assert.Single(await store.ListSlotsAsync(1, bag, 512, default)));
    }

    /// <summary>Zero-life rows end, but an active duplicate rejects deletion of the identity's expired rows.</summary>
    [Fact]
    public void PlannerChecksAllDuplicatesAndRejectsForeignRows()
    {
        var life = Row(1, InventoryBag.CharacterBag, 202001, InventoryLifetime.Life) with { Quantity = 0 };
        Assert.Equal(life, Assert.Single(InventoryItemRemoval.SelectExpired(1, 202001, [life], Now)!));
        Assert.Null(InventoryItemRemoval.SelectExpired(1, 202001, [life, life with { Quantity = 1 }], Now));
        Assert.Throws<ArgumentException>(() => InventoryItemRemoval.SelectExpired(1, 202001,
            [life, Row(2, InventoryBag.CharacterBag, 202002, InventoryLifetime.Permanent)], Now));
        Assert.Throws<ArgumentException>(() => InventoryItemRemoval.SelectExpired(1, 202001,
            [life with { Bag = (InventoryBag)2 }], Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => InventoryItemRemoval.SelectExpired(1, 202001,
            Enumerable.Repeat(life, 2 * InventoryLimits.MaxInventorySlotsPerBag + 1).ToArray(), Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => InventoryItemRemoval.SelectExpired(1, 202001,
            Enumerable.Repeat(life, InventoryLimits.MaxInventorySlotsPerBag + 1).ToArray(), Now));
    }
}
