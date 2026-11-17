namespace BubbleFighter.Fantasy.Contracts.Clans;

/// <summary>
/// The clan creation cards of the build's client: the items that found a clan and how many members the clan
/// they found may hold. The client opens its create dialog only for an owned item of kind 22 and sends that
/// item's id and its item-table parameter with the request (kr-1256 sub_51BA40 into kr-1256 sub_5C3740); its
/// ShopItem table gives every such card its capacity as that parameter (the card named for 30 members has 30).
/// </summary>
public interface IClanCreationCards
{
    /// <summary>Returns the member capacity of a clan founded with the item, or false when the item is not a creation card.</summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="capacity">The capacity of the clan the card founds.</param>
    bool TryGetCapacity(int itemId, out int capacity);
}
