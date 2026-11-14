using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.Memory.Tests;

/// <summary>Checks Favorite weapon point metadata: contract validation, JSON, external-write refusal and atomic awards.</summary>
public sealed class ItemFavoriteTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly InventoryLoadoutScope Scope = new("kr-1256", [300100, 190106]);

    /// <summary>Points round trip through JSON and compare by value; negative, oversized and repeated scopes are refused.</summary>
    [Fact]
    public void ContractValidatesBoundsAndRoundTripsJson()
    {
        var favorites = ItemFavorites.Empty.With("kr-1256", 119).With("cn-774", uint.MaxValue);
        Assert.Equal(favorites, JsonSerializer.Deserialize<ItemFavorites>(JsonSerializer.Serialize(favorites)));
        Assert.Equal("{\"cn-774\":4294967295,\"kr-1256\":119}", JsonSerializer.Serialize(favorites));
        Assert.Null(favorites.Find("kr-812"));
        Assert.Throws<InvalidDataException>(() => favorites.With("kr-1256", -1));
        Assert.Throws<InvalidDataException>(() => favorites.With("kr-1256", uint.MaxValue + 1L));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ItemFavorites>("{\"kr-1256\":1,\"kr-1256\":2}"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ItemFavorites>("{\"kr-1256\":1.5}"));
        Assert.Throws<InvalidDataException>(() => favorites.Validate(1));
    }

    /// <summary>An ordinary replace or append cannot manufacture, change or erase stored Favorite points.</summary>
    [Fact]
    public void ExternalWritesCannotManufactureOrChangeFavorites()
    {
        var owned = new InventorySlotRecord(1, InventoryBag.CharacterBag, 1, 300100, 1, 0) { Lifetime = InventoryLifetime.Permanent };
        var forged = owned with { Favorites = ItemFavorites.Empty.With(Scope.Key, 30) };
        Assert.Throws<InvalidDataException>(() => InventoryArticles.ValidateExternalWrite(1, [owned], [forged]));
        Assert.Throws<InvalidDataException>(() => InventoryArticles.ValidateExternalWrite(1, [forged], [owned]));
        Assert.Throws<InvalidDataException>(() => InventoryArticles.ValidateExternalWrite(1, [forged],
            [forged with { Favorites = ItemFavorites.Empty.With(Scope.Key, 31) }]));
    }

    /// <summary>A successful weapon enhancement commits its points with the revision on both bags; a failed one awards none.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnhancementAwardsPointsOnlyOnSuccessAtomically(bool success)
    {
        var store = new MemoryInventoryRepository(new(1));
        InventorySlotRecord[] rows = [
            new(1, InventoryBag.CharacterBag, 1, 300100, 1, 0x40) { Lifetime = InventoryLifetime.Permanent },
            new(1, InventoryBag.CharacterBag, 2, 190106, 4, 0),
        ];
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(1, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        var snapshot = await store.InitializeArticlesAsync(new(1, Scope, [300100], Now), default);
        var command = new ItemEnhancementCommand([snapshot.CharacterBag.Single(row => row.ItemId == 300100)], Scope,
            [new(snapshot.CharacterBag.Single(row => row.ItemId == 190106), 2)], ItemEnhancementKind.Weapon, success, Now)
        { FavoritePoints = 30 };
        var result = Assert.IsType<InventorySnapshot>(await store.TryEnhanceAsync(command, default));
        foreach (var bag in new[] { result.CharacterBag, result.EquippedBag })
        {
            Assert.Equal(2, bag.Single(row => row.ItemId == 190106).Quantity);
            Assert.Equal(success ? 30L : null, bag.Single(row => row.ItemId == 300100).Favorites?.Find(Scope.Key));
        }
        Assert.Equal(result.CharacterBag, (await store.ReadEnhancementInventoryAsync(1, Scope, default)).CharacterBag);
        Assert.Null(await store.TryEnhanceAsync(command, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => (command with { FavoritePoints = -1 }).Validate());
    }

    /// <summary>A modification award commits with the installed group on both bags, deletion awards nothing, and a replay awards nothing twice.</summary>
    [Fact]
    public async Task ModificationAwardsPointsWithTheGroupOnceAndDeletionAwardsNone()
    {
        var table = new MemoryPlayerTable(1);
        var store = new MemoryInventoryRepository(table);
        await new MemoryPlayerWalletRepository(table).GetOrCreateAsync(new(1, 1000, 50), default);
        InventorySlotRecord[] rows = [
            new(1, InventoryBag.CharacterBag, 1, 300100, 1, 0x40) { Lifetime = InventoryLifetime.Permanent },
            new(1, InventoryBag.CharacterBag, 2, 190106, 4, 0),
        ];
        await store.ReplaceSlotsAsync(1, InventoryBag.CharacterBag, rows, default);
        await store.ReplaceSlotsAsync(1, InventoryBag.EquippedBag, rows.Select(row => row with { Bag = InventoryBag.EquippedBag }).ToArray(), default);
        var snapshot = await store.InitializeArticlesAsync(new(1, Scope, [300100], Now), default);
        var apply = new ItemModificationCommand(snapshot.CharacterBag.Single(row => row.ItemId == 300100), Scope,
            [new(snapshot.CharacterBag.Single(row => row.ItemId == 190106), 1)], new(101, 160, -40, 0, 1), null, 0, Now)
        { FavoritePoints = 12 };
        var applied = Assert.IsType<ItemModificationResult>(await store.TryModifyAsync(apply, default));
        foreach (var bag in new[] { applied.Inventory.CharacterBag, applied.Inventory.EquippedBag })
        {
            var target = bag.Single(row => row.ItemId == 300100);
            Assert.Equal(12L, target.Favorites?.Find(Scope.Key));
            Assert.Equal(new ItemModificationState(1, [new(101, 160, -40, 0, 1)]), target.Modifications!.Find(Scope.Key));
        }
        Assert.Null(await store.TryModifyAsync(apply, default));
        Assert.Equal(12L, (await store.ReadEnhancementInventoryAsync(1, Scope, default)).CharacterBag.Single(row => row.ItemId == 300100)
            .Favorites?.Find(Scope.Key));
        var second = new ItemModificationCommand(applied.Inventory.CharacterBag.Single(row => row.ItemId == 300100), Scope, [],
            new(102, 100, -50, 1, 2), null, 0, Now) { FavoritePoints = 6 };
        var again = Assert.IsType<ItemModificationResult>(await store.TryModifyAsync(second, default));
        Assert.Equal(18L, again.Inventory.CharacterBag.Single(row => row.ItemId == 300100).Favorites?.Find(Scope.Key));
        var deletion = new ItemModificationCommand(again.Inventory.CharacterBag.Single(row => row.ItemId == 300100), Scope, [], null, 0, 0, Now);
        var deleted = Assert.IsType<ItemModificationResult>(await store.TryModifyAsync(deletion, default));
        Assert.Equal(18L, deleted.Inventory.CharacterBag.Single(row => row.ItemId == 300100).Favorites?.Find(Scope.Key));
        Assert.Throws<ArgumentException>(() => (deletion with { FavoritePoints = 6 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (apply with { FavoritePoints = -1 }).Validate());
    }}