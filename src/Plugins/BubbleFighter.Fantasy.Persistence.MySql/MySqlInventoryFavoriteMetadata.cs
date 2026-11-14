using System.Text.Json;
using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.MySql;

public sealed partial class MySqlInventoryRepository
{
    private static ItemFavorites? DecodeFavorites(string? json)
    {
        if (json is null) return null;
        var favorites = JsonSerializer.Deserialize<ItemFavorites>(json)
            ?? throw new InvalidDataException("Stored Favorite weapon points cannot be JSON null.");
        favorites.Validate();
        return favorites;
    }

    private static string? EncodeFavorites(ItemFavorites? favorites)
    {
        if (favorites is null) return null;
        favorites.Validate();
        return JsonSerializer.Serialize(favorites);
    }
}