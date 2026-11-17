using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Tests;

/// <summary>
/// Shared persistence contract tests for <see cref="IUserHostPrizeStore"/>: atomic ticket and Lucci
/// escrow begin, idempotency, online/offline win settlement, draw/cancel refunds, startup escrow
/// recovery, and single-credit login claim flushes.
/// </summary>
public abstract class UserHostPrizeStoreContractTests
{
    private const int HostTicketItemId = 4100101;

    /// <summary>Isolated store fixture with helper methods for seeding players, wallets, and bags.</summary>
    protected interface IUserHostPrizeFixture : IAsyncDisposable
    {
        /// <summary>Gets the store under test.</summary>
        IUserHostPrizeStore Store { get; }

        /// <summary>Creates a player row and returns its assigned <c>user_num</c>.</summary>
        ValueTask<long> CreatePlayerAsync(string username, CancellationToken ct);

        /// <summary>Installs or updates the player's wallet balance.</summary>
        ValueTask SetWalletAsync(long userNum, int lucci, int cash, CancellationToken ct);

        /// <summary>Reads the player's current wallet snapshot.</summary>
        ValueTask<PlayerWallet?> ReadWalletAsync(long userNum, CancellationToken ct);

        /// <summary>Replaces the player's regular character bag for setup.</summary>
        ValueTask ReplaceCharacterBagAsync(long userNum, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct);

        /// <summary>Reads the player's regular character bag.</summary>
        ValueTask<IReadOnlyList<InventorySlotRecord>> ReadCharacterBagAsync(long userNum, CancellationToken ct);
    }

    /// <summary>Creates a fresh isolated fixture for one test method.</summary>
    protected abstract ValueTask<IUserHostPrizeFixture> CreateFixtureAsync();

    [Fact]
    public async Task BeginEscrowAtomicallyDeductsTicketAndLucciAndIsIdempotent()
    {
        await using var fixture = await CreateFixtureAsync();
        var host = await fixture.CreatePlayerAsync("sponsorhost", default);
        await fixture.SetWalletAsync(host, 50_000, 100, default);

        // Missing ticket: Lucci remains untouched.
        var missingTicket = await fixture.Store.BeginEscrowAsync(
            new UserHostEscrowBeginCommand("kr-812", "match-1", 10, host, 10_000, 10, HostTicketItemId, 1_000),
            default);
        Assert.Equal(UserHostEscrowBeginOutcome.MissingTicket, missingTicket.Outcome);
        Assert.Null(missingTicket.RemainingTickets);
        Assert.Equal(50_000, (await fixture.ReadWalletAsync(host, default))?.Lucci);

        // Expired ticket is rejected cleanly.
        await fixture.ReplaceCharacterBagAsync(host,
        [
            new InventorySlotRecord(host, InventoryBag.CharacterBag, 10, HostTicketItemId, 2, 0)
            {
                Lifetime = new InventoryLifetime(InventoryLifetimeKind.ExpiresAt, 900),
            },
        ], default);
        var expiredTicket = await fixture.Store.BeginEscrowAsync(
            new UserHostEscrowBeginCommand("kr-812", "match-1", 10, host, 10_000, 10, HostTicketItemId, 1_000),
            default);
        Assert.Equal(UserHostEscrowBeginOutcome.MissingTicket, expiredTicket.Outcome);
        Assert.Equal(50_000, (await fixture.ReadWalletAsync(host, default))?.Lucci);

        // Life ticket is rejected cleanly; active stacked permanent ticket succeeds.
        await fixture.ReplaceCharacterBagAsync(host,
        [
            new InventorySlotRecord(host, InventoryBag.CharacterBag, 10, HostTicketItemId, 3, 0)
            {
                Lifetime = InventoryLifetime.Permanent,
            },
        ], default);

        // Insufficient Lucci leaves ticket stack intact.
        var poor = await fixture.Store.BeginEscrowAsync(
            new UserHostEscrowBeginCommand("kr-812", "match-1", 10, host, 100_000, 10, HostTicketItemId, 1_000),
            default);
        Assert.Equal(UserHostEscrowBeginOutcome.InsufficientLucci, poor.Outcome);
        Assert.Equal(3, Assert.Single(await fixture.ReadCharacterBagAsync(host, default)).Quantity);

        // Unknown host reports HostMissing.
        var unknown = await fixture.Store.BeginEscrowAsync(
            new UserHostEscrowBeginCommand("kr-812", "match-unknown", 10, 999_999, 1_000, 10, HostTicketItemId, 1_000),
            default);
        Assert.Equal(UserHostEscrowBeginOutcome.HostMissing, unknown.Outcome);

        // Valid escrow deducts one ticket and exact Lucci prize atomically.
        var committed = await fixture.Store.BeginEscrowAsync(
            new UserHostEscrowBeginCommand("kr-812", "match-1", 10, host, 15_000, 10, HostTicketItemId, 1_000),
            default);
        Assert.Equal(UserHostEscrowBeginOutcome.Committed, committed.Outcome);
        Assert.Equal(2, committed.RemainingTickets);
        Assert.Equal(35_000, committed.HostLucciAfter);
        Assert.Equal(35_000, (await fixture.ReadWalletAsync(host, default))?.Lucci);
        Assert.Equal(2, Assert.Single(await fixture.ReadCharacterBagAsync(host, default)).Quantity);

        // Duplicate begin for the same (profile, matchId) is idempotent.
        var duplicate = await fixture.Store.BeginEscrowAsync(
            new UserHostEscrowBeginCommand("kr-812", "match-1", 10, host, 15_000, 10, HostTicketItemId, 1_001),
            default);
        Assert.Equal(UserHostEscrowBeginOutcome.AlreadyCommitted, duplicate.Outcome);
        Assert.Null(duplicate.RemainingTickets);
        Assert.Equal(35_000, (await fixture.ReadWalletAsync(host, default))?.Lucci);
        Assert.Equal(2, Assert.Single(await fixture.ReadCharacterBagAsync(host, default)).Quantity);

        // Consuming remaining quantity down to 0 removes the slot cleanly; another profile owns separate match ids.
        await fixture.SetWalletAsync(host, 20_000, 100, default);
        var match2 = await fixture.Store.BeginEscrowAsync(
            new UserHostEscrowBeginCommand("kr-1256", "match-1", 10, host, 5_000, 10, HostTicketItemId, 2_000),
            default);
        Assert.Equal(UserHostEscrowBeginOutcome.Committed, match2.Outcome);
        Assert.Equal(1, match2.RemainingTickets);

        var match3 = await fixture.Store.BeginEscrowAsync(
            new UserHostEscrowBeginCommand("kr-812", "match-last", 10, host, 5_000, 10, HostTicketItemId, 2_500),
            default);
        Assert.Equal(UserHostEscrowBeginOutcome.Committed, match3.Outcome);
        Assert.Equal(0, match3.RemainingTickets);
        Assert.Empty(await fixture.ReadCharacterBagAsync(host, default));
        Assert.Equal(10_000, (await fixture.ReadWalletAsync(host, default))?.Lucci);
    }

    [Fact]
    public async Task SettleCreditsOnlineImmediatelyQueuesOfflineClaimsAndIsIdempotent()
    {
        await using var fixture = await CreateFixtureAsync();
        var host = await fixture.CreatePlayerAsync("host", default);
        var onlineWinner = await fixture.CreatePlayerAsync("winner_on", default);
        var offlineWinner = await fixture.CreatePlayerAsync("winner_off", default);

        await fixture.SetWalletAsync(host, 50_000, 0, default);
        await fixture.SetWalletAsync(onlineWinner, 1_000, 0, default);
        await fixture.SetWalletAsync(offlineWinner, 2_000, 0, default);
        await fixture.ReplaceCharacterBagAsync(host,
        [
            new InventorySlotRecord(host, InventoryBag.CharacterBag, 1, HostTicketItemId, 1, 0)
            {
                Lifetime = InventoryLifetime.Permanent,
            },
        ], default);

        var begin = await fixture.Store.BeginEscrowAsync(
            new UserHostEscrowBeginCommand("kr-812", "match-win", 3, host, 10_000, 10, HostTicketItemId, 1_000),
            default);
        Assert.Equal(UserHostEscrowBeginOutcome.Committed, begin.Outcome);

        var missing = await fixture.Store.SettleEscrowAsync(
            new UserHostSettleCommand("kr-812", "nonexistent", false, [new(onlineWinner, 4_500)], [onlineWinner], 2_000),
            default);
        Assert.Equal(UserHostSettlementOutcome.NotFound, missing.Outcome);

        var settled = await fixture.Store.SettleEscrowAsync(
            new UserHostSettleCommand(
                "kr-812",
                "match-win",
                IsDrawOrCancelled: false,
                [new(onlineWinner, 4_500), new(offlineWinner, 4_500)],
                OnlineUserNums: [host, onlineWinner],
                SettledAtUnixMilliseconds: 3_000),
            default);
        Assert.Equal(UserHostSettlementOutcome.Applied, settled.Outcome);
        Assert.Equal(2, settled.Payouts.Count);

        var onPayout = Assert.Single(settled.Payouts, p => p.UserNum == onlineWinner);
        Assert.True(onPayout.CreditedImmediately);
        Assert.Equal(4_500, onPayout.AmountLucci);
        Assert.Equal(5_500, onPayout.WalletLucciAfter);
        Assert.Equal(5_500, (await fixture.ReadWalletAsync(onlineWinner, default))?.Lucci);

        var offPayout = Assert.Single(settled.Payouts, p => p.UserNum == offlineWinner);
        Assert.False(offPayout.CreditedImmediately);
        Assert.Null(offPayout.WalletLucciAfter);
        Assert.Equal(2_000, (await fixture.ReadWalletAsync(offlineWinner, default))?.Lucci);

        // Replaying settlement cannot pay twice.
        var replay = await fixture.Store.SettleEscrowAsync(
            new UserHostSettleCommand(
                "kr-812",
                "match-win",
                IsDrawOrCancelled: false,
                [new(onlineWinner, 4_500), new(offlineWinner, 4_500)],
                OnlineUserNums: [host, onlineWinner, offlineWinner],
                SettledAtUnixMilliseconds: 3_500),
            default);
        Assert.Equal(UserHostSettlementOutcome.AlreadyFinalized, replay.Outcome);
        Assert.Empty(replay.Payouts);

        // Offline winner receives pending prize atomically on login.
        var flush = await fixture.Store.FlushPendingClaimsAsync("kr-812", offlineWinner, 16, 4_000, default);
        Assert.Equal(1, flush.ClaimedCount);
        Assert.Equal(4_500L, flush.CreditedLucci);
        Assert.Equal(6_500, flush.WalletAfter?.Lucci);
        Assert.Equal(6_500, (await fixture.ReadWalletAsync(offlineWinner, default))?.Lucci);

        // Second login flush sees nothing left.
        var emptyFlush = await fixture.Store.FlushPendingClaimsAsync("kr-812", offlineWinner, 16, 4_500, default);
        Assert.Equal(0, emptyFlush.ClaimedCount);
        Assert.Equal(0L, emptyFlush.CreditedLucci);
        Assert.Null(emptyFlush.WalletAfter);
    }

    [Fact]
    public async Task DrawRefundsFullPrizeAndStartupRecoveryQueuesOfflineHostClaims()
    {
        await using var fixture = await CreateFixtureAsync();
        var hostOnline = await fixture.CreatePlayerAsync("host_on", default);
        var hostCrash = await fixture.CreatePlayerAsync("host_crash", default);

        await fixture.SetWalletAsync(hostOnline, 30_000, 0, default);
        await fixture.SetWalletAsync(hostCrash, 40_000, 0, default);
        await fixture.ReplaceCharacterBagAsync(hostOnline,
        [
            new InventorySlotRecord(hostOnline, InventoryBag.CharacterBag, 1, HostTicketItemId, 1, 0)
            {
                Lifetime = InventoryLifetime.Permanent,
            },
        ], default);
        await fixture.ReplaceCharacterBagAsync(hostCrash,
        [
            new InventorySlotRecord(hostCrash, InventoryBag.CharacterBag, 1, HostTicketItemId, 2, 0)
            {
                Lifetime = InventoryLifetime.Permanent,
            },
        ], default);

        Assert.Equal(UserHostEscrowBeginOutcome.Committed,
            (await fixture.Store.BeginEscrowAsync(new("kr-812", "draw-1", 5, hostOnline, 12_000, 10, HostTicketItemId, 1_000), default)).Outcome);
        Assert.Equal(18_000, (await fixture.ReadWalletAsync(hostOnline, default))?.Lucci);

        var refund = await fixture.Store.SettleEscrowAsync(
            new UserHostSettleCommand("kr-812", "draw-1", IsDrawOrCancelled: true, [], [hostOnline], 2_000),
            default);
        Assert.Equal(UserHostSettlementOutcome.Applied, refund.Outcome);
        var entry = Assert.Single(refund.Payouts);
        Assert.True(entry.CreditedImmediately);
        Assert.Equal(12_000, entry.AmountLucci);
        Assert.Equal(30_000, entry.WalletLucciAfter);
        Assert.Equal(30_000, (await fixture.ReadWalletAsync(hostOnline, default))?.Lucci);

        // Active escrows abandoned across restart are recovered only within their own profile.
        Assert.Equal(UserHostEscrowBeginOutcome.Committed,
            (await fixture.Store.BeginEscrowAsync(new("kr-812", "crash-a", 6, hostCrash, 7_000, 10, HostTicketItemId, 1_100), default)).Outcome);
        Assert.Equal(UserHostEscrowBeginOutcome.Committed,
            (await fixture.Store.BeginEscrowAsync(new("kr-1256", "crash-b", 7, hostCrash, 9_000, 10, HostTicketItemId, 1_200), default)).Outcome);
        Assert.Equal(24_000, (await fixture.ReadWalletAsync(hostCrash, default))?.Lucci);

        var recoveredKr812 = await fixture.Store.RecoverActiveEscrowsAsync("kr-812", "", 5_000, 16, default);
        Assert.Equal(1, recoveredKr812.RecoveredCount);
        Assert.Equal(7_000L, recoveredKr812.TotalRefundedLucci);

        // Second recovery pass for kr-812 finds zero left, while kr-1256 remains untouched until its own recovery.
        var recoveredAgain = await fixture.Store.RecoverActiveEscrowsAsync("kr-812", "", 5_100, 16, default);
        Assert.Equal(0, recoveredAgain.RecoveredCount);
        Assert.Equal(0L, recoveredAgain.TotalRefundedLucci);

        var flushKr812 = await fixture.Store.FlushPendingClaimsAsync("kr-812", hostCrash, 16, 6_000, default);
        Assert.Equal(1, flushKr812.ClaimedCount);
        Assert.Equal(7_000L, flushKr812.CreditedLucci);
        Assert.Equal(31_000, flushKr812.WalletAfter?.Lucci);

        // kr-1256 still settles or recovers independently.
        var recoveredKr1256 = await fixture.Store.RecoverActiveEscrowsAsync("kr-1256", "", 7_000, 16, default);
        Assert.Equal(1, recoveredKr1256.RecoveredCount);
        Assert.Equal(9_000L, recoveredKr1256.TotalRefundedLucci);

        var flushKr1256 = await fixture.Store.FlushPendingClaimsAsync("kr-1256", hostCrash, 16, 7_500, default);
        Assert.Equal(1, flushKr1256.ClaimedCount);
        Assert.Equal(9_000L, flushKr1256.CreditedLucci);
        Assert.Equal(40_000, flushKr1256.WalletAfter?.Lucci);
    }

    [Fact]
    public async Task RecoveryRefundsOnlyTheEscrowsOfItsOwnNode()
    {
        await using var fixture = await CreateFixtureAsync();
        var host = await fixture.CreatePlayerAsync("host_nodes", default);
        await fixture.SetWalletAsync(host, 30_000, 0, default);
        await fixture.ReplaceCharacterBagAsync(host,
        [
            new InventorySlotRecord(host, InventoryBag.CharacterBag, 1, HostTicketItemId, 2, 0),
        ], default);
        Assert.Equal(UserHostEscrowBeginOutcome.Committed, (await fixture.Store.BeginEscrowAsync(
            new("kr-812", "node-a-match", 1, host, 5_000, 10, HostTicketItemId, 1_000) { NodeId = "node-a" }, default)).Outcome);
        Assert.Equal(UserHostEscrowBeginOutcome.Committed, (await fixture.Store.BeginEscrowAsync(
            new("kr-812", "node-b-match", 2, host, 6_000, 10, HostTicketItemId, 1_100) { NodeId = "node-b" }, default)).Outcome);

        // node-a restarted: it refunds its own match, never node-b's, which may still be playing.
        var recovered = await fixture.Store.RecoverActiveEscrowsAsync("kr-812", "node-a", 2_000, 16, default);
        Assert.Equal(1, recovered.RecoveredCount);
        Assert.Equal(5_000L, recovered.TotalRefundedLucci);
        Assert.Equal(0, (await fixture.Store.RecoverActiveEscrowsAsync("kr-812", "", 2_100, 16, default)).RecoveredCount);

        // node-b's match still settles normally.
        var settled = await fixture.Store.SettleEscrowAsync(
            new UserHostSettleCommand("kr-812", "node-b-match", IsDrawOrCancelled: true, [], [], 3_000), default);
        Assert.Equal(UserHostSettlementOutcome.Applied, settled.Outcome);
    }
}
