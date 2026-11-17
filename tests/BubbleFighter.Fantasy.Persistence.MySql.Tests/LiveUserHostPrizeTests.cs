using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Wallet;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed partial class LiveDatabaseTests
{
    private const int LiveUserHostTicketItemId = 4100101;

    /// <summary>Concurrent escrow begins against the same match id commit at most once and consume one ticket.</summary>
    [Fact]
    public async Task UserHostBeginEscrowSerializesConcurrentStartsPerMatch()
    {
        if (await FactoryAsync() is not { } factory) return;
        var host = await NewPlayerAsync();
        var wallets = new MySqlPlayerWalletRepository(factory);
        var inventory = new MySqlInventoryRepository(factory);
        await wallets.UpsertAsync(new PlayerWallet(host, 20_000, 0), default);
        await inventory.ReplaceSlotsAsync(host, InventoryBag.CharacterBag,
        [
            new InventorySlotRecord(host, InventoryBag.CharacterBag, 1, LiveUserHostTicketItemId, 2, 0)
            {
                Lifetime = InventoryLifetime.Permanent,
            },
        ], default);

        var matchId = "live-race-" + Guid.NewGuid().ToString("N");
        var tasks = Enumerable.Range(0, 4)
            .Select(_ => new MySqlUserHostPrizeStore(factory).BeginEscrowAsync(
                new UserHostEscrowBeginCommand("kr-812", matchId, 11, host, 15_000, 10, LiveUserHostTicketItemId, 1_000),
                default).AsTask())
            .ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.Single(results, r => r.Outcome == UserHostEscrowBeginOutcome.Committed);
        Assert.Equal(3, results.Count(r => r.Outcome == UserHostEscrowBeginOutcome.AlreadyCommitted));
        Assert.Equal(5_000, (await wallets.FindByUserNumAsync(host, default))?.Lucci);
        Assert.Equal(1, Assert.Single(await inventory.ListSlotsAsync(host, InventoryBag.CharacterBag, 16, default)).Quantity);
    }

    /// <summary>Injected insert failure on user_host_escrows rolls back host ticket consumption and Lucci debit.</summary>
    [Fact]
    public async Task UserHostBeginEscrowRollbackLeavesTicketAndWalletUntouched()
    {
        if (await FactoryAsync() is not { } factory) return;
        var host = await NewPlayerAsync();
        var wallets = new MySqlPlayerWalletRepository(factory);
        var inventory = new MySqlInventoryRepository(factory);
        await wallets.UpsertAsync(new PlayerWallet(host, 25_000, 50), default);
        await inventory.ReplaceSlotsAsync(host, InventoryBag.CharacterBag,
        [
            new InventorySlotRecord(host, InventoryBag.CharacterBag, 1, LiveUserHostTicketItemId, 1, 0)
            {
                Lifetime = InventoryLifetime.Permanent,
            },
        ], default);

        var triggerName = "test_uh_escrow_" + Guid.NewGuid().ToString("N");
        await using var raw = await RawAsync();
        await using (var create = raw.CreateCommand())
        {
            create.CommandText = $"CREATE TRIGGER {triggerName} BEFORE INSERT ON user_host_escrows FOR EACH ROW BEGIN IF NEW.host_user_num={host} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='injected escrow rollback'; END IF; END";
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            var store = new MySqlUserHostPrizeStore(factory);
            var matchId = "live-rb-" + Guid.NewGuid().ToString("N");
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await store.BeginEscrowAsync(
                    new UserHostEscrowBeginCommand("kr-812", matchId, 12, host, 10_000, 10, LiveUserHostTicketItemId, 1_000),
                    default));

            Assert.Equal(25_000, (await wallets.FindByUserNumAsync(host, default))?.Lucci);
            Assert.Equal(1, Assert.Single(await inventory.ListSlotsAsync(host, InventoryBag.CharacterBag, 16, default)).Quantity);
        }
        finally
        {
            await using var drop = raw.CreateCommand();
            drop.CommandText = $"DROP TRIGGER {triggerName}";
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Credits online winners immediately, queues offline claims, flushes once on login, and recovers stale escrows per profile.</summary>
    [Fact]
    public async Task UserHostSettleOfflineFlushAndStartupRecoveryPersistCleanly()
    {
        if (await FactoryAsync() is not { } factory) return;
        var host = await NewPlayerAsync();
        var onlineWinner = await NewPlayerAsync();
        var offlineWinner = await NewPlayerAsync();
        var wallets = new MySqlPlayerWalletRepository(factory);
        var inventory = new MySqlInventoryRepository(factory);
        await wallets.UpsertAsync(new PlayerWallet(host, 50_000, 0), default);
        await wallets.UpsertAsync(new PlayerWallet(onlineWinner, 1_000, 0), default);
        await wallets.UpsertAsync(new PlayerWallet(offlineWinner, 2_000, 0), default);
        await inventory.ReplaceSlotsAsync(host, InventoryBag.CharacterBag,
        [
            new InventorySlotRecord(host, InventoryBag.CharacterBag, 1, LiveUserHostTicketItemId, 2, 0)
            {
                Lifetime = InventoryLifetime.Permanent,
            },
        ], default);

        var store = new MySqlUserHostPrizeStore(factory);
        var winMatchId = "live-win-" + Guid.NewGuid().ToString("N");
        var beginWin = await store.BeginEscrowAsync(
            new UserHostEscrowBeginCommand("kr-812", winMatchId, 13, host, 10_000, 10, LiveUserHostTicketItemId, 1_000),
            default);
        Assert.Equal(UserHostEscrowBeginOutcome.Committed, beginWin.Outcome);

        var settle = await store.SettleEscrowAsync(
            new UserHostSettleCommand("kr-812", winMatchId, false, [new(onlineWinner, 4_500), new(offlineWinner, 4_500)], [onlineWinner], 2_000),
            default);
        Assert.Equal(UserHostSettlementOutcome.Applied, settle.Outcome);
        Assert.Equal(5_500, (await wallets.FindByUserNumAsync(onlineWinner, default))?.Lucci);
        Assert.Equal(2_000, (await wallets.FindByUserNumAsync(offlineWinner, default))?.Lucci);

        var flush = await store.FlushPendingClaimsAsync("kr-812", offlineWinner, 16, 3_000, default);
        Assert.Equal(1, flush.ClaimedCount);
        Assert.Equal(6_500, flush.WalletAfter?.Lucci);
        Assert.Equal(6_500, (await wallets.FindByUserNumAsync(offlineWinner, default))?.Lucci);

        var replayFlush = await store.FlushPendingClaimsAsync("kr-812", offlineWinner, 16, 3_100, default);
        Assert.Equal(0, replayFlush.ClaimedCount);

        var staleProfile = "live-p-" + Guid.NewGuid().ToString("N")[..12];
        var staleMatchId = "live-rec-" + Guid.NewGuid().ToString("N");
        var beginStale = await store.BeginEscrowAsync(
            new UserHostEscrowBeginCommand(staleProfile, staleMatchId, 14, host, 8_000, 10, LiveUserHostTicketItemId, 4_000),
            default);
        Assert.Equal(UserHostEscrowBeginOutcome.Committed, beginStale.Outcome);

        var recovered = await store.RecoverActiveEscrowsAsync(staleProfile, "", 5_000, 16, default);
        Assert.Equal(1, recovered.RecoveredCount);
        Assert.Equal(8_000L, recovered.TotalRefundedLucci);

        var hostFlush = await store.FlushPendingClaimsAsync(staleProfile, host, 16, 6_000, default);
        Assert.Equal(1, hostFlush.ClaimedCount);
        Assert.Equal(40_000, hostFlush.WalletAfter?.Lucci);
    }
}
