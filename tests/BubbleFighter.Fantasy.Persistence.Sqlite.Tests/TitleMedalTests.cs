using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Titles;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>Checks durable title acquisition, receipt replay and complete metadata rollback in SQLite.</summary>
public sealed class TitleMedalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-title-" + Guid.NewGuid().ToString("N"));
    private SqliteOptions Options => new() { DatabasePath = Path.Combine(_directory, "save.sqlite") };
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [400001, 300100, 301001]);

    /// <summary>Reading a missing target or scope neither throws nor materializes player or medal state.</summary>
    [Fact]
    public async Task ReadingUnknownPlayerDoesNotCreateState()
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        Assert.Empty(await store.ReadAsync(1, Scope.Key, default));
        Assert.Null(database.State(1));
        var user = await Seed(store);
        Assert.Empty(await store.ReadAsync(user, "kr-812", default));
        Assert.Single(database.RequireState(user).TitleMedals);
    }

    /// <summary>Claimed inventory, both metadata families, all medal costs and grant deduplication survive reopen together.</summary>
    [Fact]
    public async Task ClaimedTitleAndMedalReceiptsSurviveReopenWithoutDuplicateDebits()
    {
        long user;
        TitleClaimResult committed;
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            user = await Seed(store);
            committed = Assert.IsType<TitleClaimResult>(await store.TryClaimAsync(Claim(user), default));
            Assert.Equal(new[] { new TitleMedalCount(30007, 10), new TitleMedalCount(30008, 5) }, committed.Medals);
            Assert.Equal(2, committed.Inventory.EquippedBag.Count);
            Assert.NotNull(committed.Inventory.EquippedBag.Single(row => row.ItemId == 300100).ArticleIdentity);
            Assert.NotNull(committed.Inventory.EquippedBag.Single(row => row.ItemId == 301001).PetIdentity);
            Assert.Equal("3", database.RawScalar("SELECT entitlement_id FROM pet_identity_allocator WHERE singleton=1"));
            Assert.NotNull(committed.GrantedItem.ArticleIdentity);
            Assert.Equal(13L, database.RawScalar("PRAGMA user_version"));
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            var inventory = await store.ReadEnhancementInventoryAsync(user, Scope, default);
            Assert.Equal(committed.Inventory.CharacterBag, inventory.CharacterBag);
            Assert.Equal(committed.Inventory.EquippedBag, inventory.EquippedBag);
            Assert.Equal(committed.Medals, await store.ReadAsync(user, Scope.Key, default));
            Assert.False((await store.GrantAsync(Grant(user), default)).Applied);
            Assert.Null(await store.TryClaimAsync(Claim(user), default));
            Assert.Null(await store.TryClaimAsync(Claim(user) with { ExpectedTitle = committed.GrantedItem }, default));
            Assert.Empty(database.RequireState(user).Purchases);
            Assert.Equal(3, database.RequireState(user).LastPurchaseSlotKey);
            Assert.Empty(await store.ReadAsync(user, "kr-812", default));
        }
    }

    /// <summary>Cancellation after writing the coupled snapshot rolls back medals, ownership and the shared Article allocator.</summary>
    [Fact]
    public async Task CancellationAfterClaimWriteRollsBackEveryEffect()
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        var user = await Seed(store);
        var before = await store.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default);
        using var cancellation = new CancellationTokenSource();
        Assert.IsType<SqliteConnection>(database.Orm.Ado.Connection).CreateFunction("cancel_title_write", () =>
        {
            cancellation.Cancel();
            return 0;
        });
        database.Write((db, tx) => db.RawExecute(
            "CREATE TRIGGER cancel_title AFTER UPDATE OF state ON players BEGIN SELECT cancel_title_write(); END;", tx), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryClaimAsync(Claim(user), cancellation.Token).AsTask());
        database.Write((db, tx) => db.RawExecute("DROP TRIGGER cancel_title;", tx), default);
        Assert.Equal(before, await store.ListSlotsAsync(user, InventoryBag.CharacterBag, 512, default));
        Assert.Equal(30, (await store.ReadAsync(user, Scope.Key, default))[0].Count);
        Assert.Equal("0", database.RawScalar("SELECT entitlement_id FROM pet_identity_allocator WHERE singleton=1"));
        Assert.Equal(0, database.RequireState(user).LastPurchaseSlotKey);
        Assert.NotNull(await store.TryClaimAsync(Claim(user), default));
    }

    /// <summary>
    /// Receipts are keyed rows outside the player snapshot: a conflicting replay changes nothing, new events keep applying,
    /// and every applied event still deduplicates after reopening.
    /// </summary>
    [Fact]
    public async Task KeyedReceiptsDeduplicateAcrossReopenWithoutGrowingTheSnapshot()
    {
        var options = Options;
        long user;
        using (var database = new SqliteDatabase(options))
        {
            var store = new SqliteRepositories(database);
            user = await Seed(store);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.GrantAsync(Grant(user) with { Source = "different" }, default).AsTask());
            for (var index = 0; index < 50; index++)
                Assert.True((await store.GrantAsync(new TitleMedalGrantCommand(user, Scope.Key, "test", "event-" + index, [new(30007, 1)]), default)).Applied);
            Assert.DoesNotContain("event-", System.Text.Json.JsonSerializer.Serialize(database.RequireState(user)), StringComparison.Ordinal);
        }
        using (var database = new SqliteDatabase(options))
        {
            var store = new SqliteRepositories(database);
            Assert.False((await store.GrantAsync(Grant(user), default)).Applied);
            Assert.False((await store.GrantAsync(new TitleMedalGrantCommand(user, Scope.Key, "test", "event-7", [new(30007, 1)]), default)).Applied);
            Assert.Equal(80, (await store.ReadAsync(user, Scope.Key, default))[0].Count);
        }
    }

    /// <summary>Schema 13 refuses bonus acquisitions before mutation while ordinary title claims and medal reads remain available.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedAcquisitionBonusesPreserveOrdinaryTitles(bool itemBonus)
    {
        long user;
        TitleClaimResult committed;
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            user = await Seed(store);
            Assert.False(store.SupportsAcquisitionBonuses);
            var before = await store.ReadEnhancementInventoryAsync(user, Scope, default);
            var balances = await store.ReadAsync(user, Scope.Key, default);
            var command = itemBonus
                ? Claim(user) with { BonusGrants = [new(300100, InventoryGrantKind.Permanent, 1, 1, false)] }
                : Claim(user) with { BonusMedals = [new(30009, 2)] };
            await Assert.ThrowsAsync<NotSupportedException>(() => store.TryClaimAsync(command, default).AsTask());
            var after = await store.ReadEnhancementInventoryAsync(user, Scope, default);
            Assert.Equal(before.CharacterBag, after.CharacterBag);
            Assert.Equal(before.EquippedBag, after.EquippedBag);
            Assert.Equal(balances, await store.ReadAsync(user, Scope.Key, default));
            Assert.Equal("0", database.RawScalar("SELECT entitlement_id FROM pet_identity_allocator WHERE singleton=1"));
            Assert.Equal(13L, database.RawScalar("PRAGMA user_version"));
            committed = Assert.IsType<TitleClaimResult>(await store.TryClaimAsync(Claim(user), default));
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            Assert.Equal(committed.Medals, await store.ReadAsync(user, Scope.Key, default));
            Assert.Equal(committed.Inventory.CharacterBag,
                (await store.ReadEnhancementInventoryAsync(user, Scope, default)).CharacterBag);
        }
    }

    /// <summary>Existing ordinary and pet selection paths commit Article metadata only with a valid selection.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SelectionMetadataAndAllocatorAreOneTransaction(bool petSelection, bool refuse)
    {
        using var database = new SqliteDatabase(Options);
        var store = new SqliteRepositories(database);
        var user = await Seed(store);
        var pets = new PetInitializationCommand(user, Scope, [301001], new(0, 0, Now, null, null, null), Now);
        var initial = await store.InitializePetsAsync(pets, default);
        var articles = new ArticleInitializationCommand(user, Scope, [300100], Now);
        var target = new PetEquipmentTarget(301001, initial.CharacterBag.Single(row => row.ItemId == 301001).PetIdentity!);
        if (refuse) target = target with { Identity = new(999, 100000999) };
        var result = petSelection
            ? await store.SelectPetAsync(new(user, Scope, [], target, true, Now) { Initialization = pets, ArticleInitialization = articles }, default)
            : await store.UpdateLoadoutSelectionSnapshotAsync(user, Scope, [], refuse ? 400001 : 300100, true, default, pets, articles);
        Assert.Equal(refuse, result is null);
        Assert.Equal(refuse ? "1" : "2", database.RawScalar("SELECT entitlement_id FROM pet_identity_allocator WHERE singleton=1"));
        Assert.Equal(refuse, (await store.ReadEnhancementInventoryAsync(user, Scope, default)).CharacterBag
            .Single(row => row.ItemId == 300100).ArticleIdentity is null);
    }

    /// <summary>An automatic grant persists one permanent row without touching the medals and refuses a replay after reopening.</summary>
    [Fact]
    public async Task AutomaticGrantPersistsWithoutMedalDebitsAndRefusesReplay()
    {
        long user;
        TitleClaimCommand command;
        TitleClaimResult committed;
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            user = await Seed(store);
            command = new(user, Scope, 400001, 1200001, [], new(400001, InventoryGrantKind.Permanent, 1, 1, false), null, Now)
                { IsAutomaticGrant = true };
            committed = Assert.IsType<TitleClaimResult>(await store.TryClaimAsync(command, default));
            Assert.Equal(new[] { new TitleMedalCount(30007, 30), new TitleMedalCount(30008, 25) }, committed.Medals);
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            var inventory = await store.ReadEnhancementInventoryAsync(user, Scope, default);
            Assert.Equal(committed.Inventory.CharacterBag, inventory.CharacterBag);
            Assert.Equal(committed.Medals, await store.ReadAsync(user, Scope.Key, default));
            Assert.Null(await store.TryClaimAsync(command, default));
            Assert.Null(await store.TryClaimAsync(command with { ExpectedTitle = committed.GrantedItem }, default));
        }
    }
    private static TitleClaimCommand Claim(long user) => new(user, Scope, 400001, 1200001,
        [new(30007, 20), new(30008, 20)], new(400001, InventoryGrantKind.Permanent, 1, 1, false), null, Now)
    {
        ArticleInitialization = new(user, Scope, [300100, 400001], Now),
        PetInitialization = new(user, Scope, [301001], new(0, 0, Now, null, null, null), Now),
    };

    private static TitleMedalGrantCommand Grant(long user) => new(user, Scope.Key, "test", "initial", [new(30007, 30), new(30008, 25)]);

    private static async Task<long> Seed(SqliteRepositories store)
    {
        var user = (await store.CreateAsync("title-account", "title-player", default)).UserNum;
        InventorySlotRecord[] rows = [
            new(user, InventoryBag.CharacterBag, 1, 300100, 1, 0) { Lifetime = InventoryLifetime.Permanent },
            new(user, InventoryBag.CharacterBag, 2, 301001, 1, 0) { Lifetime = InventoryLifetime.Permanent },
        ];
        await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        await store.GrantAsync(Grant(user), default);
        return user;
    }

    /// <summary>Removes this test's temporary save after all owned connections have closed.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
