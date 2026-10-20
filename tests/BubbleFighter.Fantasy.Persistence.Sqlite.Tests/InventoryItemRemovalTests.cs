using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>Checks targeted inventory removal against a real isolated SQLite file and transaction failures.</summary>
public sealed class InventoryItemRemovalTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    private static readonly InventoryBag[] Bags = [InventoryBag.CharacterBag, InventoryBag.EquippedBag];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-sqlite-removal-" + Guid.NewGuid().ToString("N"));
    private SqliteOptions Options => new() { DatabasePath = Path.Combine(_directory, "save.sqlite"),
        MaxPlayers = 4, MaxAccounts = 4, MaxDatabaseBytes = 1048576 };

    private static InventorySlotRecord Row(long user, InventoryBag bag, int item, InventoryLifetime lifetime) =>
        new(user, bag, item, item, 1, 0) { Lifetime = lifetime };

    /// <summary>Concurrent retries remove both mirrors once, preserve other owners and remain absent after restart.</summary>
    [Fact]
    public async Task ConcurrentRemovalSurvivesRestartWithoutChangingOtherItemsOrUsers()
    {
        long user, other;
        using (var database = new SqliteDatabase(Options))
        {
            var stores = new SqliteRepositories(database);
            user = (await stores.CreateAsync("one", "one", default))!.UserNum;
            other = (await stores.CreateAsync("two", "two", default))!.UserNum;
            foreach (var owner in new[] { user, other })
                foreach (var bag in Bags)
                    await stores.ReplaceSlotsAsync(owner, bag,
                        [Row(owner, bag, 202001, InventoryLifetime.Until(Now)),
                         Row(owner, bag, 202002, InventoryLifetime.Until(Now.AddDays(-1)))], default);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return await stores.RemoveExpiredItemAsync(user, 202001, Now, default);
            })).ToArray();
            start.SetResult();
            var results = await Task.WhenAll(requests);
            Assert.Equal(2, Assert.Single(results, result => result is { Count: > 0 })!.Count);
            Assert.Equal(7, results.Count(result => result is { Count: 0 }));
        }
        using var reopened = new SqliteDatabase(Options);
        var inventory = new SqliteRepositories(reopened);
        foreach (var bag in Bags)
        {
            Assert.Equal(202002, Assert.Single(await inventory.ListSlotsAsync(user, bag, 512, default)).ItemId);
            Assert.Equal(2, (await inventory.ListSlotsAsync(other, bag, 512, default)).Count);
        }
        Assert.Empty((await inventory.RemoveExpiredItemAsync(user, 202001, Now, default))!);
    }

    /// <summary>A renewed mirror rejects deletion, and absent or rejected requests do not rewrite the saved snapshot.</summary>
    [Fact]
    public async Task RenewedMirrorAndCancelledRequestsCannotPartiallyRemoveOrRewrite()
    {
        using var database = new SqliteDatabase(Options);
        var stores = new SqliteRepositories(database);
        var user = (await stores.CreateAsync("one", "one", default))!.UserNum;
        var expired = Row(user, InventoryBag.CharacterBag, 202001, InventoryLifetime.Until(Now));
        var active = Row(user, InventoryBag.EquippedBag, 202001, InventoryLifetime.Until(Now.AddMilliseconds(1)));
        await stores.ReplaceSlotsAsync(user, expired.Bag, [expired], default);
        await stores.ReplaceSlotsAsync(user, active.Bag, [active], default);
        database.RawExecute("CREATE TRIGGER block_inventory_write BEFORE UPDATE ON players BEGIN SELECT RAISE(ABORT, 'unexpected snapshot write'); END;");
        Assert.Null(await stores.RemoveExpiredItemAsync(user, 202001, Now, default));
        Assert.Empty((await stores.RemoveExpiredItemAsync(user, 202002, Now, default))!);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stores.RemoveExpiredItemAsync(user, 202001, Now, cancelled.Token).AsTask());
        Assert.Equal(expired, Assert.Single(await stores.ListSlotsAsync(user, expired.Bag, 512, default)));
        Assert.Equal(active, Assert.Single(await stores.ListSlotsAsync(user, active.Bag, 512, default)));
    }

    /// <summary>An aborted snapshot write rolls back both bags and leaves the file usable for a real retry.</summary>
    [Fact]
    public async Task FailedWriteRollsBackBothViewsAndTheRetryCommits()
    {
        using var database = new SqliteDatabase(Options);
        var stores = new SqliteRepositories(database);
        var user = (await stores.CreateAsync("one", "one", default))!.UserNum;
        foreach (var bag in Bags)
            await stores.ReplaceSlotsAsync(user, bag, [Row(user, bag, 202001, InventoryLifetime.Until(Now))], default);
        database.RawExecute("CREATE TRIGGER block_inventory_write BEFORE UPDATE ON players BEGIN SELECT RAISE(ABORT, 'reject removal'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => stores.RemoveExpiredItemAsync(user, 202001, Now, default).AsTask());
        foreach (var bag in Bags)
            Assert.Single(await stores.ListSlotsAsync(user, bag, 512, default));
        database.RawExecute("DROP TRIGGER block_inventory_write;");
        Assert.Equal(2, (await stores.RemoveExpiredItemAsync(user, 202001, Now, default))!.Count);
        foreach (var bag in Bags)
            Assert.Empty(await stores.ListSlotsAsync(user, bag, 512, default));
    }

    /// <summary>A concurrent renewal either remains owned or fails before charging after the discard commits.</summary>
    [Fact]
    public async Task RenewalAndRemovalShareTheSameWriteTransactionBoundary()
    {
        using var database = new SqliteDatabase(Options);
        var stores = new SqliteRepositories(database);
        var user = (await stores.CreateAsync("one", "one", default))!.UserNum;
        var expired = Row(user, InventoryBag.CharacterBag, 202001, InventoryLifetime.Until(Now));
        foreach (var bag in Bags)
            await stores.ReplaceSlotsAsync(user, bag, [expired with { Bag = bag }], default);
        var command = new ShopPurchaseCommand(new(user, 100, 0), 42, 0, "lucci", 10, 1000,
            [new(202001, 1) { Mode = ShopGrantMode.MatchItem, ExpectedSlot = expired,
                Lifetime = InventoryLifetime.Until(Now.AddDays(3)) }]);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewal = Task.Run(async () =>
        {
            await start.Task;
            try { return await stores.PurchaseAsync(command, default); }
            catch (ShopPurchaseConflictException) { return null; }
        });
        var removal = Task.Run(async () =>
        {
            await start.Task;
            return await stores.RemoveExpiredItemAsync(user, 202001, Now, default);
        });
        start.SetResult();
        await Task.WhenAll(renewal, removal);
        if (await renewal is { } committed)
        {
            Assert.Null(await removal);
            Assert.Equal(90, committed.Wallet.Lucci);
            Assert.Equal(Now.AddDays(3).ToUnixTimeMilliseconds(),
                Assert.Single(await stores.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default)).Lifetime.ExpiresAtUnixMilliseconds);
            Assert.Single(await stores.ListSlotsAsync(user, InventoryBag.EquippedBag, 512, default));
            Assert.Single(await stores.ListRecentAsync(user, 10, default));
        }
        else
        {
            Assert.Equal(2, (await removal)!.Count);
            foreach (var bag in Bags)
                Assert.Empty(await stores.ListSlotsAsync(user, bag, 512, default));
            Assert.Null(await ((BubbleFighter.Fantasy.Contracts.Wallet.IPlayerWalletRepository)stores)
                .FindByUserNumAsync(user, default));
            Assert.Empty(await stores.ListRecentAsync(user, 10, default));
        }
    }

    /// <summary>Releases only this fixture's temporary directory after its owned connections have closed.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
