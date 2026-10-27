using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Wallet;

namespace BubbleFighter.Fantasy.Contracts.Shop;

/// <summary>A request to purchase the exact Stock or StockSet selected in the catalog.</summary>
/// <param name="Key">Catalog purchase identity, distinct from an equipment item id.</param>
/// <param name="IsOrdinaryPurchase">Whether the adapter recognized the ordinary purchase option; other options must be refused.</param>
public sealed record ShopPurchaseRequested(int Key, bool IsOrdinaryPurchase) : IMessage;

/// <summary>The client requested its complete persisted inventory.</summary>
public sealed record ShopInventoryRequested : IMessage;

/// <summary>The client requests a fresh balance before enabling its cash purchase dialog.</summary>
public sealed record ShopBalanceRequested : IMessage;

/// <summary>Publishes current balances without replacing inventory or changing its time origin.</summary>
/// <param name="Player">Authenticated identity and current level.</param>
/// <param name="Wallet">Stored balances, or the configured seed before the first transaction.</param>
public sealed record ShopBalanceUpdated(PlayerIdentity Player, PlayerWallet Wallet) : IMessage;

/// <summary>Publishes a full inventory or committed item changes together with the corresponding wallet.</summary>
/// <param name="Player">Authenticated identity and current level.</param>
/// <param name="Wallet">Balance snapshot for the same player.</param>
/// <param name="Slots">Complete bag when FullInventory is true, otherwise the committed grants.</param>
/// <param name="FullInventory">Whether the client must replace its bag and establish a new time baseline.</param>
public sealed record ShopStateUpdated(PlayerIdentity Player, PlayerWallet Wallet,
    IReadOnlyList<InventorySlotRecord> Slots, bool FullInventory) : IMessage;

/// <summary>Completes a purchase dialog after a committed state publication or a refusal.</summary>
/// <param name="Key">The exact purchase identity to echo.</param>
/// <param name="Succeeded">True only after the transaction and state publication succeeded.</param>
public sealed record ShopPurchaseReplied(int Key, bool Succeeded) : IMessage
{
    /// <summary>
    /// Gets the reply text of the refusal the modern dialog builds its failure box from; the success path and
    /// the legacy two-word reply carry it empty. Its display purpose is unproven.
    /// </summary>
    public string ReplyText { get; init; } = string.Empty;
}
