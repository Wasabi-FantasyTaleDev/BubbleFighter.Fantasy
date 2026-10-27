using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

public sealed class SqliteSaveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-sqlite-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "save.sqlite");
    private SqliteOptions Options(int players = 4, int accounts = 4, long bytes = 1048576) => new()
        { DatabasePath = DatabasePath, MaxPlayers = players, MaxAccounts = accounts, MaxDatabaseBytes = bytes };

    [Fact]
    public async Task InitialPagesDoNotReplaceAnExistingTable()
    {
        using var database = new SqliteDatabase(Options());
        var stores = new SqliteRepositories(database);
        Assert.True(await stores.RegisterAsync("preset-initializer", "hash", default));
        var player = await stores.CreateCharacterAsync("preset-initializer", "preset-player",
            new([300001, 202001], [300001, 202001]), default);
        var first = WeaponPresetPages.SeedFromWorn(202001, 0);
        var second = WeaponPresetPages.SeedFromWorn(204001, 205001);
        var pages = await stores.GetOrCreateWeaponPresetPagesAsync(player!.UserNum, first, default);
        Assert.Equal(first, pages);
        pages[0] = new(999, 0, 0);
        Assert.Equal(first, await stores.GetOrCreateWeaponPresetPagesAsync(player.UserNum, second, default));
        Assert.Equal(first, await stores.GetWeaponPresetPagesAsync(player.UserNum, default));
    }

    [Fact]
    public async Task RestartRestoresIdentityOnboardingWalletProgressInventoryLifetimesAndOpens()
    {
        long user;
        var deadline = InventoryLifetime.Until(DateTimeOffset.UtcNow.AddDays(3));
        // The materialized preset pages ride the same snapshot; the second and third words are stored
        // verbatim because their meaning is not traced.
        WeaponPresetPage[] pages =
        [
            new(202001, 205001, 0), new(203001, 205001, 0), new(204001, 0, 0),
            new(202003, 205001, 7), new(204003, 0, 0),
        ];
        using (var database = new SqliteDatabase(Options()))
        {
            var stores = new SqliteRepositories(database);
            Assert.True(await stores.RegisterAsync("account", "hash", default));
            var player = await stores.CreateCharacterAsync("account", "nickname", new([300001, 201001], [300001, 201001]), default);
            user = player!.UserNum;
            Assert.True(await stores.NeedsTutorialAsync(user, default));
            await stores.CompleteTutorialAsync(user, default);
            await stores.UpsertAsync(new BubbleFighter.Fantasy.Contracts.Wallet.PlayerWallet(user, 100, 50), default);
            await stores.SetActiveWeaponPresetSlotAsync(user, 3, default);
            await stores.SetWeaponPresetPagesAsync(user, pages, default);
            var purchase = await stores.PurchaseAsync(new(new(user, 999, 999), 42, 0, "lucci", 10, 1000,
                [new(100001, 2) { Lifetime = deadline }, new(100002, 1) { Lifetime = InventoryLifetime.Permanent }]), default);
            Assert.Equal(90, purchase!.Wallet.Lucci);
            Assert.Equal(95, (await stores.SettleRoundAsync(new("m-save", user, 5, _ => new(user, 2, 123)), default)).LucciBalance);
            await stores.RememberOpenAsync(new ShopOpenRecord(user, 1234, 0, ShopKind.ItemShop), default);
            await stores.RememberOpenAsync(new ClanOpenRecord(user, ClanOpenKind.MainOpen), default);
        }
        Assert.False(File.Exists(DatabasePath + "-wal"));
        using (var database = new SqliteDatabase(Options()))
        {
            var stores = new SqliteRepositories(database);
            var player = await stores.FindByUsernameAsync("account", default);
            Assert.Equal(user, player!.UserNum);
            Assert.Equal(2, player.Level);
            Assert.True(await stores.HasCreatedCharacterAsync(user, default));
            Assert.False(await stores.NeedsTutorialAsync(user, default));
            var account = await stores.FindAsync("account", default);
            Assert.Equal("hash", account!.PasswordHash);
            Assert.Equal(user, account.UserNumber);
            Assert.Equal(95, (await stores.FindByUserNumAsync(user, default))!.Lucci);
            Assert.Equal(123, (await stores.FindAsync(user, default))!.Exp);
            Assert.Equal(3, await stores.GetActiveWeaponPresetSlotAsync(user, default));
            Assert.Equal(pages, await stores.GetWeaponPresetPagesAsync(user, default));
            var slots = await stores.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default);
            Assert.Equal(deadline, slots.Single(slot => slot.ItemId == 100001).Lifetime);
            Assert.Equal(InventoryLifetime.Permanent, slots.Single(slot => slot.ItemId == 100002).Lifetime);
            Assert.Equal(1, await stores.ConsumeItemAsync(user, 100001, default));
            Assert.Equal(2, (await stores.ListByUserNumAsync(user, default)).Single().Level);
            Assert.Single(await stores.ListRecentAsync(user, 10, default));
            Assert.Equal(1234, (await ((IShopSessionStore)stores).FindLastOpenAsync(user, default))!.Token);
            Assert.Equal(ClanOpenKind.MainOpen, (await ((IClanStore)stores).FindLastOpenAsync(user, default))!.Kind);
        }
    }

    [Fact]
    public async Task RegistrationAndFirstPasswordBindingAreAtomicAndSurviveRestart()
    {
        using (var database = new SqliteDatabase(Options()))
        {
            var stores = new SqliteRepositories(database);
            var registered = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
                await stores.RegisterAsync("new", "hash", default))));
            Assert.Single(registered, value => value);
            await stores.CreateAsync("old", "nick", default);
            var bound = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
                await stores.TrySetInitialPasswordAsync("old", "hash" + i, default))));
            Assert.Single(bound, value => value);
            Assert.False(await stores.RegisterAsync("old", "overwrite", default));
        }
        using var reopened = new SqliteDatabase(Options());
        var accounts = new SqliteRepositories(reopened);
        Assert.StartsWith("hash", (await accounts.FindAsync("old", default))!.PasswordHash);
        Assert.False(await accounts.TrySetInitialPasswordAsync("old", "overwrite", default));
    }

    [Fact]
    public async Task FailedAndConflictingPurchasesDoNotChangeWalletBagOrAudit()
    {
        using var database = new SqliteDatabase(Options());
        var stores = new SqliteRepositories(database);
        var user = (await stores.CreateAsync("a", "n", default)).UserNum;
        await stores.UpsertAsync(new BubbleFighter.Fantasy.Contracts.Wallet.PlayerWallet(user, 100, 0), default);
        var slot = new InventorySlotRecord(user, InventoryBag.CharacterBag, 1, 100001, 3, 0x81)
            { Lifetime = InventoryLifetime.Permanent };
        await stores.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, [slot], default);
        var command = new ShopPurchaseCommand(new(user, 1, 0), 42, 0, "lucci", 10, 1000,
            [new(100001, 4) { Mode = ShopGrantMode.MatchItem, ExpectedSlot = slot, Lifetime = slot.Lifetime }]);
        Assert.Null(await stores.PurchaseAsync(command with { Price = 101 }, default));
        var successes = 0;
        var conflicts = 0;
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            try { await stores.PurchaseAsync(command, default); Interlocked.Increment(ref successes); }
            catch (ShopPurchaseConflictException) { Interlocked.Increment(ref conflicts); }
        })));
        Assert.Equal(1, successes);
        Assert.Equal(11, conflicts);
        Assert.Equal(90, (await stores.FindByUserNumAsync(user, default))!.Lucci);
        Assert.Single(await stores.ListRecentAsync(user, 10, default));
        var full = Enumerable.Range(0, 512).Select(i => slot with { SlotKey = i + 1, ItemId = 100001 + i }).ToArray();
        await stores.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, full, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stores.PurchaseAsync(command with
            { Grants = [new(999999, 1)] }, default).AsTask());
        Assert.Equal(full, await stores.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default));
        Assert.Equal(90, (await stores.FindByUserNumAsync(user, default))!.Lucci);
        Assert.Single(await stores.ListRecentAsync(user, 10, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stores.PurchaseAsync(command,
            new CancellationToken(true)).AsTask());
    }

    [Fact]
    public async Task QuotaFailureRollsBackAndSaveCanBeReopened()
    {
        using (var database = new SqliteDatabase(Options(bytes: 1048576)))
        {
            var stores = new SqliteRepositories(database);
            await stores.RegisterAsync("first", "hash", default);
            var exception = await Assert.ThrowsAsync<SqliteException>(() => stores.RegisterAsync("large",
                new string('x', 2097152), default).AsTask());
            Assert.Equal(13, exception.SqliteErrorCode);
            Assert.Null(await stores.FindAsync("large", default));
            Assert.NotNull(await stores.FindAsync("first", default));
        }
        Assert.InRange(new FileInfo(DatabasePath).Length, 1, 1048576);
        using var reopened = new SqliteDatabase(Options());
        Assert.Null(await new SqliteRepositories(reopened).FindAsync("large", default));
    }

    [Fact]
    public async Task CapacityAndSamePathLeaseFailExplicitlyAndReleaseOnClose()
    {
        using (var database = new SqliteDatabase(Options(1, 1)))
        {
            Assert.Throws<IOException>(() => new SqliteDatabase(Options()));
            var stores = new SqliteRepositories(database);
            await stores.CreateAsync("a", "n", default);
            await stores.RegisterAsync("b", "hash", default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => stores.CreateAsync("c", "n2", default).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => stores.RegisterAsync("c", "hash", default).AsTask());
        }
        using var reopened = new SqliteDatabase(Options());
        Assert.NotNull(await new SqliteRepositories(reopened).FindByUsernameAsync("a", default));
    }

    [Theory]
    [InlineData("PRAGMA user_version=99")]
    [InlineData("PRAGMA application_id=1")]
    [InlineData("ALTER TABLE accounts RENAME COLUMN password_hash TO lost")]
    [InlineData("UPDATE players SET state='{}'")]
    [InlineData("UPDATE players SET state='null'")]
    public async Task UnsupportedOrMalformedSaveFailsStartupWithoutFallback(string mutation)
    {
        using (var database = new SqliteDatabase(Options()))
            await new SqliteRepositories(database).CreateAsync("a", "n", default);
        using (var connection = new SqliteConnection("Data Source=" + DatabasePath + ";Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = mutation;
            command.ExecuteNonQuery();
        }
        Assert.ThrowsAny<Exception>(() => new SqliteDatabase(Options()));
        using var lease = new FileStream(DatabasePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void CorruptFileAndRelativePathFailWithoutCreatingMemoryState()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(DatabasePath, "not a database");
        Assert.Throws<SqliteException>(() => new SqliteDatabase(Options()));
        Assert.Throws<ConfigException>(() => new SqliteDatabase(new() { DatabasePath = "relative.sqlite" }));
    }

    [Fact]
    public async Task EquipmentAndRenameConsumeAreAtomicAcrossBothInventoryViews()
    {
        using var database = new SqliteDatabase(Options());
        var stores = new SqliteRepositories(database);
        var user = (await stores.CreateCharacterAsync("a", "old", new([300001, 201001, 201002], [300001, 201001]), default))!.UserNum;
        await stores.CreateAsync("b", "taken", default);
        await stores.AppendMissingItemsAsync(user, InventoryBag.CharacterBag,
            [new(user, InventoryBag.CharacterBag, 11, 100001, 2, 0) { Lifetime = InventoryLifetime.Until(DateTimeOffset.UtcNow.AddDays(1)) },
             new(user, InventoryBag.CharacterBag, 12, 100002, 2, 0) { Lifetime = InventoryLifetime.Until(DateTimeOffset.UtcNow.AddDays(-1)) }], default);
        Assert.Null(await stores.ConsumeItemAsync(user, 100002, default));
        Assert.Equal(9, await stores.RenameWithItemAsync(user, "taken", 100001, default));
        Assert.Equal(2, (await stores.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default)).Single(s => s.ItemId == 100001).Quantity);
        Assert.Equal(0, await stores.RenameWithItemAsync(user, "new", 100001, default));
        Assert.Equal("new", (await stores.FindByUsernameAsync("a", default))!.Nickname);
        Assert.Equal("new", (await stores.ListByUserNumAsync(user, default)).Single().Nickname);
        Assert.Equal(1, (await stores.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default)).Single(s => s.ItemId == 100001).Quantity);
        await stores.UpdateWornSelectionAsync(user, [new(201001, 201999, 201001, true)], 201002, true, default);
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var rows = await stores.ListSlotsAsync(user, bag, 512, default);
            Assert.Equal(0, rows.Single(s => s.ItemId == 201001).Flags & 1);
            Assert.Equal(1, rows.Single(s => s.ItemId == 201002).Flags & 1);
        }
    }

    [Fact]
    public async Task ExpiryRemovalDeletesOnlyThisPlayersEndedRowsFromBothBags()
    {
        using var database = new SqliteDatabase(Options());
        var stores = new SqliteRepositories(database);
        var user = (await stores.CreateCharacterAsync("a", "one", new([300001, 201001], [300001, 201001]), default))!.UserNum;
        var other = (await stores.CreateCharacterAsync("b", "two", new([300001, 201001], [300001, 201001]), default))!.UserNum;
        var now = DateTimeOffset.UtcNow;
        foreach (var owner in new[] { user, other })
            foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
                await stores.AppendMissingItemsAsync(owner, bag,
                    [new(owner, bag, 21, 100001, 1, 0) { Lifetime = InventoryLifetime.Until(now.AddDays(-1)) },
                     new(owner, bag, 22, 100002, 1, 0) { Lifetime = InventoryLifetime.Until(now.AddDays(1)) }], default);

        var removed = await stores.RemoveExpiredAsync(user, now, default);

        Assert.Equal([(InventoryBag.CharacterBag, 100001), (InventoryBag.EquippedBag, 100001)],
            removed.OrderBy(row => row.Bag).Select(row => (row.Bag, row.ItemId)));
        foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            Assert.DoesNotContain(await stores.ListSlotsAsync(user, bag, 512, default), row => row.ItemId == 100001);
            Assert.Contains(await stores.ListSlotsAsync(user, bag, 512, default), row => row.ItemId == 100002);
            Assert.Contains(await stores.ListSlotsAsync(other, bag, 512, default), row => row.ItemId == 100001);
        }
        Assert.Empty(await stores.RemoveExpiredAsync(user, now, default));
    }

    [Fact]
    public async Task TheDailyLifeRuleSurvivesARestartChargesOnlyLifeRowsAndNeverOtherPlayers()
    {
        long user, other;
        var day = new DateOnly(2026, 10, 12);
        using (var database = new SqliteDatabase(Options()))
        {
            var stores = new SqliteRepositories(database);
            user = (await stores.CreateCharacterAsync("a", "one", new([300001, 201001], [300001, 201001]), default))!.UserNum;
            other = (await stores.CreateCharacterAsync("b", "two", new([300001, 201001], [300001, 201001]), default))!.UserNum;
            foreach (var owner in new[] { user, other })
                foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
                    await stores.AppendMissingItemsAsync(owner, bag,
                        [new(owner, bag, 21, 201350, 5, 0) { Lifetime = InventoryLifetime.Life },
                         new(owner, bag, 22, 100104, 5, 0) { Lifetime = InventoryLifetime.Permanent }], default);
            Assert.Equal(0, await stores.AdvanceLifeAsync(user, day, default));
            Assert.Equal(0, await stores.AdvanceLifeAsync(other, day, default));
            Assert.Equal(1, await stores.AdvanceLifeAsync(user, day.AddDays(1), default));
        }
        using (var database = new SqliteDatabase(Options()))
        {
            var stores = new SqliteRepositories(database);
            // The stored day (day + 1) survived the restart: a repeat the same day charges nothing.
            Assert.Equal(0, await stores.AdvanceLifeAsync(user, day.AddDays(1), default));
            Assert.Equal(3, await stores.AdvanceLifeAsync(user, day.AddDays(4), default));
            foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            {
                var rows = await stores.ListSlotsAsync(user, bag, 512, default);
                Assert.Equal(1, rows.Single(row => row.ItemId == 201350).Quantity);
                Assert.Equal(InventoryLifetime.Life, rows.Single(row => row.ItemId == 201350).Lifetime);
                Assert.Equal(5, rows.Single(row => row.ItemId == 100104).Quantity);
                Assert.Equal(5, (await stores.ListSlotsAsync(other, bag, 512, default)).Single(row => row.ItemId == 201350).Quantity);
            }
            // Past zero the row floors, then the expiry removal takes it from both bags.
            Assert.Equal(9, await stores.AdvanceLifeAsync(user, day.AddDays(13), default));
            var removed = await stores.RemoveExpiredAsync(user, DateTimeOffset.UtcNow, default);
            Assert.Equal([(InventoryBag.CharacterBag, 201350), (InventoryBag.EquippedBag, 201350)],
                removed.OrderBy(row => row.Bag).Select(row => (row.Bag, row.ItemId)));
            Assert.Null(await stores.ConsumeItemAsync(other, 201350, default));
            Assert.Equal(5, (await stores.ListSlotsAsync(other, InventoryBag.CharacterBag, 512, default)).Single(row => row.ItemId == 201350).Quantity);
        }
    }

    [Fact]
    public async Task CharacterCollisionAndInvalidGrantRollbackTheWholeCreation()
    {
        using var database = new SqliteDatabase(Options());
        var stores = new SqliteRepositories(database);
        var kit = new StarterKit([300001, 201001], [300001, 201001]);
        var created = await stores.CreateCharacterAsync("one", "n", kit, default);
        Assert.NotNull(created);
        Assert.Null(await stores.CreateCharacterAsync("two", "N", kit, default));
        Assert.Null(await stores.FindByUsernameAsync("two", default));
        Assert.Null(await stores.CreateCharacterAsync("one", "other", kit, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => stores.CreateCharacterAsync("invalid", "valid",
            new([201001], [201001]), default).AsTask());
        Assert.Null(await stores.FindByUsernameAsync("invalid", default));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
