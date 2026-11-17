using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.MysteryStore;

/// <summary>What a Mysterystore request asks for; the dialect maps each to the build's native request type.</summary>
public enum MysteryStoreAction
{
    /// <summary>Opening the dialog: the client needs the configuration, the countdown and the cards.</summary>
    Open,
    /// <summary>The periodic countdown query the open dialog repeats.</summary>
    Query,
    /// <summary>A confirmed paid replacement of the cards.</summary>
    Refresh,
    /// <summary>Buying one card at its quoted coin price.</summary>
    Purchase,
}

/// <summary>Why a reply is shown; the dialect maps each to the build's own signed status code.</summary>
public enum MysteryStoreStatus
{
    /// <summary>The request was carried out.</summary>
    Done,
    /// <summary>The cards were replaced because the previous selection's time ran out.</summary>
    Rotated,
    /// <summary>The store is not available to this player.</summary>
    Unavailable,
    /// <summary>The token, deadline or catalog no longer match the cards the client shows.</summary>
    ListChanged,
    /// <summary>The selected card was already bought from this selection.</summary>
    AlreadyPurchased,
    /// <summary>The player already owns the item for good, so it cannot be granted again.</summary>
    AlreadyOwned,
    /// <summary>The grant would exceed the item's maximum duration.</summary>
    DurationLimit,
    /// <summary>The grant would exceed the item's life limit.</summary>
    LifeLimit,
    /// <summary>The grant would exceed the item's maximum quantity or the bag.</summary>
    QuantityLimit,
    /// <summary>The requested card is not one of the current selection's cards.</summary>
    InvalidProduct,
    /// <summary>The player owns fewer coins than the quoted price.</summary>
    InsufficientCoins,
    /// <summary>The request disagrees with the server's own record, for example in its echoed price.</summary>
    InvalidRequest,
}

/// <summary>A Mysterystore request with its native echoed token and the type-specific value.</summary>
/// <param name="Action">What the client asks for.</param>
/// <param name="Token">The token of the configuration the client holds, echoed unchanged; zero before any.</param>
/// <param name="Seconds">The remaining seconds a query reports; zero for other actions.</param>
/// <param name="QuotedCoins">The refresh cost or card price the client quotes; zero for open and query.</param>
/// <param name="NativeKey">The Stock or StockSet key of the card to buy; zero for other actions.</param>
public sealed record MysteryStoreRequested(MysteryStoreAction Action, uint Token, ushort Seconds, ushort QuotedCoins,
    int NativeKey) : IMessage;

/// <summary>The configuration section of a reply: the token to echo, the coin item and the refresh cost.</summary>
/// <param name="Token">Nonzero token the client echoes in its next requests.</param>
/// <param name="CoinItemId">The inventory item whose owned count pays for the store.</param>
/// <param name="RefreshCoinCost">The coins a refresh costs.</param>
/// <param name="GroupStockVariants">Whether the client groups Stock variants of one item into one card.</param>
public sealed record MysteryStoreReplyConfig(uint Token, int CoinItemId, ushort RefreshCoinCost, bool GroupStockVariants);

/// <summary>One card of a reply, in the order the purchased mask addresses.</summary>
/// <param name="NativeKey">The card's Stock or StockSet key.</param>
/// <param name="CoinPrice">The card's coin price.</param>
/// <param name="Unknown4">The opaque first row word.</param>
/// <param name="UnknownName">The opaque row text.</param>
public sealed record MysteryStoreReplyOffer(int NativeKey, ushort CoinPrice, uint Unknown4, string UnknownName);

/// <summary>A Mysterystore reply; every section that is present replaces the client's copy of it.</summary>
/// <param name="Action">The request this reply answers.</param>
/// <param name="Status">Why it is shown.</param>
/// <param name="Config">The configuration section, or null to keep the client's.</param>
/// <param name="RemainingSeconds">The countdown section, or null to keep the client's.</param>
/// <param name="Offers">The cards, or null to keep the client's.</param>
/// <param name="PurchasedMask">The bought cards' bits; it is only sent with the cards it indexes.</param>
/// <param name="ReceiptNativeKey">The bought Stock or StockSet key of a successful purchase, otherwise zero.</param>
public sealed record MysteryStoreReplied(MysteryStoreAction Action, MysteryStoreStatus Status,
    MysteryStoreReplyConfig? Config, ushort? RemainingSeconds, IReadOnlyList<MysteryStoreReplyOffer>? Offers,
    uint? PurchasedMask, int ReceiptNativeKey) : IMessage
{
    /// <summary>Gets the cards copied in mask order, or null.</summary>
    public IReadOnlyList<MysteryStoreReplyOffer>? Offers { get; } = Offers is null ? null : Copy(Offers);

    /// <summary>
    /// Gets the purchased mask. The client rebuilds its bought map from the cards of the same reply
    /// (kr-1256 sub_E5F6B0 -> kr-1256 sub_114E600), so a mask without cards would clear it.
    /// </summary>
    public uint? PurchasedMask { get; } = PurchasedMask is not null && Offers is null
        ? throw new ArgumentException("A purchased mask is only meaningful with the cards it indexes.", nameof(PurchasedMask))
        : PurchasedMask;

    /// <summary>Gets the receipt key, which only a successful purchase carries.</summary>
    public int ReceiptNativeKey { get; } = (Action == MysteryStoreAction.Purchase && Status == MysteryStoreStatus.Done)
        != (ReceiptNativeKey > 0)
        ? throw new ArgumentException("Only a successful purchase carries a positive receipt key.", nameof(ReceiptNativeKey))
        : ReceiptNativeKey;

    private static IReadOnlyList<MysteryStoreReplyOffer> Copy(IReadOnlyList<MysteryStoreReplyOffer> offers)
    {
        if (offers.Any(offer => offer is null || offer.NativeKey <= 0 || offer.UnknownName is null))
            throw new ArgumentException("Every card needs a positive key and text.", nameof(offers));
        return Array.AsReadOnly(offers.ToArray());
    }
}
