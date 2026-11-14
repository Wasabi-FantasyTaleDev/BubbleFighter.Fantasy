using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.Persistence.MySql.Tests;

/// <summary>Checks Favorite point mapping and validation without opening a database connection.</summary>
public sealed class ItemFavoriteStorageTests
{
    private static readonly InventorySlotRecord Target = new(1, InventoryBag.CharacterBag, 1, 300100, 1, 0)
    { Lifetime = InventoryLifetime.Permanent, ArticleIdentity = new(9, 100000008) };

    /// <summary>Points survive ORM mapping beside other metadata, and a slot without progress stores SQL NULL.</summary>
    [Fact]
    public void EntityRoundTripPreservesFavoritePoints()
    {
        var slot = Target with { Favorites = ItemFavorites.Empty.With("kr-1256", 120).With("cn-774", 0) };
        var row = MySqlInventoryRepository.ToRow(slot, 0);
        Assert.Equal("{\"cn-774\":0,\"kr-1256\":120}", row.FavoritesJson);
        Assert.Equal(slot, MySqlInventoryRepository.ToRecord(row));
        Assert.Null(MySqlInventoryRepository.ToRow(Target, 0).FavoritesJson);
    }

    /// <summary>Corrupt stored points fail loudly instead of becoming zero progress.</summary>
    [Fact]
    public void StoredMalformedFavoritesAreRejected()
    {
        var row = MySqlInventoryRepository.ToRow(Target, 0);
        foreach (var json in new[] { "null", "{\"kr-1256\":-1}", "{\"kr-1256\":4294967296}" })
        {
            row.FavoritesJson = json;
            Assert.ThrowsAny<Exception>(() => MySqlInventoryRepository.ToRecord(row));
        }
        row.FavoritesJson = "{\"kr-1256\":\"7\"}";
        Assert.Throws<JsonException>(() => MySqlInventoryRepository.ToRecord(row));
    }

    /// <summary>The shared row mutation the MySQL store runs adds modification points beside the group, rejects stale replay and awards nothing for deletion.</summary>
    [Fact]
    public void ModificationMutationAddsPointsOnceAndRefusesReplay()
    {
        var scope = new InventoryLoadoutScope("kr-1256", [300100, 190106]);
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        var material = new InventorySlotRecord(1, InventoryBag.CharacterBag, 2, 190106, 4, 0);
        var target = Target;
        var command = new ItemModificationCommand(target, scope, [new(material, 1)], new(101, 160, -40, 0, 0), null, 0, now) { FavoritePoints = 24 };
        var rows = command.Apply([target, material], new InventoryLoadoutState(null, 0, null))!;
        Assert.Equal(24L, rows.Single(row => row.ItemId == 300100).Favorites?.Find(scope.Key));
        Assert.Equal(3, rows.Single(row => row.ItemId == 190106).Quantity);
        Assert.Null(command.Apply(rows, new InventoryLoadoutState(null, 0, null)));
        var deletion = new ItemModificationCommand(rows.Single(row => row.ItemId == 300100), scope, [], null, 0, 0, now);
        Assert.Equal(24L, deletion.Apply(rows, new InventoryLoadoutState(null, 0, null))!.Single(row => row.ItemId == 300100).Favorites?.Find(scope.Key));
    }}