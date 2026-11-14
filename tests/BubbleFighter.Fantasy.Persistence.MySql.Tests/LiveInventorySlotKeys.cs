using System.Data.Common;
using System.Globalization;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using MySqlConnector;
using SqlSugar;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

public sealed partial class LiveDatabaseTests
{
    /// <summary>Older-node purchase history raises the allocation floor while the new column remains the compare-and-update precondition.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderNodePurchaseHistoryRemainsAnAllocationFloor(bool purchase)
    {
        if (await FactoryAsync() is not { } factory) return;
        var setup = await SeedInventoryKeyMileageAsync(factory);
        await InsertOlderInventoryKeyPurchaseAsync(setup.User, 19000);
        Assert.Equal(0, await ScalarAsync<long>("SELECT last_inventory_slot_key FROM players WHERE user_num=@u", ("@u", setup.User)));
        if (purchase)
        {
            var result = Assert.IsType<ShopPurchaseResult>(await new MySqlShopPurchaseStore(factory)
                .PurchaseAsync(InventoryKeyPurchase(setup.User), default));
            Assert.Equal(19001, Assert.Single(result.Grants).SlotKey);
        }
        else
        {
            var result = Assert.IsType<PetMutationResult>(await setup.Store.MutatePetWithResultAsync(
                InventoryKeyMileageCommand(setup.Snapshot, setup.Initialization), default));
            Assert.Equal(19001, Assert.Single(result.Snapshot.CharacterBag, row => row.ItemId == 200777).SlotKey);
        }
        Assert.Equal(19001, await ScalarAsync<long>("SELECT last_inventory_slot_key FROM players WHERE user_num=@u", ("@u", setup.User)));
        Assert.Equal(purchase ? 2 : 1, await ScalarAsync<int>("SELECT COUNT(*) FROM purchase_log WHERE user_num=@u", ("@u", setup.User)));
    }

    /// <summary>Locking reads observe a historical grant committed after this transaction established a consistent snapshot.</summary>
    [Fact]
    public async Task AllocationFloorUsesCurrentPurchaseHistoryUnderRepeatableRead()
    {
        if (await FactoryAsync() is not { } factory) return;
        var setup = await SeedInventoryKeyMileageAsync(factory);
        await using var session = await SugarSession.OpenAsync(factory, default);
        await using var transaction = await session.BeginAsync();
        Assert.Empty(await session.Db.Queryable<PurchaseLogRow>().Where(row => row.UserNum == setup.User).ToListAsync());
        await InsertOlderInventoryKeyPurchaseAsync(setup.User, 19000);
        await MySqlPlayerLock.AcquireAsync(session.Db, setup.User, default);
        var keys = await MySqlInventorySlotKeys.ReadAsync(session.Db, setup.User, default);
        Assert.Equal(0, keys.Stored);
        Assert.Equal(19000, keys.Highest);
    }

    /// <summary>A mileage reward's key remains retired after consumption and a repository reopen.</summary>
    [Fact]
    public async Task MileageRegrantAllocatesBeyondAConsumedRewardWithoutPurchaseAudit()
    {
        if (await FactoryAsync() is not { } factory) return;
        var setup = await SeedInventoryKeyMileageAsync(factory);
        var first = Assert.IsType<PetMutationResult>(await setup.Store.MutatePetWithResultAsync(
            InventoryKeyMileageCommand(setup.Snapshot, setup.Initialization), default));
        var firstReward = Assert.Single(first.Snapshot.CharacterBag, row => row.ItemId == 200777);
        Assert.Equal(9001, firstReward.SlotKey);
        Assert.Equal(0, await setup.Store.ConsumeItemAsync(setup.User, 200777, default));
        var reopened = new MySqlInventoryRepository(factory);
        var current = await reopened.ReadPetInventoryAsync(setup.User, setup.Initialization.Scope, default);
        Assert.DoesNotContain(current.CharacterBag, row => row.ItemId == 200777);
        var second = Assert.IsType<PetMutationResult>(await reopened.MutatePetWithResultAsync(
            InventoryKeyMileageCommand(current, setup.Initialization), default));
        Assert.Equal(9002, Assert.Single(second.Snapshot.CharacterBag, row => row.ItemId == 200777).SlotKey);
        Assert.Equal(9002, await ScalarAsync<long>("SELECT last_inventory_slot_key FROM players WHERE user_num=@u", ("@u", setup.User)));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM purchase_log WHERE user_num=@u", ("@u", setup.User)));
    }

    /// <summary>Shop grants share the mileage watermark even when the mileage row no longer exists.</summary>
    [Fact]
    public async Task PurchaseAfterConsumedMileageRewardUsesTheSharedWatermark()
    {
        if (await FactoryAsync() is not { } factory) return;
        var setup = await SeedInventoryKeyMileageAsync(factory);
        Assert.NotNull(await setup.Store.MutatePetWithResultAsync(
            InventoryKeyMileageCommand(setup.Snapshot, setup.Initialization), default));
        Assert.Equal(0, await setup.Store.ConsumeItemAsync(setup.User, 200777, default));
        var result = Assert.IsType<ShopPurchaseResult>(await new MySqlShopPurchaseStore(factory)
            .PurchaseAsync(InventoryKeyPurchase(setup.User), default));
        Assert.Equal(9002, Assert.Single(result.Grants).SlotKey);
        Assert.Equal(99, result.Wallet.Lucci);
        Assert.Equal(9002, await ScalarAsync<long>("SELECT last_inventory_slot_key FROM players WHERE user_num=@u", ("@u", setup.User)));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM purchase_log WHERE user_num=@u", ("@u", setup.User)));
        Assert.Equal(9002, await ScalarAsync<long>("SELECT last_slot_key FROM purchase_log WHERE user_num=@u", ("@u", setup.User)));
    }

    /// <summary>Cancellation during the watermark update rolls back preceding inventory, wallet and audit writes too.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAtWatermarkUpdateRollsBackTheWholeGrant(bool purchase)
    {
        if (await FactoryAsync() is not { } factory) return;
        var setup = await SeedInventoryKeyMileageAsync(factory);
        var keyBefore = await ScalarAsync<long>("SELECT last_inventory_slot_key FROM players WHERE user_num=@u", ("@u", setup.User));
        var suffix = Guid.NewGuid().ToString("N");
        var trigger = "bff_key_" + suffix;
        var lockName = "bff_key_wait_" + suffix;
        await using var holder = await RawAsync();
        await using (var hold = new MySqlCommand("SELECT GET_LOCK(@name, 5)", holder))
        {
            hold.Parameters.AddWithValue("@name", lockName);
            Assert.Equal(1, Convert.ToInt32(await hold.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }
        // The user-scoped trigger waits after the watermark statement has started, following the inventory writes.
        var create = string.Create(CultureInfo.InvariantCulture, $"""
            CREATE TRIGGER {trigger} AFTER UPDATE ON players FOR EACH ROW
            BEGIN
                IF NEW.user_num = {setup.User} AND NEW.last_inventory_slot_key > OLD.last_inventory_slot_key THEN
                    DO GET_LOCK('{lockName}', 20);
                END IF;
            END
            """);
        await ExecuteInventoryKeySqlAsync(create);
        var recording = new InventoryKeyConnectionFactory(factory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task? operation = null;
        try
        {
            operation = purchase
                ? new MySqlShopPurchaseStore(recording).PurchaseAsync(InventoryKeyPurchase(setup.User), cancellation.Token).AsTask()
                : new MySqlInventoryRepository(recording).MutatePetWithResultAsync(
                    InventoryKeyMileageCommand(setup.Snapshot, setup.Initialization), cancellation.Token).AsTask();
            var connectionId = await recording.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var state = await ScalarAsync<string>("SELECT STATE FROM information_schema.PROCESSLIST WHERE ID=@id", ("@id", connectionId));
                if (state?.Contains("User lock", StringComparison.OrdinalIgnoreCase) == true) break;
                Assert.False(operation.IsCompleted, "The grant must reach its staged watermark update before cancellation.");
                await Task.Delay(20, wait.Token);
            }
            await cancellation.CancelAsync();
            var failure = await Record.ExceptionAsync(() => operation);
            Assert.NotNull(failure);
            Assert.True(failure is OperationCanceledException or MySqlException or SqlSugarException,
                $"Unexpected cancellation failure: {failure.GetType().Name}");
        }
        finally
        {
            await cancellation.CancelAsync();
            await using (var release = new MySqlCommand("SELECT RELEASE_LOCK(@name)", holder))
            {
                release.Parameters.AddWithValue("@name", lockName);
                _ = await release.ExecuteScalarAsync();
            }
            if (operation is not null)
            {
                try { await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) { }
            }
            await ExecuteInventoryKeySqlAsync("DROP TRIGGER " + trigger);
        }
        var after = await setup.Store.ReadPetInventoryAsync(setup.User, setup.Initialization.Scope, default);
        Assert.Equal(setup.Snapshot.CharacterBag, after.CharacterBag);
        Assert.Equal(setup.Snapshot.EquippedBag, after.EquippedBag);
        Assert.Equal(keyBefore, await ScalarAsync<long>("SELECT last_inventory_slot_key FROM players WHERE user_num=@u", ("@u", setup.User)));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM purchase_log WHERE user_num=@u", ("@u", setup.User)));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM player_wallets WHERE user_num=@u", ("@u", setup.User)));
    }

    private static async Task<(long User, MySqlInventoryRepository Store, PetInventorySnapshot Snapshot,
        PetInitializationCommand Initialization)> SeedInventoryKeyMileageAsync(MySqlConnectionFactory factory)
    {
        var user = await NewPlayerAsync();
        var store = new MySqlInventoryRepository(factory);
        var now = DateTimeOffset.UtcNow;
        InventoryLoadoutScope scope = new("kr-1256", [301001, 100643, 103385, 200777, 200888]);
        InventorySlotRecord[] rows = [new(user, InventoryBag.CharacterBag, 1, 301001, 1, 1),
            new(user, InventoryBag.CharacterBag, 2, 100643, 3, 0),
            new(user, InventoryBag.CharacterBag, 9000, 103385, 0, 0) { Lifetime = InventoryLifetime.Permanent }];
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag,
            rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        PetInitializationCommand initialization = new(user, scope, [301001], new(0, 0, now.AddHours(1), null, null, null), now);
        return (user, store, await store.InitializePetsAsync(initialization, default), initialization);
    }

    private static PetMutationCommand InventoryKeyMileageCommand(PetInventorySnapshot snapshot, PetInitializationCommand initialization)
    {
        var pet = snapshot.CharacterBag.Single(row => row.ItemId == 301001);
        var progress = pet.Pet!.Find(initialization.Scope.Key)!;
        return new(pet, initialization.Scope, progress with { Experience = progress.Experience + 1 }, 100643, true, initialization.Now)
        {
            Initialization = initialization,
            Mileage = new(103385, snapshot.CharacterBag.Single(row => row.ItemId == 103385), 1, 100, 1,
                new(1, 1, 1, false, 0, [new(200777, 1) { Mode = ShopGrantMode.MatchItem, Lifetime = InventoryLifetime.Permanent }])),
        };
    }

    private static ShopPurchaseCommand InventoryKeyPurchase(long user) => new(new(user, 100, 0), 77, 0, "lucci", 1, 1,
        [new(200888, 1) { Mode = ShopGrantMode.MatchItem, Lifetime = InventoryLifetime.Permanent }]);

    private static async Task ExecuteInventoryKeySqlAsync(string sql)
    {
        await using var db = await RawAsync();
        await using var command = new MySqlCommand(sql, db);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertOlderInventoryKeyPurchaseAsync(long user, long lastKey)
    {
        await using var db = await RawAsync();
        await using var command = new MySqlCommand("""
            INSERT INTO purchase_log(user_num, stock_set_id, item_id, money_type, price, slot_key, purchase_key, last_slot_key)
            VALUES(@user, 0, 200999, 'lucci', 1, @key, 79, @key)
            """, db);
        command.Parameters.AddWithValue("@user", user);
        command.Parameters.AddWithValue("@key", lastKey);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private sealed class InventoryKeyConnectionFactory(IDbConnectionFactory inner) : IDbConnectionFactory
    {
        internal TaskCompletionSource<int> Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<DbConnection> OpenAsync(CancellationToken ct)
        {
            var connection = await inner.OpenAsync(ct);
            Opened.TrySetResult(((MySqlConnection)connection).ServerThread);
            return connection;
        }
    }
}
