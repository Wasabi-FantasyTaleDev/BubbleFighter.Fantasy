using BubbleFighter.Fantasy.Contracts.CashInbox;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Tests;

/// <summary>
/// Persistence contract tests shared by every <see cref="ICashInboxStore"/> provider: monotonic non-reused slot
/// identifiers, idempotent claim keys, owner/state/expiry enforcement, conditional grant conflict rejection,
/// cancellation safety, and atomic bag/wallet/inbox state transitions.
/// </summary>
public abstract class CashInboxStoreContractTests
{
    /// <summary>Holds the store and supporting player repositories for one isolated test run.</summary>
    protected interface ICashInboxFixture : IAsyncDisposable
    {
        /// <summary>Gets the store under test.</summary>
        ICashInboxStore Store { get; }

        /// <summary>Creates a player account and returns its assigned <c>user_num</c>.</summary>
        ValueTask<long> CreatePlayerAsync(string username, CancellationToken ct);

        /// <summary>Reads the player's current regular character bag.</summary>
        ValueTask<IReadOnlyList<InventorySlotRecord>> ReadCharacterBagAsync(long userNum, CancellationToken ct);

        /// <summary>Replaces the player's regular character bag for setup.</summary>
        ValueTask ReplaceCharacterBagAsync(long userNum, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct);

        /// <summary>Reads the player's current wallet, or null if none exists yet.</summary>
        ValueTask<PlayerWallet?> ReadWalletAsync(long userNum, CancellationToken ct);
    }

    /// <summary>Creates an empty isolated fixture for one test case.</summary>
    protected abstract ValueTask<ICashInboxFixture> CreateFixtureAsync();

    [Fact]
    public async Task GrantAllocatesMonotonicSlotIdsNeverReusedAcrossPickupOrExpiry()
    {
        await using var fixture = await CreateFixtureAsync();
        var user = await fixture.CreatePlayerAsync("alice", default);

        var first = await fixture.Store.GrantAsync(Grant(user, stockKey: 1001, itemKey: 50001, createdAt: 1000, expiresAt: 0), default);
        var second = await fixture.Store.GrantAsync(Grant(user, stockKey: 1002, itemKey: 50002, createdAt: 1001, expiresAt: 2000), default);
        Assert.Equal(CashInboxGrantOutcome.Granted, first.Outcome);
        Assert.Equal(CashInboxGrantOutcome.Granted, second.Outcome);
        Assert.True(first.Entry!.CashSlotId > 0);
        Assert.True(second.Entry!.CashSlotId > first.Entry.CashSlotId);

        var pickupFirst = await fixture.Store.PickUpAsync(
            new CashInboxPickupCommand(
                "kr-812",
                user,
                first.Entry.CashSlotId,
                new PlayerWallet(user, 500, 0),
                1000,
                [new ShopPurchaseGrant(50001, 1) { Mode = ShopGrantMode.MatchItem, Lifetime = InventoryLifetime.Permanent }],
                1500),
            default);
        Assert.Equal(CashInboxPickupOutcome.PickedUp, pickupFirst.Outcome);

        var third = await fixture.Store.GrantAsync(Grant(user, stockKey: 1003, itemKey: 50003, createdAt: 2500, expiresAt: 0), default);
        Assert.Equal(CashInboxGrantOutcome.Granted, third.Outcome);
        Assert.True(third.Entry!.CashSlotId > second.Entry.CashSlotId);

        var expiredPickup = await fixture.Store.PickUpAsync(
            new CashInboxPickupCommand(
                "kr-812",
                user,
                second.Entry.CashSlotId,
                new PlayerWallet(user, 500, 0),
                1000,
                [new ShopPurchaseGrant(50002, 1) { Mode = ShopGrantMode.MatchItem, Lifetime = InventoryLifetime.Permanent }],
                2500),
            default);
        Assert.Equal(CashInboxPickupOutcome.NotFound, expiredPickup.Outcome);
        Assert.Equal(CashInboxEntryState.Expired, (await fixture.Store.FindBySlotIdAsync("kr-812", second.Entry.CashSlotId, default))?.State);

        var active = await fixture.Store.ListActiveAsync("kr-812", user, 16, 2500, default);
        Assert.Equal([third.Entry.CashSlotId], active.Select(e => e.CashSlotId));
    }

    [Fact]
    public async Task ClaimKeyIsIdempotentPerPlayerAndBuild()
    {
        await using var fixture = await CreateFixtureAsync();
        var alice = await fixture.CreatePlayerAsync("alice", default);
        var bob = await fixture.CreatePlayerAsync("bob", default);

        var first = await fixture.Store.GrantAsync(Grant(alice, 1001, 50001, 1000, 0, claimKey: "promo:2026"), default);
        var duplicate = await fixture.Store.GrantAsync(Grant(alice, 1001, 50001, 1005, 0, claimKey: "promo:2026"), default);
        var otherProfile = await fixture.Store.GrantAsync(Grant(alice, 1001, 50001, 1010, 0, profileId: "kr-1256", claimKey: "promo:2026"), default);
        var otherUser = await fixture.Store.GrantAsync(Grant(bob, 1001, 50001, 1015, 0, claimKey: "promo:2026"), default);

        Assert.Equal(CashInboxGrantOutcome.Granted, first.Outcome);
        Assert.Equal(CashInboxGrantOutcome.AlreadyClaimed, duplicate.Outcome);
        Assert.Null(duplicate.Entry);
        Assert.Equal(CashInboxGrantOutcome.Granted, otherProfile.Outcome);
        Assert.Equal(CashInboxGrantOutcome.Granted, otherUser.Outcome);
        Assert.Single(await fixture.Store.ListActiveAsync("kr-812", alice, 16, 1020, default));
    }

    [Fact]
    public async Task BoxLimitAndMissingOwnerAreEnforcedWithoutBlockingExpiredEntries()
    {
        await using var fixture = await CreateFixtureAsync();
        var alice = await fixture.CreatePlayerAsync("alice", default);

        var missing = await fixture.Store.GrantAsync(Grant(alice + 999, 1001, 50001, 1000, 0), default);
        Assert.Equal(CashInboxGrantOutcome.OwnerMissing, missing.Outcome);

        Assert.Equal(CashInboxGrantOutcome.Granted, (await fixture.Store.GrantAsync(Grant(alice, 1001, 50001, 1000, 1500, maxEntries: 2), default)).Outcome);
        Assert.Equal(CashInboxGrantOutcome.Granted, (await fixture.Store.GrantAsync(Grant(alice, 1002, 50002, 1001, 0, maxEntries: 2), default)).Outcome);
        Assert.Equal(CashInboxGrantOutcome.BoxFull, (await fixture.Store.GrantAsync(Grant(alice, 1003, 50003, 1002, 0, maxEntries: 2), default)).Outcome);

        var bypassed = await fixture.Store.GrantAsync(Grant(alice, 1003, 50003, 1003, 0, maxEntries: 2, ignoreInboxLimit: true), default);
        Assert.Equal(CashInboxGrantOutcome.Granted, bypassed.Outcome);

        // Once slot #1 has expired at t=2000, only 2 active entries remain, so a new maxEntries=3 grant succeeds.
        var afterExpiry = await fixture.Store.GrantAsync(Grant(alice, 1004, 50004, 2000, 0, maxEntries: 3), default);
        Assert.Equal(CashInboxGrantOutcome.Granted, afterExpiry.Outcome);
    }

    [Fact]
    public async Task PickupValidatesOwnerConditionsBagCapacityCancellationAndCommitsLucci()
    {
        await using var fixture = await CreateFixtureAsync();
        var alice = await fixture.CreatePlayerAsync("alice", default);
        var bob = await fixture.CreatePlayerAsync("bob", default);

        var existingDeadline = InventoryLifetime.Until(DateTimeOffset.FromUnixTimeMilliseconds(10_000));
        var existingSlot = new InventorySlotRecord(alice, InventoryBag.CharacterBag, 50, 50010, 1, 0x01) { Lifetime = existingDeadline };
        await fixture.ReplaceCharacterBagAsync(alice, [existingSlot], default);

        var entry = (await fixture.Store.GrantAsync(Grant(alice, 2001, 50010, 1000, 0, note: "bonus"), default)).Entry!;

        var wrongOwner = await fixture.Store.PickUpAsync(
            new CashInboxPickupCommand(
                "kr-812",
                bob,
                entry.CashSlotId,
                new PlayerWallet(bob, 100, 0),
                1000,
                [new ShopPurchaseGrant(50011, 1) { Mode = ShopGrantMode.MatchItem, Lifetime = InventoryLifetime.Permanent }],
                1100),
            default);
        Assert.Equal(CashInboxPickupOutcome.OwnerMismatch, wrongOwner.Outcome);
        Assert.Equal(CashInboxEntryState.Active, (await fixture.Store.FindBySlotIdAsync("kr-812", entry.CashSlotId, default))?.State);

        var staleSnapshot = await fixture.Store.PickUpAsync(
            new CashInboxPickupCommand(
                "kr-812",
                alice,
                entry.CashSlotId,
                new PlayerWallet(alice, 100, 0),
                1000,
                [new ShopPurchaseGrant(50010, 1) { Mode = ShopGrantMode.MatchItem, ExpectedSlot = null, Lifetime = InventoryLifetime.Permanent }],
                1100),
            default);
        Assert.Equal(CashInboxPickupOutcome.AlreadyOwned, staleSnapshot.Outcome);
        Assert.Equal(CashInboxEntryState.Active, (await fixture.Store.FindBySlotIdAsync("kr-812", entry.CashSlotId, default))?.State);
        Assert.Null(await fixture.ReadWalletAsync(alice, default));

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.PickUpAsync(
            new CashInboxPickupCommand(
                "kr-812",
                alice,
                entry.CashSlotId,
                new PlayerWallet(alice, 100, 0),
                1000,
                [new ShopPurchaseGrant(50010, 1) { Mode = ShopGrantMode.MatchItem, ExpectedSlot = existingSlot, Lifetime = InventoryLifetime.Permanent }],
                1100)
            { LucciReward = 250 },
            new CancellationToken(true)).AsTask());
        Assert.NotNull(cancelled);
        Assert.Equal(CashInboxEntryState.Active, (await fixture.Store.FindBySlotIdAsync("kr-812", entry.CashSlotId, default))?.State);
        Assert.Null(await fixture.ReadWalletAsync(alice, default));

        var succeeded = await fixture.Store.PickUpAsync(
            new CashInboxPickupCommand(
                "kr-812",
                alice,
                entry.CashSlotId,
                new PlayerWallet(alice, 100, 20),
                1000,
                [
                    new ShopPurchaseGrant(50010, 1)
                    {
                        Mode = ShopGrantMode.MatchItem,
                        ExpectedSlot = existingSlot,
                        Lifetime = InventoryLifetime.Permanent,
                    },
                    new ShopPurchaseGrant(50012, 5)
                    {
                        Mode = ShopGrantMode.MatchItem,
                        Lifetime = InventoryLifetime.Permanent,
                    },
                ],
                1200)
            { LucciReward = 250 },
            default);

        Assert.Equal(CashInboxPickupOutcome.PickedUp, succeeded.Outcome);
        Assert.Equal(CashInboxEntryState.PickedUp, succeeded.Entry?.State);
        Assert.Equal(350, succeeded.PurchaseResult?.Wallet.Lucci);
        Assert.Equal(20, succeeded.PurchaseResult?.Wallet.Cash);
        Assert.Equal(2, succeeded.PurchaseResult?.Grants.Count);

        var bagAfter = await fixture.ReadCharacterBagAsync(alice, default);
        Assert.Equal(2, bagAfter.Count);
        Assert.Equal(50, bagAfter[0].SlotKey);
        Assert.Equal(0x01, bagAfter[0].Flags);
        Assert.Equal(InventoryLifetime.Permanent, bagAfter[0].Lifetime);
        Assert.Equal(50012, bagAfter[1].ItemId);
        Assert.Equal(1000, bagAfter[1].SlotKey);

        var replay = await fixture.Store.PickUpAsync(
            new CashInboxPickupCommand(
                "kr-812",
                alice,
                entry.CashSlotId,
                new PlayerWallet(alice, 100, 20),
                1000,
                [new ShopPurchaseGrant(50012, 5)],
                1201),
            default);
        Assert.Equal(CashInboxPickupOutcome.NotFound, replay.Outcome);
    }

    [Fact]
    public async Task FullCharacterBagRefusesAppendPickupWithoutMutatingInbox()
    {
        await using var fixture = await CreateFixtureAsync();
        var alice = await fixture.CreatePlayerAsync("alice", default);

        var fullBag = Enumerable.Range(0, InventoryLimits.MaxInventorySlotsPerBag)
            .Select(i => new InventorySlotRecord(alice, InventoryBag.CharacterBag, 100 + i, 60_000 + i, 1, 0)
            {
                Lifetime = InventoryLifetime.Permanent,
            })
            .ToArray();
        await fixture.ReplaceCharacterBagAsync(alice, fullBag, default);

        var entry = (await fixture.Store.GrantAsync(Grant(alice, 3001, 69999, 1000, 0), default)).Entry!;
        var failed = await fixture.Store.PickUpAsync(
            new CashInboxPickupCommand(
                "kr-812",
                alice,
                entry.CashSlotId,
                new PlayerWallet(alice, 100, 0),
                2000,
                [new ShopPurchaseGrant(69999, 1)],
                1100),
            default);

        Assert.Equal(CashInboxPickupOutcome.Failed, failed.Outcome);
        Assert.Equal(CashInboxEntryState.Active, (await fixture.Store.FindBySlotIdAsync("kr-812", entry.CashSlotId, default))?.State);
        Assert.Equal(InventoryLimits.MaxInventorySlotsPerBag, (await fixture.ReadCharacterBagAsync(alice, default)).Count);
    }

    private static CashInboxGrantCommand Grant(
        long userNum,
        int stockKey,
        int itemKey,
        long createdAt,
        long expiresAt,
        string profileId = "kr-812",
        int maxEntries = CashInboxLimits.DefaultMaxEntriesPerPlayer,
        string note = "",
        string? claimKey = null,
        bool ignoreInboxLimit = false) =>
        new(profileId, userNum, stockKey, itemKey, 1, 0, note, createdAt, expiresAt, maxEntries)
        {
            ClaimKey = claimKey,
            IgnoreInboxLimit = ignoreInboxLimit,
        };
}
