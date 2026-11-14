using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.ItemEnhancement.Data;

namespace BubbleFighter.Fantasy.ItemEnhancement;

/// <summary>Counts Master weapons from an account's committed Articles, deriving each grade from its stored points.</summary>
internal sealed class FavoriteWeaponProgress(string protocol, FavoriteWeaponCatalog favorite, EnhancementCatalog items,
    IItemEnhancementStore store, InventoryLoadoutScope scope) : IFavoriteWeaponProgress
{
    /// <inheritdoc />
    public string Protocol => protocol;

    /// <inheritdoc />
    public async ValueTask<int> CountMasterWeaponsAsync(long userNum, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        var snapshot = await store.ReadEnhancementInventoryAsync(userNum, scope, ct).ConfigureAwait(false);
        return snapshot.CharacterBag.Count(row => row.ArticleIdentity is not null
            && items.Items.TryGetValue(row.ItemId, out var item) && item.Kind == ItemEnhancementKind.Weapon
            && row.Favorites?.Find(scope.Key) is { } points && favorite.GradeOf(points) == FavoriteGrade.Master);
    }
}