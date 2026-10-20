using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Persistence.MySql;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

// Runs the repositories against a real server. Every test returns early unless BFF_MYSQL_VERIFY_CONNECTION
// names a scratch database (never a live one): the suite migrates it and writes rows with unique player ids.
public sealed class LiveDatabaseTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("BFF_MYSQL_VERIFY_CONNECTION");
    private static readonly Lazy<Task<MySqlConnectionFactory>> Ready = new(PrepareAsync);
    private static readonly InventoryWearGroup[] WearGroups = [new(1000, 1999, 1000), new(2000, 2999, 2000)];

    private static async Task<MySqlConnectionFactory> PrepareAsync()
    {
        var factory = new MySqlConnectionFactory(ConnectionString!);
        await new MigrationRunner(factory, NullLogger.Instance, typeof(MySqlPlugin).Assembly).ApplyAsync(default);
        return factory;
    }

    private static async Task<MySqlConnectionFactory?> FactoryAsync() =>
        ConnectionString is null ? null : await Ready.Value;

    private static async Task<MySqlConnection> RawAsync()
    {
        var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args)
    {
        await using var db = await RawAsync();
        await using var command = new MySqlCommand(sql, db);
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value);
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default! : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private static async Task<long> NewPlayerAsync()
    {
        var name = "lv" + Guid.NewGuid().ToString("N")[..20];
        await using var db = await RawAsync();
        await using var command = new MySqlCommand(
            "INSERT INTO players (username, nickname, level, created_at) VALUES (@n, @n, 1, UTC_TIMESTAMP()); SELECT LAST_INSERT_ID();", db);
        command.Parameters.AddWithValue("@n", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static Task<long> CountAsync(string table, long userNum) =>
        ScalarAsync<long>($"SELECT COUNT(*) FROM {table} WHERE user_num = @u", ("@u", userNum));

    private static async Task<int[]> IntListAsync(string sql, long user)
    {
        var values = new List<int>();
        await using var db = await RawAsync();
        await using var command = new MySqlCommand(sql, db);
        command.Parameters.AddWithValue("@u", user);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(Convert.ToInt32(reader.GetValue(0)));
        return [.. values];
    }

    private static T[] Arr<T>(params T[] values) => values;

    private static InventorySlotRecord Slot(long user, int item, byte flags = 0, int quantity = 1, InventoryBag bag = InventoryBag.CharacterBag) =>
        new(user, bag, 500 + item, item, quantity, flags) { Lifetime = InventoryLifetime.Permanent };

    private static ShopPurchaseCommand Purchase(long user, int key, int item, int price, int lucci = 100, int cash = 0, string money = "lucci") =>
        new(new PlayerWallet(user, lucci, cash), key, 7, money, price, 10_000, [new ShopPurchaseGrant(item, 1) { Lifetime = InventoryLifetime.Permanent }]);

    [Fact]
    public async Task MigrationsApplyFromEmptyAndAreIdempotent()
    {
        if (await FactoryAsync() is not { } factory) return;
        var runner = new MigrationRunner(factory, NullLogger.Instance, typeof(MySqlPlugin).Assembly);
        await runner.ApplyAsync(default);
        var expected = runner.DiscoverMigrations().Count;
        Assert.Equal(expected, await ScalarAsync<long>("SELECT COUNT(*) FROM schema_migrations"));
        foreach (var table in new[] { "players", "fighters", "inventory_slots", "shop_sessions", "clans", "purchase_log", "player_wallets" })
            Assert.Equal(1L, await ScalarAsync<long>(
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @t", ("@t", table)));
        Assert.Equal(0L, await ScalarAsync<long>(
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'player_money'"));
    }

    [Fact]
    public async Task PlayerCreationYieldsDefaultsAndUniqueIds()
    {
        if (await FactoryAsync() is not { } factory) return;
        var a = await NewPlayerAsync();
        var b = await NewPlayerAsync();
        Assert.NotEqual(a, b);
        Assert.Equal(1, await ScalarAsync<int>("SELECT level FROM players WHERE user_num = @u", ("@u", a)));
        Assert.Equal(0, await ScalarAsync<int>("SELECT active_weapon_preset_slot FROM players WHERE user_num = @u", ("@u", a)));
        var progress = new MySqlPlayerProgressRepository(factory);
        Assert.Equal(new PlayerProgress(a, 1, 0), await progress.FindAsync(a, default));
        Assert.Null(await progress.FindAsync(long.MaxValue - 1, default));
    }

    [Fact]
    public async Task LauncherEnrollmentPreservesLegacyIdentityAndSerializesClaims()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var name = await ScalarAsync<string>("SELECT username FROM players WHERE user_num = @u", ("@u", user));
        var accounts = new MySqlLauncherAccounts(factory);
        var before = await accounts.FindAsync(name, default);
        Assert.NotNull(before); Assert.Equal(user, before.UserNumber); Assert.Equal("", before.PasswordHash);
        var results = await Task.WhenAll(accounts.TrySetInitialPasswordAsync(name, "first", default).AsTask(), accounts.TrySetInitialPasswordAsync(name, "second", default).AsTask());
        Assert.Single(results, result => result);
        var after = await accounts.FindAsync(name, default);
        Assert.NotNull(after); Assert.Equal(before.UserNumber, after.UserNumber); Assert.Equal(before.Nickname, after.Nickname);
        Assert.False(await accounts.TrySetInitialPasswordAsync(name, "replacement", default));
        Assert.Equal(after.PasswordHash, (await accounts.FindAsync(name, default))!.PasswordHash);
        var absent = "absent_" + Guid.NewGuid().ToString("N")[..20];
        Assert.False(await accounts.TrySetInitialPasswordAsync(absent, "password", default));
        Assert.Null(await accounts.FindAsync(absent, default));

        var emptyName = "empty_" + Guid.NewGuid().ToString("N")[..20];
        Assert.True(await accounts.RegisterAsync(emptyName, "", default));
        Assert.True(await accounts.TrySetInitialPasswordAsync(emptyName, "enrolled", default));
        var bannedName = "banned_" + Guid.NewGuid().ToString("N")[..20];
        Assert.True(await accounts.RegisterAsync(bannedName, "", default));
        await ScalarAsync<long>("UPDATE accounts SET banned_until = DATE_ADD(UTC_TIMESTAMP(), INTERVAL 1 DAY) WHERE username = @n", ("@n", bannedName));
        Assert.False(await accounts.TrySetInitialPasswordAsync(bannedName, "enrolled", default));
        Assert.Equal("", (await accounts.FindAsync(bannedName, default))!.PasswordHash);
    }

    [Fact]
    public async Task WalletReadUpsertCreditAndNegativeRefusal()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var wallets = new MySqlPlayerWalletRepository(factory);
        Assert.Null(await wallets.FindByUserNumAsync(user, default));
        await wallets.UpsertAsync(new PlayerWallet(user, 50, 7), default);
        Assert.Equal(new PlayerWallet(user, 50, 7), await wallets.FindByUserNumAsync(user, default));
        await wallets.UpsertAsync(new PlayerWallet(user, 60, 9), default);
        Assert.Equal(new PlayerWallet(user, 60, 9), await wallets.FindByUserNumAsync(user, default));
        Assert.Equal(1L, await CountAsync("player_wallets", user));

        var progress = new MySqlPlayerProgressRepository(factory);
        var settled = await progress.SettleRoundAsync(new("m-live", user, 25, _ => new PlayerProgress(user, 3, 120)), default);
        Assert.Equal((RoundSettlementOutcome.Settled, 85), (settled.Outcome, settled.LucciBalance));
        var repeated = await progress.SettleRoundAsync(new("m-live", user, 25, _ => new PlayerProgress(user, 9, 999)), default);
        Assert.Equal((RoundSettlementOutcome.AlreadySettled, 85, new PlayerProgress(user, 3, 120)),
            (repeated.Outcome, repeated.LucciBalance, repeated.After));
        Assert.Equal(new PlayerWallet(user, 85, 9), await wallets.FindByUserNumAsync(user, default));
        Assert.Equal(new PlayerProgress(user, 3, 120), await progress.FindAsync(user, default));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => wallets.UpsertAsync(new PlayerWallet(user, -1, 0), default).AsTask());
        Assert.Equal(new PlayerWallet(user, 85, 9), await wallets.FindByUserNumAsync(user, default));
    }

    [Fact]
    public async Task InsufficientDebitLeavesNothingWritten()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var store = new MySqlShopPurchaseStore(factory);
        Assert.Null(await store.PurchaseAsync(Purchase(user, 1, 4001, price: 500, lucci: 100), default));
        Assert.Equal(0L, await CountAsync("inventory_slots", user));
        Assert.Equal(0L, await CountAsync("purchase_log", user));
        // A refused purchase rolls back everything, including the seed wallet row it would have created.
        Assert.Null(await new MySqlPlayerWalletRepository(factory).FindByUserNumAsync(user, default));
        Assert.Equal(0L, await CountAsync("player_wallets", user));

        await new MySqlPlayerWalletRepository(factory).UpsertAsync(new PlayerWallet(user, 100, 0), default);
        Assert.Null(await store.PurchaseAsync(Purchase(user, 2, 4001, price: 1, lucci: 100, cash: 0, money: "cash"), default));
        Assert.Equal(0L, await CountAsync("purchase_log", user));
        Assert.Equal(new PlayerWallet(user, 100, 0), await new MySqlPlayerWalletRepository(factory).FindByUserNumAsync(user, default));
    }

    [Fact]
    public async Task PurchaseWritesBagSlotLogAndCharge()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var store = new MySqlShopPurchaseStore(factory);
        var result = await store.PurchaseAsync(Purchase(user, 11, 4002, price: 30), default);
        Assert.NotNull(result);
        Assert.Equal(new PlayerWallet(user, 70, 0), result.Wallet);
        var grant = Assert.Single(result.Grants);
        Assert.Equal(4002, grant.ItemId);
        Assert.Equal(10_000, grant.SlotKey);

        var bag = await new MySqlInventoryRepository(factory).ListSlotsAsync(user, InventoryBag.CharacterBag, 10, default);
        Assert.Equal([grant], bag);
        Assert.Equal(new PlayerWallet(user, 70, 0), await new MySqlPlayerWalletRepository(factory).FindByUserNumAsync(user, default));
        var log = Assert.Single(await store.ListRecentAsync(user, 10, default));
        Assert.Equal(new ShopPurchaseRecord(user, 7, 4002, "lucci", 30, 10_000, 11), log);

        var second = await store.PurchaseAsync(Purchase(user, 12, 4003, price: 20, lucci: 0), default);
        Assert.NotNull(second);
        Assert.Equal(new PlayerWallet(user, 50, 0), second.Wallet);
        Assert.Equal(10_001, Assert.Single(second.Grants).SlotKey);
        Assert.Equal(2L, await CountAsync("inventory_slots", user));
        Assert.Equal(Arr(12, 11), (await store.ListRecentAsync(user, 10, default)).Select(r => r.PurchaseKey).ToArray());
    }

    [Fact]
    public async Task PurchaseForMissingPlayerFailsAndWritesNothing()
    {
        if (await FactoryAsync() is not { } factory) return;
        var ghost = 9_000_000_000L + Random.Shared.NextInt64(1_000_000);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MySqlShopPurchaseStore(factory).PurchaseAsync(Purchase(ghost, 1, 4001, 10), default).AsTask());
        Assert.Equal(0L, await CountAsync("player_wallets", ghost));
    }

    [Fact]
    public async Task ConcurrentDoublePurchaseChargesOnlyOnce()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var store = new MySqlShopPurchaseStore(factory);
        var results = await Task.WhenAll(
            Task.Run(() => store.PurchaseAsync(Purchase(user, 21, 4101, price: 80), default).AsTask()),
            Task.Run(() => store.PurchaseAsync(Purchase(user, 22, 4102, price: 80), default).AsTask()));
        Assert.Equal(1, results.Count(r => r is not null));
        Assert.Equal(new PlayerWallet(user, 20, 0), await new MySqlPlayerWalletRepository(factory).FindByUserNumAsync(user, default));
        Assert.Equal(1L, await CountAsync("purchase_log", user));
        Assert.Equal(1L, await CountAsync("inventory_slots", user));
    }

    [Fact]
    public async Task InventoryReplaceThenAppendKeepsRowCounts()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var inventory = new MySqlInventoryRepository(factory);
        await inventory.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [Slot(user, 1000), Slot(user, 1001), Slot(user, 2000)], default);
        await inventory.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, [Slot(user, 1000, 1, bag: InventoryBag.EquippedBag)], default);
        Assert.Equal(3L, await ScalarAsync<long>("SELECT COUNT(*) FROM inventory_slots WHERE user_num = @u AND bag = 0", ("@u", user)));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM inventory_slots WHERE user_num = @u AND bag = 1", ("@u", user)));

        await inventory.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [Slot(user, 1000), Slot(user, 3000)], default);
        var listed = await inventory.ListSlotsAsync(user, InventoryBag.CharacterBag, 50, default);
        Assert.Equal(Arr(1000, 3000), listed.Select(s => s.ItemId).ToArray());
        Assert.All(listed, s => Assert.Equal(InventoryLifetimeKind.Permanent, s.Lifetime.Kind));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM inventory_slots WHERE user_num = @u AND bag = 1", ("@u", user)));

        var merged = await inventory.AppendMissingItemsAsync(user, InventoryBag.CharacterBag,
            [Slot(user, 3000), Slot(user, 4000), Slot(user, 4001)], default);
        Assert.Equal(Arr(1000, 3000, 4000, 4001), merged.Select(s => s.ItemId).ToArray());
        Assert.Equal(4L, await ScalarAsync<long>("SELECT COUNT(*) FROM inventory_slots WHERE user_num = @u AND bag = 0", ("@u", user)));
        Assert.Equal(Arr(0, 1, 2, 3), await IntListAsync("SELECT ordinal FROM inventory_slots WHERE user_num = @u AND bag = 0 ORDER BY ordinal", user));

        await inventory.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [], default);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM inventory_slots WHERE user_num = @u AND bag = 0", ("@u", user)));
    }

    [Fact]
    public async Task ExpiringLifetimeRoundTrips()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var inventory = new MySqlInventoryRepository(factory);
        var deadline = new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero);
        var expiring = Slot(user, 1500) with { Lifetime = InventoryLifetime.Until(deadline) };
        await inventory.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [expiring, Slot(user, 1501)], default);
        var listed = await inventory.ListSlotsAsync(user, InventoryBag.CharacterBag, 10, default);
        Assert.Equal(expiring, listed[0]);
        Assert.Equal(deadline.ToUnixTimeMilliseconds(), listed[0].Lifetime.ExpiresAtUnixMilliseconds);
    }

    [Fact]
    public async Task SetSlotWornTogglesFlagAndReportsMissingItems()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var inventory = new MySqlInventoryRepository(factory);
        await inventory.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [Slot(user, 1000), Slot(user, 1001, flags: 4)], default);
        Assert.Equal(1, (await inventory.SetSlotWornAsync(user, 1000, true, default))!.Flags);
        Assert.Equal(1, (await inventory.SetSlotWornAsync(user, 1000, true, default))!.Flags);
        Assert.Equal(1, await ScalarAsync<int>("SELECT flags FROM inventory_slots WHERE user_num = @u AND item_id = 1000", ("@u", user)));
        Assert.Equal(5, (await inventory.SetSlotWornAsync(user, 1001, true, default))!.Flags);
        Assert.Equal(4, (await inventory.SetSlotWornAsync(user, 1001, false, default))!.Flags);
        Assert.Equal(4, await ScalarAsync<int>("SELECT flags FROM inventory_slots WHERE user_num = @u AND item_id = 1001", ("@u", user)));
        Assert.Null(await inventory.SetSlotWornAsync(user, 9999, true, default));
    }

    [Fact]
    public async Task WearSelectionUpdatesSeveralSlotsAtOnce()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var inventory = new MySqlInventoryRepository(factory);
        await inventory.ReplaceSlotsAsync(user, InventoryBag.CharacterBag,
            [Slot(user, 1000), Slot(user, 1001, 1), Slot(user, 2000), Slot(user, 2001)], default);
        const string flagsSql = "SELECT flags FROM inventory_slots WHERE user_num = @u AND bag = 0 ORDER BY ordinal";

        Assert.NotNull(await inventory.UpdateWornSelectionAsync(user, WearGroups, 1000, true, default));
        Assert.Equal(Arr(1, 0, 0, 0), await IntListAsync(flagsSql, user));

        Assert.NotNull(await inventory.UpdateWornSelectionAsync(user, WearGroups, 2001, true, default));
        Assert.Equal(Arr(1, 0, 0, 1), await IntListAsync(flagsSql, user));

        Assert.NotNull(await inventory.UpdateWornSelectionAsync(user, WearGroups, 2001, false, default));
        Assert.Equal(Arr(1, 0, 0, 0), await IntListAsync(flagsSql, user));
        Assert.Null(await inventory.UpdateWornSelectionAsync(user, WearGroups, 9999, true, default));
        Assert.Equal(Arr(1, 0, 0, 0), await IntListAsync(flagsSql, user));
    }

    [Fact]
    public async Task FighterUpsertTwiceKeepsOneRowPerSlot()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var fighters = new MySqlFighterRepository(factory);
        await fighters.UpsertAsync(new FighterRecord(user, 0, 5, 1, 300005, "first"), default);
        await fighters.UpsertAsync(new FighterRecord(user, 0, 6, 9, 300006, "second"), default);
        await fighters.UpsertAsync(new FighterRecord(user, 1, 0, 1, 0, ""), default);
        Assert.Equal(2L, await CountAsync("fighters", user));
        Assert.Equal(
            [new FighterRecord(user, 0, 6, 9, 300006, "second"), new FighterRecord(user, 1, 0, 1, 0, "")],
            await fighters.ListByUserNumAsync(user, default));
    }

    [Fact]
    public async Task ClanOpenMembershipAndListsQuery()
    {
        if (await FactoryAsync() is not { } factory) return;
        var store = new MySqlClanStore(factory);
        var user = await NewPlayerAsync();
        Assert.Null(await store.FindLastOpenAsync(user, default));
        Assert.Equal(new ClanMembership(user, 0), await store.GetMembershipAsync(user, default));
        await store.RememberOpenAsync(new ClanOpenRecord(user, ClanOpenKind.MainOpen), default);
        await store.RememberOpenAsync(new ClanOpenRecord(user, ClanOpenKind.Attendance), default);
        await store.RememberOpenAsync(new ClanOpenRecord(user, ClanOpenKind.Attendance), default);
        Assert.Equal(new ClanOpenRecord(user, ClanOpenKind.Attendance), await store.FindLastOpenAsync(user, default));
        Assert.Equal(1L, await CountAsync("clan_opens", user));

        long clanId;
        await using (var db = await RawAsync())
        {
            await using var create = new MySqlCommand("INSERT INTO clans () VALUES (); SELECT LAST_INSERT_ID();", db);
            clanId = Convert.ToInt64(await create.ExecuteScalarAsync());
            await using var join = new MySqlCommand("INSERT INTO clan_members (clan_id, user_num) VALUES (@c, @u)", db);
            join.Parameters.AddWithValue("@c", clanId);
            join.Parameters.AddWithValue("@u", user);
            await join.ExecuteNonQueryAsync();
        }
        Assert.Equal(new ClanMembership(user, clanId), await store.GetMembershipAsync(user, default));
        Assert.Equal([user], await store.ListMemberUserNumsAsync(clanId, 5, default));
        var total = await ScalarAsync<long>("SELECT COUNT(*) FROM clans");
        Assert.Contains(clanId, await store.ListClanIdsAsync((int)total + 50, default));
    }

    [Fact]
    public async Task ShopSessionUpsertReplacesThePreviousOpen()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var store = new MySqlShopSessionStore(factory);
        Assert.Null(await store.FindLastOpenAsync(user, default));
        await store.RememberOpenAsync(new ShopOpenRecord(user, 111, 2, ShopKind.ItemShop), default);
        await store.RememberOpenAsync(new ShopOpenRecord(user, 222, 3, ShopKind.CashShop), default);
        await store.RememberOpenAsync(new ShopOpenRecord(user, 222, 3, ShopKind.CashShop), default);
        Assert.Equal(new ShopOpenRecord(user, 222, 3, ShopKind.CashShop), await store.FindLastOpenAsync(user, default));
        Assert.Equal(1L, await CountAsync("shop_sessions", user));
    }

    [Fact]
    public async Task PlayerLockMakesASecondTransactionWaitAndUnblocksOnCommit()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var store = new MySqlShopPurchaseStore(factory);
        await using var holder = await RawAsync();
        await using var tx = await holder.BeginTransactionAsync();
        await using (var lockCommand = new MySqlCommand("SELECT user_num FROM players WHERE user_num = @u FOR UPDATE", holder, tx))
        {
            lockCommand.Parameters.AddWithValue("@u", user);
            Assert.Equal(user, Convert.ToInt64(await lockCommand.ExecuteScalarAsync()));
        }

        var waiting = Task.Run(() => store.PurchaseAsync(Purchase(user, 31, 4201, price: 10), default).AsTask());
        Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(1500)));
        Assert.Equal(0L, await CountAsync("purchase_log", user));

        await tx.CommitAsync();
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotNull(result);
        Assert.Equal(1L, await CountAsync("purchase_log", user));
    }

    [Fact]
    public async Task AppendAndPurchaseForOnePlayerSerializeWithoutLosingRows()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var inventory = new MySqlInventoryRepository(factory);
        var store = new MySqlShopPurchaseStore(factory);
        await inventory.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [Slot(user, 1000)], default);
        await Task.WhenAll(
            Task.Run(() => inventory.AppendMissingItemsAsync(user, InventoryBag.CharacterBag, [Slot(user, 5000), Slot(user, 5001)], default).AsTask()),
            Task.Run(() => store.PurchaseAsync(Purchase(user, 41, 4301, price: 10), default).AsTask()));
        Assert.Equal(4L, await CountAsync("inventory_slots", user));
        Assert.Equal(Arr(0, 1, 2, 3), await IntListAsync("SELECT ordinal FROM inventory_slots WHERE user_num = @u AND bag = 0 ORDER BY ordinal", user));
    }
}
