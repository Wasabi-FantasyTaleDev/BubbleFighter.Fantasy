using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Fighters;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Persistence.MySql;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

// Runs the repositories against a real server. Every test returns early unless BFF_MYSQL_VERIFY_CONNECTION
// names a scratch database (never a live one): the suite migrates it and writes rows with unique player ids.
[Collection("LiveMySqlDatabase")]
public sealed partial class LiveDatabaseTests
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

    [Fact]
    public async Task BuildLoadoutsPreserveRheaAndIndependentPresetsAcrossRepositoryReopen()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var store = new MySqlInventoryRepository(factory);
        InventoryLoadoutScope korean = new("kr-1256", [300001, 300002, 300029, 202001, 204999]);
        InventoryLoadoutScope older = new("cn-406", [300001, 300002, 202001]);
        InventoryWearGroup[] groups = [new(300001, 300031, 300001, true), new(202000, 204999, 202001, true)];
        var rows = korean.SupportedItemIds.Select((id, index) => new InventorySlotRecord(user, InventoryBag.CharacterBag,
            index + 1, id, 5, (byte)(id is 300029 or 204999 ? 65 : 64))).ToArray();
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        var presets = new MySqlPlayerInventoryState(factory);
        await presets.SetActiveWeaponPresetSlotAsync(user, 4, default);
        await presets.SetWeaponPresetPagesAsync(user, WeaponPresetPages.SeedFromWorn(204999, 0), default);
        await store.UpdateLoadoutSelectionAsync(user, korean, groups, null, false, default);
        var old = await store.UpdateLoadoutSelectionAsync(user, older, groups, null, false, default);
        Assert.DoesNotContain(old!, row => row.ItemId is 300029 or 204999);
        Assert.Contains(old!, row => row.ItemId == 300001 && (row.Flags & 1) != 0);
        await store.UpdateLoadoutSelectionAsync(user, older, groups, 300002, true, default);
        await store.UpdateLoadoutPresetsAsync(user, older, 1, WeaponPresetPages.SeedFromWorn(202001, 0), false, default);
        Assert.Null(await store.UpdateLoadoutSelectionAsync(user, older, groups, 300029, true, default));
        store = new MySqlInventoryRepository(factory);
        var kr = await store.GetLoadoutAsync(user, korean, default);
        Assert.Contains(300029, kr.WornItemIds!);
        Assert.Equal(4, kr.ActivePresetSlot);
        Assert.All(kr.PresetPages!, page => Assert.Equal(204999, page.Unknown0));
        var cn = await store.GetLoadoutAsync(user, older, default);
        Assert.Contains(300002, cn.WornItemIds!);
        Assert.Equal(1, cn.ActivePresetSlot);
        Assert.All(cn.PresetPages!, page => Assert.Equal(202001, page.Unknown0));
        Assert.Equal(rows, await store.ListSlotsAsync(user, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default));
        Assert.Equal(4, await presets.GetActiveWeaponPresetSlotAsync(user, default));
    }

    [Fact]
    public async Task BuildLoadoutWritesSerializeWithSharedAppendsAndPreserveCapacity()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var store = new MySqlInventoryRepository(factory, maxLoadoutsPerPlayer: 1);
        InventoryLoadoutScope scope = new("kr-1256", [300001, 300029, 202001]);
        InventoryWearGroup[] groups = [new(300001, 300031, 300001, true)];
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag,
            [new(user, InventoryBag.CharacterBag, 1, 300001, 1, 1), new(user, InventoryBag.CharacterBag, 2, 300029, 1, 0)], default);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            if (index == 0) await store.AppendMissingItemsAsync(user, InventoryBag.CharacterBag,
                [new(user, InventoryBag.CharacterBag, 3, 202001, 5, 0)], default);
            else await store.UpdateLoadoutSelectionAsync(user, scope, groups, index % 2 == 0 ? 300001 : 300029, true, default);
        })));
        Assert.Equal(5, (await store.ListSlotsAsync(user, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, default))
            .Single(row => row.ItemId == 202001).Quantity);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetLoadoutAsync(user, new("cn-279", [300001]), default).AsTask());
        Assert.NotNull(await store.UpdateLoadoutSelectionAsync(user, scope, groups, 300029, true, default));
    }

    [Fact]
    public async Task BuildLoadoutMutationRejectsAnExpiredAccountAuthority()
    {
        if (await FactoryAsync() is not { } factory) return;
        var domain = await new MySqlPersistentDataDomainIdentity(factory).ReadAsync(default);
        var unpooled = new MySqlConnectionFactory(new MySqlConnectionStringBuilder(ConnectionString!) { Pooling = false }.ConnectionString);
        await using var ownership = new MySqlAccountOwnershipStore(unpooled, domain, 1, 5);
        var user = await NewPlayerAsync();
        var raw = new MySqlInventoryRepository(factory);
        await raw.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [new(user, InventoryBag.CharacterBag, 1, 300001, 1, 1)], default);
        InventoryLoadoutScope scope = new("cn-279", [300001]);
        var protectedStore = new MySqlInventoryRepository(factory, new MySqlAccountWriteFence(ownership.Read));
        Assert.Single(await protectedStore.ListLoadoutSlotsAsync(user, scope, InventoryBag.CharacterBag, 10, default));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM player_loadouts WHERE user_num=@u", ("@u", user)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => protectedStore.UpdateLoadoutPresetsAsync(user, scope, 2, null, false, default).AsTask());
        await using var lease = await ownership.TryAcquireAsync(user, default);
        Assert.NotNull(lease);
        await protectedStore.UpdateLoadoutPresetsAsync(user, scope, 1, null, false, default);
        await lease.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => protectedStore.UpdateLoadoutPresetsAsync(user, scope, 3, null, false, default).AsTask());
        Assert.Equal(1, (await raw.GetLoadoutAsync(user, scope, default)).ActivePresetSlot);
    }

    [Fact]
    public async Task BuildLoadoutIdenticalUpdatesWorkWithAffectedRowCounting()
    {
        if (await FactoryAsync() is null) return;
        var user = await NewPlayerAsync();
        var factory = new MySqlConnectionFactory(new MySqlConnectionStringBuilder(ConnectionString!) { UseAffectedRows = true }.ConnectionString);
        var store = new MySqlInventoryRepository(factory);
        InventoryLoadoutScope scope = new("cn-406", [300001]);
        await store.UpdateLoadoutPresetsAsync(user, scope, 2, null, false, default);
        await store.UpdateLoadoutPresetsAsync(user, scope, 2, null, false, default);
        Assert.Equal(2, (await store.GetLoadoutAsync(user, scope, default)).ActivePresetSlot);
        Assert.Equal(2, (await store.GetLoadoutAsync(user, scope, default)).ActivePresetSlot);
    }

    [Fact]
    public async Task AccountWriteFenceRejectsMissingAndReplacedAuthorityWithoutChangingTheWallet()
    {
        if (await FactoryAsync() is not { } factory) return;
        var domain = await new MySqlPersistentDataDomainIdentity(factory).ReadAsync(default);
        var unpooled = new MySqlConnectionFactory(new MySqlConnectionStringBuilder(ConnectionString!) { Pooling = false }.ConnectionString);
        await using var first = new MySqlAccountOwnershipStore(unpooled, domain, 1, 5);
        await using var second = new MySqlAccountOwnershipStore(unpooled, domain, 1, 5);
        var user = await NewPlayerAsync();
        var firstWallet = new MySqlPlayerWalletRepository(factory, new MySqlAccountWriteFence(first.Read));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await firstWallet.UpsertAsync(new(user, 90, 0), default));
        await using var initialOwner = await first.TryAcquireAsync(user, default);
        Assert.NotNull(initialOwner);
        var staleProof = first.Read(user);
        Assert.NotNull(staleProof);
        await firstWallet.UpsertAsync(new(user, 10, 0), default);
        await initialOwner.DisposeAsync();
        Assert.Null(first.Read(user));
        await using var nextOwner = await second.TryAcquireAsync(user, default);
        Assert.NotNull(nextOwner);
        Assert.NotEqual(staleProof.Token, second.Read(user)!.Token);
        var staleWallet = new MySqlPlayerWalletRepository(factory, new MySqlAccountWriteFence(_ => staleProof));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await staleWallet.UpsertAsync(new(user, 99, 0), default));
        var nextWallet = new MySqlPlayerWalletRepository(factory, new MySqlAccountWriteFence(second.Read));
        await nextWallet.UpsertAsync(new(user, 20, 0), default);
        Assert.Equal(20, (await nextWallet.FindByUserNumAsync(user, default))!.Lucci);
    }

    [Fact]
    public async Task AccountWriteFenceHandoverWaitsForTheOriginalMutationTransactionAfterSocketLoss()
    {
        if (await FactoryAsync() is not { } factory) return;
        var domain = await new MySqlPersistentDataDomainIdentity(factory).ReadAsync(default);
        var unpooled = new MySqlConnectionFactory(new MySqlConnectionStringBuilder(ConnectionString!) { Pooling = false }.ConnectionString);
        await using var first = new MySqlAccountOwnershipStore(unpooled, domain, 1, 10);
        await using var second = new MySqlAccountOwnershipStore(unpooled, domain, 1, 10);
        var user = await NewPlayerAsync();
        await using var initialOwner = await first.TryAcquireAsync(user, default);
        Assert.NotNull(initialOwner);
        var proof = first.Read(user);
        Assert.NotNull(proof);
        await using var writer = await RawAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        var originalFence = new MySqlAccountWriteFence(_ => proof);
        await originalFence.VerifyAsync(writer, transaction, user, default);
        var key = MySqlAccountOwnershipStore.LockName(domain, user);
        // Reuse the observer socket so lock observation does not wait for repeated remote handshakes.
        await using var observer = await RawAsync();
        await using var observe = new MySqlCommand("SELECT COALESCE(IS_USED_LOCK(@name), 0)", observer);
        observe.Parameters.AddWithValue("@name", key);
        async Task<long> ReadOwnerAsync(CancellationToken ct) => Convert.ToInt64(await observe.ExecuteScalarAsync(ct));
        var oldConnection = await ReadOwnerAsync(default);
        await using (var kill = new MySqlCommand("KILL CONNECTION " + oldConnection.ToString(System.Globalization.CultureInfo.InvariantCulture), observer))
            await kill.ExecuteNonQueryAsync();
        var pending = second.TryAcquireAsync(user, default).AsTask();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await ReadOwnerAsync(deadline.Token) is var current
            && (current == 0 || current == oldConnection))
        {
            if (pending.IsCompleted) await pending;
            await Task.Delay(10, deadline.Token);
        }
        Assert.False(pending.IsCompleted);
        Assert.Null(second.Read(user));
        await transaction.CommitAsync();
        await using var nextOwner = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(nextOwner);
        await using var staleTransaction = await writer.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await originalFence.VerifyAsync(writer, staleTransaction, user, default));
    }

    [Fact]
    public async Task AccountHandoffMovesWriteAuthorityToTheNamedMigrationOnly()
    {
        if (await FactoryAsync() is not { } factory) return;
        var domain = await new MySqlPersistentDataDomainIdentity(factory).ReadAsync(default);
        var unpooled = new MySqlConnectionFactory(new MySqlConnectionStringBuilder(ConnectionString!) { Pooling = false }.ConnectionString);
        await using var source = new MySqlAccountOwnershipStore(unpooled, domain, 2, 5, "node-a");
        await using var destination = new MySqlAccountOwnershipStore(unpooled, domain, 2, 5, "node-b");
        var user = await NewPlayerAsync();
        var migration = Guid.NewGuid().ToString("N");
        var sourceWallet = new MySqlPlayerWalletRepository(factory, new MySqlAccountWriteFence(source.Read));
        var owner = await source.TryAcquireAsync(user, default);
        Assert.NotNull(owner);
        await sourceWallet.UpsertAsync(new(user, 10, 0), default);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await source.HandOffAsync(owner, new(user, migration, "other", "node-b", TimeSpan.FromSeconds(30)), default));
        Assert.True(await owner.IsHeldAsync(default));
        await source.HandOffAsync(owner, new(user, migration, "node-a", "node-b", TimeSpan.FromSeconds(30)), default);
        await using var unrelated = new MySqlAccountOwnershipStore(unpooled, domain, 1, 5, "node-c");
        Assert.Null(await unrelated.TryClaimAsync(user, migration, default));
        Assert.Null(await unrelated.TryWithdrawAsync(user, migration, default));
        Assert.Null(await source.TryClaimAsync(user, migration, default));
        Assert.Null(await destination.TryWithdrawAsync(user, migration, default));

        Assert.Null(source.Read(user));
        Assert.False(await owner.IsHeldAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sourceWallet.UpsertAsync(new(user, 99, 0), default));
        Assert.Null(await destination.TryAcquireAsync(user, default));
        Assert.Null(await destination.TryClaimAsync(user, Guid.NewGuid().ToString("N"), default));
        await using var claimed = await destination.TryClaimAsync(user, migration, default);
        Assert.NotNull(claimed);
        Assert.Null(await source.TryWithdrawAsync(user, migration, default));
        var destinationWallet = new MySqlPlayerWalletRepository(factory, new MySqlAccountWriteFence(destination.Read));
        await destinationWallet.UpsertAsync(new(user, 20, 0), default);
        Assert.Equal(20, (await destinationWallet.FindByUserNumAsync(user, default))!.Lucci);
        Assert.Equal("claimed", await ScalarAsync<string>("SELECT state FROM account_handoffs WHERE user_num = @u", ("@u", user)));
        await claimed.DisposeAsync();
        Assert.Null(await destination.TryClaimAsync(user, migration, default));
    }

    [Fact]
    public async Task WithdrawnAndExpiredHandoffsReturnTheAccountToOrdinaryAdmission()
    {
        if (await FactoryAsync() is not { } factory) return;
        var domain = await new MySqlPersistentDataDomainIdentity(factory).ReadAsync(default);
        var unpooled = new MySqlConnectionFactory(new MySqlConnectionStringBuilder(ConnectionString!) { Pooling = false }.ConnectionString);
        await using var source = new MySqlAccountOwnershipStore(unpooled, domain, 2, 5, "node-a");
        await using var destination = new MySqlAccountOwnershipStore(unpooled, domain, 2, 5, "node-b");
        var user = await NewPlayerAsync();
        var migration = Guid.NewGuid().ToString("N");
        var owner = await source.TryAcquireAsync(user, default);
        Assert.NotNull(owner);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await source.HandOffAsync(owner, new(user, migration, "other", "node-b", TimeSpan.FromSeconds(30)), default));
        Assert.True(await owner.IsHeldAsync(default));
        await source.HandOffAsync(owner, new(user, migration, "node-a", "node-b", TimeSpan.FromSeconds(30)), default);
        await using var unrelated = new MySqlAccountOwnershipStore(unpooled, domain, 1, 5, "node-c");
        Assert.Null(await unrelated.TryClaimAsync(user, migration, default));
        Assert.Null(await unrelated.TryWithdrawAsync(user, migration, default));
        Assert.Null(await source.TryClaimAsync(user, migration, default));
        Assert.Null(await destination.TryWithdrawAsync(user, migration, default));
        var restored = await source.TryWithdrawAsync(user, migration, default);
        Assert.NotNull(restored);
        Assert.NotNull(source.Read(user));
        Assert.Null(await destination.TryClaimAsync(user, migration, default));
        Assert.Equal("withdrawn", await ScalarAsync<string>("SELECT state FROM account_handoffs WHERE user_num = @u", ("@u", user)));

        var late = Guid.NewGuid().ToString("N");
        await source.HandOffAsync(restored, new(user, late, "node-a", "node-b", TimeSpan.FromMilliseconds(200)), default);
        await Task.Delay(600);
        Assert.Null(await destination.TryClaimAsync(user, late, default));
        await using var ordinary = await destination.TryAcquireAsync(user, default);
        Assert.NotNull(ordinary);
        Assert.Equal("expired", await ScalarAsync<string>("SELECT state FROM account_handoffs WHERE user_num = @u", ("@u", user)));
        Assert.Null(await source.TryWithdrawAsync(user, late, default));
    }

    [Fact]
    public async Task ClanWritesRequireTheOriginalAccountBinding()
    {
        if (await FactoryAsync() is not { } factory) return;
        var domain = await new MySqlPersistentDataDomainIdentity(factory).ReadAsync(default);
        var unpooled = new MySqlConnectionFactory(new MySqlConnectionStringBuilder(ConnectionString!) { Pooling = false }.ConnectionString);
        await using var owners = new MySqlAccountOwnershipStore(unpooled, domain, 1, 5);
        var mutations = new MySqlAccountMutationScopes(owners.Read);
        var clans = new MySqlClanStore(factory, new MySqlAccountWriteFence(mutations.Read));
        var user = await NewPlayerAsync();
        var opened = new ClanOpenRecord(user, ClanOpenKind.MainOpen);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await clans.RememberOpenAsync(opened, default));
        Assert.Null(await clans.FindLastOpenAsync(user, default));
        await using var first = await owners.TryAcquireAsync(user, default);
        Assert.NotNull(first);
        var session = new MutationSession(1);
        using var firstBinding = mutations.Bind(session, first);
        using var originalFlow = mutations.Enter(session);
        await clans.RememberOpenAsync(opened, default);
        Assert.Equal(opened, await clans.FindLastOpenAsync(user, default));
        firstBinding.Dispose();
        await first.DisposeAsync();
        await using var replacement = await owners.TryAcquireAsync(user, default);
        Assert.NotNull(replacement);
        using var replacementBinding = mutations.Bind(session, replacement);
        var attended = new ClanOpenRecord(user, ClanOpenKind.Attendance);
        using (mutations.Enter(session)) await clans.RememberOpenAsync(attended, default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await clans.RememberOpenAsync(opened, default));
        Assert.Equal(attended, await clans.FindLastOpenAsync(user, default));
    }

    private static Task<long> CountAsync(string table, long userNum) =>
        ScalarAsync<long>($"SELECT COUNT(*) FROM {table} WHERE user_num = @u", ("@u", userNum));

    [Fact]
    public async Task ShopWritesRequireTheOriginalAccountBinding()
    {
        if (await FactoryAsync() is not { } factory) return;
        var domain = await new MySqlPersistentDataDomainIdentity(factory).ReadAsync(default);
        var unpooled = new MySqlConnectionFactory(new MySqlConnectionStringBuilder(ConnectionString!) { Pooling = false }.ConnectionString);
        await using var owners = new MySqlAccountOwnershipStore(unpooled, domain, 1, 5);
        var mutations = new MySqlAccountMutationScopes(owners.Read);
        var shops = new MySqlShopSessionStore(factory, new MySqlAccountWriteFence(mutations.Read));
        var user = await NewPlayerAsync();
        var opened = new ShopOpenRecord(user, 11, 1, ShopKind.ItemShop);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await shops.RememberOpenAsync(opened, default));
        Assert.Null(await shops.FindLastOpenAsync(user, default));
        await using var first = await owners.TryAcquireAsync(user, default);
        Assert.NotNull(first);
        var session = new MutationSession(1);
        using var firstBinding = mutations.Bind(session, first);
        using var originalFlow = mutations.Enter(session);
        await shops.RememberOpenAsync(opened, default);
        Assert.Equal(opened, await shops.FindLastOpenAsync(user, default));
        firstBinding.Dispose();
        await first.DisposeAsync();
        await using var replacement = await owners.TryAcquireAsync(user, default);
        Assert.NotNull(replacement);
        using var replacementBinding = mutations.Bind(session, replacement);
        var reopened = new ShopOpenRecord(user, 22, 2, ShopKind.CashShop);
        using (mutations.Enter(session)) await shops.RememberOpenAsync(reopened, default);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await shops.RememberOpenAsync(opened, default));
        Assert.Equal(reopened, await shops.FindLastOpenAsync(user, default));
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountOwnershipPluginActivatesAndReleasesItsPublishedStoreOnUnload(bool reuseSession)
    {
        if (await FactoryAsync() is not { } factory) return;
        var domain = await new MySqlPersistentDataDomainIdentity(factory).ReadAsync(default);
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow
        {
            Id = "accounts", Plugin = "persistence.mysql-account-ownership", Source = "test",
            Config = new Dictionary<string, object?>
            {
                ["connection_string"] = ConnectionString!, ["persistent_domain_id"] = domain, ["max_owners"] = 2,
            },
        }, services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var user = await NewPlayerAsync();
        IAccountOwnership? hold = null;
        try
        {
            await new MySqlAccountOwnershipPlugin().ActivateAsync(context, default);
            hold = await services.Get<IAccountOwnershipStore>().TryAcquireAsync(user, default);
            Assert.NotNull(hold);
            Assert.True(await hold.IsHeldAsync(default));
            var mutations = services.Get<IAccountMutationScopes>();
            var session = new MutationSession(1);
            using var binding = mutations.Bind(session, hold);
            using var caller = mutations.Enter(session);
            var fencedWallet = new MySqlPlayerWalletRepository(factory, services.Get<IAccountMutationFence>());
            await fencedWallet.UpsertAsync(new(user, 17, 0), default);
            Assert.Equal(17, (await fencedWallet.FindByUserNumAsync(user, default))!.Lucci);
            var resumeOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delayedOld = Task.Run(async () =>
            {
                await resumeOld.Task;
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await fencedWallet.UpsertAsync(new(user, 99, 0), default));
            });
            try
            {
                binding.Dispose();
                await hold.DisposeAsync();
                await using var renewed = await services.Get<IAccountOwnershipStore>().TryAcquireAsync(user, default);
                Assert.NotNull(renewed);
                var next = reuseSession ? session : new MutationSession(2);
                using var nextBinding = mutations.Bind(next, renewed);
                using (mutations.Enter(next))
                {
                    await fencedWallet.UpsertAsync(new(user, 19, 0), default);
                    resumeOld.TrySetResult();
                    await delayedOld.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            finally
            {
                resumeOld.TrySetResult();
                await delayedOld.WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.Equal(19, (await fencedWallet.FindByUserNumAsync(user, default))!.Lucci);
        }
        finally { await context.UnwindAsync(); }
        Assert.False(services.TryGet<IAccountOwnershipStore>(out _));
        Assert.False(services.TryGet<IAccountMutationFence>(out _));
        Assert.False(services.TryGet<IAccountMutationScopes>(out _));
        Assert.NotNull(hold);
        Assert.False(await hold.IsHeldAsync(default));
        var unpooled = new MySqlConnectionFactory(new MySqlConnectionStringBuilder(ConnectionString!) { Pooling = false }.ConnectionString);
        await using var replacementStore = new MySqlAccountOwnershipStore(unpooled, domain, 1, 3);
        await using var replacement = await replacementStore.TryAcquireAsync(user, default);
        Assert.NotNull(replacement);
        Assert.True(await replacement.IsHeldAsync(default));
    }

    [Fact]
    public async Task AccountOwnershipIsExclusiveAcrossIndependentStores()
    {
        if (await FactoryAsync() is not { } factory) return;
        var domain = await new MySqlPersistentDataDomainIdentity(factory).ReadAsync(default);
        var unpooled = new MySqlConnectionFactory(new MySqlConnectionStringBuilder(ConnectionString!) { Pooling = false }.ConnectionString);
        await using var first = new MySqlAccountOwnershipStore(unpooled, domain, 2, 3);
        await using var second = new MySqlAccountOwnershipStore(unpooled, domain, 2, 3);
        var user = await NewPlayerAsync();
        var attempts = await Task.WhenAll(first.TryAcquireAsync(user, default).AsTask(), second.TryAcquireAsync(user, default).AsTask());
        var winner = Assert.Single(attempts, result => result is not null)!;
        Assert.True(await winner.IsHeldAsync(default));
        await winner.DisposeAsync();
        await using var replacement = await second.TryAcquireAsync(user, default);
        Assert.NotNull(replacement);
        await winner.DisposeAsync();
        Assert.False(await winner.IsHeldAsync(default));
        Assert.True(await replacement.IsHeldAsync(default));
    }

    [Fact]
    public async Task AccountOwnershipLosesTheKilledConnectionWithoutReacquiring()
    {
        if (await FactoryAsync() is not { } factory) return;
        var domain = await new MySqlPersistentDataDomainIdentity(factory).ReadAsync(default);
        var unpooled = new MySqlConnectionFactory(new MySqlConnectionStringBuilder(ConnectionString!) { Pooling = false }.ConnectionString);
        await using var first = new MySqlAccountOwnershipStore(unpooled, domain, 1, 3);
        await using var second = new MySqlAccountOwnershipStore(unpooled, domain, 1, 3);
        var user = await NewPlayerAsync();
        await using var owner = await first.TryAcquireAsync(user, default);
        Assert.NotNull(owner);
        var holder = await ScalarAsync<long>("SELECT IS_USED_LOCK(@name)", ("@name", MySqlAccountOwnershipStore.LockName(domain, user)));
        Assert.True(holder > 0);
        _ = await ScalarAsync<long>("KILL CONNECTION " + holder.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(async () => await owner.IsHeldAsync(default));
        Assert.False(await owner.IsHeldAsync(default));
        await using var replacement = await second.TryAcquireAsync(user, default);
        Assert.NotNull(replacement);
        await owner.DisposeAsync();
        Assert.True(await replacement.IsHeldAsync(default));
    }

    [Fact]
    public async Task AccountOwnershipCannotRecoverAfterItsDomainChanges()
    {
        if (await FactoryAsync() is not { } factory) return;
        var domain = await new MySqlPersistentDataDomainIdentity(factory).ReadAsync(default);
        var unpooled = new MySqlConnectionFactory(new MySqlConnectionStringBuilder(ConnectionString!) { Pooling = false }.ConnectionString);
        await using var store = new MySqlAccountOwnershipStore(unpooled, domain.ToUpperInvariant(), 1, 3);
        await using var owner = await store.TryAcquireAsync(await NewPlayerAsync(), default);
        Assert.NotNull(owner);
        Assert.True(await owner.IsHeldAsync(default));
        try
        {
            _ = await ScalarAsync<long>("UPDATE cluster_data_domain SET domain_id = @id WHERE singleton_id = 1", ("@id", Guid.NewGuid().ToString("D")));
            Assert.False(await owner.IsHeldAsync(default));
        }
        finally
        {
            _ = await ScalarAsync<long>("UPDATE cluster_data_domain SET domain_id = @id WHERE singleton_id = 1", ("@id", Guid.ParseExact(domain, "N").ToString("D")));
        }
        Assert.False(await owner.IsHeldAsync(default));
    }

    [Fact]
    public async Task AccountOwnershipRefusesTheWrongDatabaseDomain()
    {
        if (await FactoryAsync() is not { } factory) return;
        var unpooled = new MySqlConnectionFactory(new MySqlConnectionStringBuilder(ConnectionString!) { Pooling = false }.ConnectionString);
        await using var wrong = new MySqlAccountOwnershipStore(unpooled, Guid.NewGuid().ToString("N"), 1, 3);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await wrong.TryAcquireAsync(await NewPlayerAsync(), default));
    }

    [Fact]
    public async Task AccountOwnershipDisposalReleasesEveryHoldAndRefusesNewWork()
    {
        if (await FactoryAsync() is not { } factory) return;
        var domain = await new MySqlPersistentDataDomainIdentity(factory).ReadAsync(default);
        var unpooled = new MySqlConnectionFactory(new MySqlConnectionStringBuilder(ConnectionString!) { Pooling = false }.ConnectionString);
        var first = new MySqlAccountOwnershipStore(unpooled, domain, 2, 3);
        await using var second = new MySqlAccountOwnershipStore(unpooled, domain, 2, 3);
        var user1 = await NewPlayerAsync();
        var user2 = await NewPlayerAsync();
        Assert.NotNull(await first.TryAcquireAsync(user1, default));
        Assert.NotNull(await first.TryAcquireAsync(user2, default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await first.TryAcquireAsync(await NewPlayerAsync(), default));
        await first.DisposeAsync();
        await using var renewed1 = await second.TryAcquireAsync(user1, default);
        await using var renewed2 = await second.TryAcquireAsync(user2, default);
        Assert.NotNull(renewed1);
        Assert.NotNull(renewed2);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await first.TryAcquireAsync(user1, default));
    }

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
    public async Task DatabaseDomainIdentityIsStableAcrossRepositoriesAndRepeatedMigrations()
    {
        if (await FactoryAsync() is not { } factory) return;
        var identity = await new MySqlPersistentDataDomainIdentity(factory).ReadAsync(default);
        Assert.True(Guid.TryParseExact(identity, "N", out var id));
        Assert.NotEqual(Guid.Empty, id);
        await new MigrationRunner(factory, NullLogger.Instance, typeof(MySqlPlugin).Assembly).ApplyAsync(default);
        var separateConnectionPool = new MySqlConnectionFactory(ConnectionString!);
        Assert.Equal(identity, await new MySqlPersistentDataDomainIdentity(separateConnectionPool).ReadAsync(default));
        var textual = new MySqlConnectionStringBuilder(ConnectionString!) { GuidFormat = MySqlGuidFormat.None };
        Assert.Equal(identity, await new MySqlPersistentDataDomainIdentity(new MySqlConnectionFactory(textual.ConnectionString)).ReadAsync(default));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM cluster_data_domain"));
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
    public async Task ConcurrentWalletInitializationAcrossRepositoriesPreservesTheCommittedWinner()
    {
        if (await FactoryAsync() is not { } factory) return;
        var user = await NewPlayerAsync();
        var repositories = Enumerable.Range(0, 16).Select(_ => new MySqlPlayerWalletRepository(factory)).ToArray();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = repositories.Select(async (repository, index) =>
        {
            await start.Task;
            return await repository.GetOrCreateAsync(new(user, 100 + index, index), default);
        }).ToArray();
        start.SetResult();
        var wallets = await Task.WhenAll(calls);
        Assert.All(wallets, wallet => Assert.Equal(wallets[0], wallet));
        Assert.Equal(1L, await CountAsync("player_wallets", user));

        var committed = new PlayerWallet(user, 765, 43);
        await repositories[0].UpsertAsync(committed, default);
        var timestamp = await ScalarAsync<DateTime>("SELECT updated_at FROM player_wallets WHERE user_num = @u", ("@u", user));
        var existing = await Task.WhenAll(repositories.Select(repository =>
            repository.GetOrCreateAsync(new(user, 9999, 9999), default).AsTask()));
        Assert.All(existing, wallet => Assert.Equal(committed, wallet));
        Assert.Equal(timestamp, await ScalarAsync<DateTime>("SELECT updated_at FROM player_wallets WHERE user_num = @u", ("@u", user)));
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
