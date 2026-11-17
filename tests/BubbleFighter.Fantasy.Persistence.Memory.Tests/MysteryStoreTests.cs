using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.MysteryStore;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Verifies the memory Mysterystore commits coins, rewards and the selection together.</summary>
public sealed class MysteryStoreTests
{
    private const int Coin = 100385;
    private const int OfferKey = 2000001;
    private const string Revision = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [Coin, .. Enumerable.Range(101, 20)]);

    /// <summary>New build scopes cannot grow beyond the configured per-player bound.</summary>
    [Fact]
    public async Task ScopeCapacityDoesNotReplaceExistingSelection()
    {
        var repository = new MemoryInventoryRepository(new(1), 1) { MysteryStoreClock = new FixedClock(Now) };
        await repository.ReplaceSlotsAsync(7, InventoryBag.CharacterBag, [], default);
        IMysteryStore store = repository;
        var original = await store.EnsureAsync(new(7, Scope, Board(), 60), default);
        var other = new InventoryLoadoutScope("other", [Coin, .. Enumerable.Range(101, 20)]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.EnsureAsync(new(7, other, Board(), 60), default));
        var again = await store.EnsureAsync(new(7, Scope, Board(), 60), default);
        Assert.Equal(original.Selection!.Token, again.Selection!.Token);
        Assert.Equal(MysteryStoreOutcome.Unchanged, again.Outcome);
    }

    /// <summary>Concurrent copies of a purchase debit and grant exactly once.</summary>
    [Fact]
    public async Task ConcurrentPurchaseIsReplayedWithoutAnotherDebit()
    {
        var repository = new MemoryInventoryRepository(new(1)) { MysteryStoreClock = new FixedClock(Now) };
        await repository.ReplaceSlotsAsync(7, InventoryBag.CharacterBag,
            [new(7, InventoryBag.CharacterBag, 1, Coin, 10, 0) { Lifetime = InventoryLifetime.Permanent }], default);
        IMysteryStore store = repository;
        var opened = await store.EnsureAsync(new(7, Scope, Board(), 60), default);
        var command = new MysteryStorePurchaseCommand(new(7, Scope), Revision, opened.Selection!.Token, OfferKey, 3);
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(async () => await store.PurchaseAsync(command, default)));
        var results = await Task.WhenAll(tasks);
        Assert.Single(results, result => result.Outcome == MysteryStoreOutcome.Purchased);
        Assert.Single(results, result => result.Outcome == MysteryStoreOutcome.AlreadyPurchased);
        foreach (var result in results)
        {
            Assert.Equal(7, result.Inventory!.CharacterBag.Single(row => row.ItemId == Coin).Quantity);
            Assert.Equal(2, result.Inventory.CharacterBag.Single(row => row.ItemId == 101).Quantity);
        }
    }

    /// <summary>Metadata initialization uses locked provider time rather than the earlier caller timestamp.</summary>
    [Fact]
    public async Task ExpiredPetIsNotInitializedByStaleCallerTime()
    {
        var repository = new MemoryInventoryRepository(new(1)) { MysteryStoreClock = new FixedClock(Now) };
        await repository.ReplaceSlotsAsync(7, InventoryBag.CharacterBag,
        [
            new(7, InventoryBag.CharacterBag, 1, Coin, 10, 0) { Lifetime = InventoryLifetime.Permanent },
            new(7, InventoryBag.CharacterBag, 2, 110, 1, 0) { Lifetime = InventoryLifetime.Until(Now.AddSeconds(-1)) },
        ], default);
        IMysteryStore store = repository;
        var opened = await store.EnsureAsync(new(7, Scope, Board(), 60), default);
        var inventory = new InventoryMetadataPreparationCommand(7, Scope)
        {
            PetInitialization = new(7, Scope, [110], new(0, 0, Now.AddHours(1), null, null, null), Now.AddMinutes(-1)),
        };
        var result = await store.PurchaseAsync(new(inventory, Revision, opened.Selection!.Token, OfferKey, 3), default);
        Assert.Equal(MysteryStoreOutcome.Purchased, result.Outcome);
        Assert.Null(result.Inventory!.CharacterBag.Single(row => row.ItemId == 110).PetIdentity);
    }

    /// <summary>Open creates a selection, a purchase pays and grants once, and a refresh replaces the cards for its cost.</summary>
    [Fact]
    public async Task OpenPurchaseReplayAndRefreshCommitAtomically()
    {
        var repository = new MemoryInventoryRepository(new(1)) { MysteryStoreClock = new FixedClock(Now) };
        IMysteryStore store = repository;
        await repository.ReplaceSlotsAsync(7, InventoryBag.CharacterBag,
            [new(7, InventoryBag.CharacterBag, 1, Coin, 10, 0) { Lifetime = InventoryLifetime.Permanent }], default);

        var opened = await store.EnsureAsync(new(7, Scope, Board(), 60), default);
        Assert.Equal(MysteryStoreOutcome.SelectionCreated, opened.Outcome);
        var token = opened.Selection!.Token;
        Assert.Equal(MysteryStoreOutcome.Unchanged, (await store.EnsureAsync(new(7, Scope, Board(), 60), default)).Outcome);

        var bought = await store.PurchaseAsync(new(new(7, Scope), Revision, token, OfferKey, 3), default);
        Assert.Equal(MysteryStoreOutcome.Purchased, bought.Outcome);
        Assert.Equal(OfferKey, bought.ReceiptNativeKey);
        Assert.Equal(1u, bought.Selection!.PurchasedMask);
        Assert.Equal(7, bought.Inventory!.CharacterBag.Single(row => row.ItemId == Coin).Quantity);
        Assert.Equal(2, bought.Inventory.CharacterBag.Single(row => row.ItemId == 101).Quantity);

        var again = await store.PurchaseAsync(new(new(7, Scope), Revision, token, OfferKey, 3), default);
        Assert.Equal(MysteryStoreOutcome.AlreadyPurchased, again.Outcome);
        Assert.Equal(7, again.Inventory!.CharacterBag.Single(row => row.ItemId == Coin).Quantity);

        var refreshed = await store.RefreshAsync(new(new(7, Scope), token, 2, Board(), 60), default);
        Assert.Equal(MysteryStoreOutcome.Refreshed, refreshed.Outcome);
        Assert.True(refreshed.Selection!.Token > token);
        Assert.Equal(0u, refreshed.Selection.PurchasedMask);
        Assert.Equal(5, refreshed.Inventory!.CharacterBag.Single(row => row.ItemId == Coin).Quantity);

        var stale = await store.PurchaseAsync(new(new(7, Scope), Revision, token, OfferKey + 1, 3), default);
        Assert.Equal(MysteryStoreOutcome.StaleToken, stale.Outcome);
    }

    private static MysteryStoreBoard Board() => new(Revision, Coin, 2, false,
        Enumerable.Range(0, 6).Select(index => new MysteryStoreOffer(OfferKey + index, 3, 0, string.Empty,
            [new(101 + index, InventoryGrantKind.Count, 2, 99, false)])).ToArray());

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
