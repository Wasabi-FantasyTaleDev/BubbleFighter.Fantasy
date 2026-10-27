using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Wallet;

namespace BubbleFighter.Fantasy.Contracts.Shop;

/// <summary>The client opened the item shop and asks for its on-sale catalog.</summary>
/// <param name="Token">The cache token the client sent; a build whose request has no body carries 0.</param>
/// <param name="MainCategory">The main tab the client asked to open; 0 when the request carries none.</param>
public sealed record ShopCatalogRequested(long Token, int MainCategory) : IMessage;

/// <summary>The client opened the cash (TC) shop and asks for its on-sale catalog.</summary>
/// <param name="Token">The cache token the client sent, copied back to <c>dword_18E56E8+38</c> when the reply carries data.</param>
public sealed record ShopCashCatalogRequested(long Token) : IMessage;

/// <summary>
/// Publishes the item-shop catalog for one session. The cards, categories and main category are the logic
/// row's selection; the dialect sender applies the build's catalog layout: the kr-1256 family writes the
/// table-stated reply (optionally replacing the token with a content revision), and a legacy build builds the
/// tree its <c>shop.catalog</c> values state, bounds the card vector to the frame budget and picks the echoed
/// main category from the same values.
/// </summary>
/// <param name="Token">The token to echo, or 0 when <see cref="ContentRevisionToken"/> derives it from the body.</param>
/// <param name="MainCategory">The main tab the reply selects.</param>
/// <param name="Cards">The eligible on-sale cards, in the logic row's order.</param>
/// <param name="Categories">The card-linked category tree.</param>
/// <param name="ShopVisibleMains">Every workbook main with <c>shopView != 0</c>, as empty-sub stubs.</param>
/// <param name="ShopVisibleSubcategories">Every subcategory row with <c>shopView != 0</c>.</param>
/// <param name="MainDefaultSubcategoryIds">Resolved <c>shopDefaultSubCategory</c> ids keyed by main id.</param>
public sealed record ShopCatalogPublished(
    long Token,
    int MainCategory,
    IReadOnlyList<ShopCardRow> Cards,
    IReadOnlyList<ShopCategoryRow> Categories,
    IReadOnlyList<ShopCategoryRow> ShopVisibleMains,
    IReadOnlyList<ShopSubcategoryRow> ShopVisibleSubcategories,
    IReadOnlyDictionary<int, int> MainDefaultSubcategoryIds) : IMessage
{
    /// <summary>
    /// Gets whether the echo token is the content revision: the first eight bytes of the SHA-256 of the body
    /// written with revision 0 (kr-1256 <c>sub_9ADA00</c> stores it and the next request sends it back).
    /// </summary>
    public bool ContentRevisionToken { get; init; }

    /// <summary>Gets the diagnostic card-count cap for the reply; zero sends every card.</summary>
    public int CardLimit { get; init; }

    /// <summary>Gets the inclusive encoded catalog payload budget; zero applies no catalog-specific byte limit.</summary>
    public int ByteBudget { get; init; }

    /// <summary>Gets the legacy card-count cap beside the byte budget; zero uses only the budget.</summary>
    public int CardCap { get; init; }
}

/// <summary>Publishes the cash-shop catalog for one session.</summary>
/// <param name="Token">The cache token echoed to <c>dword_18E56E8+38</c>.</param>
/// <param name="Cards">The on-sale cash stock sets.</param>
public sealed record ShopCashCatalogPublished(long Token, IReadOnlyList<ShopCardRow> Cards) : IMessage;

/// <summary>The client asked for one catalog card's full purchase terms.</summary>
/// <param name="Key">The ShopItem or StockSet identity the client named.</param>
public sealed record ShopCardInfoRequested(int Key) : IMessage;

/// <summary>Answers a card-info request with the full card, or the missing-row form.</summary>
/// <param name="Result">1 when <paramref name="Card"/> is present; -1 is the missing-row path.</param>
/// <param name="Card">The catalog row, or null for the default-constructed card.</param>
public sealed record ShopCardInfoReplied(int Result, ShopCardRow? Card) : IMessage;

/// <summary>The client submitted a coupon code from the coupon dialog.</summary>
/// <param name="Code">The typed code, read as an opaque wide string; its meaning is unproven.</param>
public sealed record ShopCouponUseRequested(string Code) : IMessage;

/// <summary>Answers a coupon submission; 1 is the only success value, -1 a listed error code.</summary>
/// <param name="Result">The result word the coupon dialog branches on.</param>
public sealed record ShopCouponUseReplied(int Result) : IMessage;

/// <summary>
/// The coupon dialog's cash probe: the client typed a 17-to-20-character code and the dialog needs the
/// current cash balance before it will enable a purchase.
/// </summary>
/// <param name="Code">The typed code, read as an opaque wide string; its meaning is unproven.</param>
public sealed record ShopCashCheckRequested(string Code) : IMessage;

/// <summary>
/// Answers the coupon dialog's cash probe with the cash balance, and optionally the six-word money snapshot
/// that also refreshes the cached Lucci word the buy pre-check reads.
/// </summary>
/// <param name="Cash">The current cash balance.</param>
/// <param name="MoneyStat">The wallet snapshot to re-publish, or null to answer with the cash word alone.</param>
public sealed record ShopCashCheckReplied(int Cash, PlayerWallet? MoneyStat) : IMessage;
