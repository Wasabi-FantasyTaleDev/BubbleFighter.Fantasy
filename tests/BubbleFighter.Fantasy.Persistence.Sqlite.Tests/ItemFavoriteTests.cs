using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Sqlite.Tests;

/// <summary>Checks that Favorite weapon points commit with an enhancement and survive reopening in SQLite schema 13.</summary>
public sealed class ItemFavoriteTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bff-favorite-" + Guid.NewGuid().ToString("N"));
    private SqliteOptions Options => new() { DatabasePath = Path.Combine(_directory, "save.sqlite") };
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [300100, 190106]);

    /// <summary>Successful points persist on both bags after reopening, and a failed attempt stores none.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PointsCommitWithTheEnhancementAndSurviveReopen(bool success)
    {
        long user;
        InventorySnapshot saved;
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            user = (await store.CreateAsync("favorite-account", "favorite-player", default)).UserNum;
            InventorySlotRecord[] rows = [
                new(user, InventoryBag.CharacterBag, 1, 300100, 1, 0x40) { Lifetime = InventoryLifetime.Permanent },
                new(user, InventoryBag.CharacterBag, 2, 190106, 4, 0),
            ];
            await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
            await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
            var initial = await store.InitializeArticlesAsync(new(user, Scope, [300100], Now), default);
            saved = Assert.IsType<InventorySnapshot>(await store.TryEnhanceAsync(new([initial.CharacterBag.Single(row => row.ItemId == 300100)], Scope,
                [new(initial.CharacterBag.Single(row => row.ItemId == 190106), 2)], ItemEnhancementKind.Weapon, success, Now)
            { FavoritePoints = 30 }, default));
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            Assert.Equal(13L, database.RawScalar("PRAGMA user_version"));
            var reopened = await store.ReadEnhancementInventoryAsync(user, Scope, default);
            Assert.Equal(saved.CharacterBag, reopened.CharacterBag);
            Assert.Equal(saved.EquippedBag, reopened.EquippedBag);
            foreach (var bag in new[] { reopened.CharacterBag, reopened.EquippedBag })
                Assert.Equal(success ? 30L : null, bag.Single(row => row.ItemId == 300100).Favorites?.Find(Scope.Key));
        }
    }

    /// <summary>A modification award persists with the installed group on both bags across reopening, and a replay awards nothing.</summary>
    [Fact]
    public async Task ModificationPointsPersistWithTheGroupAcrossReopen()
    {
        long user;
        ItemModificationCommand apply;
        InventorySnapshot saved;
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            user = (await store.CreateAsync("favorite-mod-account", "favorite-mod-player", default)).UserNum;
            await store.GetOrCreateAsync(new(user, 1000, 50), default);
            InventorySlotRecord[] rows = [
                new(user, InventoryBag.CharacterBag, 1, 300100, 1, 0x40) { Lifetime = InventoryLifetime.Permanent },
                new(user, InventoryBag.CharacterBag, 2, 190106, 4, 0),
            ];
            await store.ReplaceSlotsAsync(user, InventoryBag.CharacterBag, rows, default);
            await store.ReplaceSlotsAsync(user, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
            var initial = await store.InitializeArticlesAsync(new(user, Scope, [300100], Now), default);
            apply = new(initial.CharacterBag.Single(row => row.ItemId == 300100), Scope,
                [new(initial.CharacterBag.Single(row => row.ItemId == 190106), 1)], new(101, 160, -40, 0, 2), null, 0, Now)
            { FavoritePoints = 6 };
            saved = Assert.IsType<ItemModificationResult>(await store.TryModifyAsync(apply, default)).Inventory;
        }
        using (var database = new SqliteDatabase(Options))
        {
            var store = new SqliteRepositories(database);
            var reopened = await store.ReadEnhancementInventoryAsync(user, Scope, default);
            Assert.Equal(saved.CharacterBag, reopened.CharacterBag);
            Assert.Equal(saved.EquippedBag, reopened.EquippedBag);
            foreach (var bag in new[] { reopened.CharacterBag, reopened.EquippedBag })
                Assert.Equal(6L, bag.Single(row => row.ItemId == 300100).Favorites?.Find(Scope.Key));
            Assert.Null(await store.TryModifyAsync(apply, default));
            Assert.Equal(6L, (await store.ReadEnhancementInventoryAsync(user, Scope, default)).CharacterBag.Single(row => row.ItemId == 300100)
                .Favorites?.Find(Scope.Key));
        }
    }
    /// <summary>Removes this test's temporary save after every owned connection has been disposed.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}