using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Shop;

/// <summary>
/// Grants a Stock or StockSet of the build's own shop workbook to a player at no charge, whether or not it is on
/// sale, under the same item terms a purchase uses. Game rewards that name their prize by such a key, like the
/// tutorial first-clear gift in MapMeta <c>IsItemAward</c>, go through this service so the shop row stays the only
/// owner of item terms and of the inventory and wallet wire.
/// </summary>
public interface IShopGiftService
{
    /// <summary>Returns whether <paramref name="key"/> is a Stock or StockSet this build can grant.</summary>
    /// <param name="key">A Stock or StockSet id from the build's workbook.</param>
    bool CanGrant(int key);

    /// <summary>
    /// Commits the gift's items and Lucci in one transaction and publishes them to the session the way a purchase
    /// does. Returns false when the item terms refuse the gift (a count or day limit, or an item that cannot be
    /// owned twice); nothing is changed then.
    /// </summary>
    /// <param name="session">The receiving player's game session.</param>
    /// <param name="userNum">The receiving player.</param>
    /// <param name="key">A key for which <see cref="CanGrant"/> is true.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<bool> GrantAsync(ISession session, long userNum, int key, CancellationToken ct);
}
