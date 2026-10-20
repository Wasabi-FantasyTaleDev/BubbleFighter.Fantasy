namespace BubbleFighter.Fantasy.Contracts.Shop;

/// <summary>Which shop-open C2S produced a stored token.</summary>
public enum ShopKind
{
    /// <summary>C2S 801 / S2C 800 item shop.</summary>
    ItemShop = 0,

    /// <summary>C2S 802 / S2C 801 cash shop.</summary>
    CashShop = 1,
}

/// <summary>One remembered shop-open. Catalog rows are supplied separately and are not persisted here.</summary>
/// <param name="UserNum">Player id when known; 0 when the session has no proven identity.</param>
/// <param name="Token">Token echoed on the matching S2C open.</param>
/// <param name="ShopType">C2S 801 <c>unknown4</c>, or 0 for cash-shop open.</param>
/// <param name="Kind">Which open packet was handled.</param>
public sealed record ShopOpenRecord(long UserNum, long Token, int ShopType, ShopKind Kind);

/// <summary>Persists the last shop-open per player through the configured storage provider.</summary>
public interface IShopSessionStore
{
    /// <summary>Records an open, replacing the previous one for this player. Player id zero is ignored.</summary>
    /// <param name="record">The open to remember.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask RememberOpenAsync(ShopOpenRecord record, CancellationToken ct);
    /// <summary>Returns the most recent stored open, or null for an untracked or anonymous player.</summary>
    /// <param name="userNum">Player identity, or zero for an anonymous session; negative identities are invalid.</param>
    /// <param name="ct">Cancellation token, observed even for anonymous players.</param>
    ValueTask<ShopOpenRecord?> FindLastOpenAsync(long userNum, CancellationToken ct);
}
