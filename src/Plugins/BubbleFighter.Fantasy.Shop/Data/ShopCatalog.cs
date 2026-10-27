using BubbleFighter.Fantasy.Contracts.Shop;

namespace BubbleFighter.Fantasy.Shop.Data;



/// <summary>A priced purchase resolved from an exact Stock or StockSet key, never an equipment item id.</summary>
/// <param name="Key">The request key echoed by S2C 816.</param>
/// <param name="StockSetId">The purchased set, or 0 for an individual stock.</param>
/// <param name="Name">The catalog display name.</param>
/// <param name="MoneyType">The ShopItem or StockSet currency.</param>
/// <param name="Price">The selected Stock.sellPrice or StockSet.sellPrice.</param>
/// <param name="Stocks">Every stock granted by this purchase.</param>
public sealed record ShopPurchaseOffer(int Key, int StockSetId, string Name, string MoneyType, int Price,
    IReadOnlyList<ShopStockRow> Stocks);

/// <summary>What one Stock or StockSet grants when it is given rather than sold.</summary>
/// <param name="Key">The Stock or StockSet id.</param>
/// <param name="StockSetId">The set, or 0 for a single Stock.</param>
/// <param name="Lucci">The set's Lucci, or 0.</param>
/// <param name="Stocks">The stocks whose items are granted; empty for a Lucci-only set.</param>
internal sealed record ShopGift(int Key, int StockSetId, int Lucci, IReadOnlyList<ShopStockRow> Stocks);



/// <summary>In-memory shop catalog built from <c>Config/ShopItem2.xml</c>.</summary>
public sealed class ShopCatalog
{
    /// <summary>Creates a catalog. All collections must already be bounded.</summary>
    /// <param name="sourcePath">The file that was loaded.</param>
    /// <param name="cards">On-sale stocks and stock sets.</param>
    /// <param name="categories">Shop-visible main categories.</param>
    /// <param name="shopVisibleSubcategories">
    /// Every SubCategory row with <c>shopView != 0</c>. Card-linked trees omit the
    /// main-tab default rows (<c>All_*</c> / <c>shopDefaultSubCategory</c>); id-400
    /// reattaches them before <c>gspRepEnterShop</c> so <c>sub_8E4BB0</c> can resolve
    /// pageType at subcategory +40.
    /// </param>
    /// <param name="mainDefaultSubcategoryIds">
    /// Optional <c>MainCategory.shopDefaultSubCategory</c> resolved to subcategory ids,
    /// keyed by main id. Includes shop-visible mains that have no on-sale cards.
    /// </param>
    /// <param name="shopVisibleMains">
    /// Every MainCategory with <c>shopView != 0</c> as an empty-sub stub. id-400,
    /// cn-279, and cn-324 must still emit main 1000 when it has no cards so the
    /// client's open tab id stays in the tab list (<c>sub_8E1F40</c> /
    /// <c>sub_66B830</c> / <c>sub_7F82B0</c>).
    /// </param>
    public ShopCatalog(
        string sourcePath,
        IReadOnlyList<ShopCardRow> cards,
        IReadOnlyList<ShopCategoryRow> categories,
        IReadOnlyList<ShopSubcategoryRow>? shopVisibleSubcategories = null,
        IReadOnlyDictionary<int, int>? mainDefaultSubcategoryIds = null,
        IReadOnlyList<ShopCategoryRow>? shopVisibleMains = null)
    {
        SourcePath = sourcePath;
        Cards = cards;
        Categories = categories;
        ShopVisibleSubcategories = shopVisibleSubcategories ?? [];
        MainDefaultSubcategoryIds = mainDefaultSubcategoryIds ?? new Dictionary<int, int>();
        ShopVisibleMains = shopVisibleMains ?? [];
        foreach (var card in cards)
        {
            if (card.StockSetId != 0)
                ByStockSetId.Add(card.StockSetId, card);
            if (card.PrimaryItemId != 0)
                ByItemId.TryAdd(card.PrimaryItemId, card);
            foreach (var stock in card.Stocks)
            {
                if (card.StockSetId == 0)
                    ByStockId.Add(stock.StockId, card);
                if (stock.ItemId != 0)
                    ByItemId.TryAdd(stock.ItemId, card);
            }
        }
    }

    /// <summary>Gets the catalog file path.</summary>
    public string SourcePath { get; }

    /// <summary>Gets the on-sale stocks and stock sets displayed as cards.</summary>
    public IReadOnlyList<ShopCardRow> Cards { get; }

    /// <summary>Gets shop-visible categories.</summary>
    public IReadOnlyList<ShopCategoryRow> Categories { get; }

    /// <summary>Gets every shop-visible subcategory row from the workbook.</summary>
    public IReadOnlyList<ShopSubcategoryRow> ShopVisibleSubcategories { get; }

    /// <summary>
    /// Gets every SubCategory row of the workbook in sheet order, including rows with <c>shopView</c> 0 that cards
    /// still use (kr-1256 402 Characters, 801 Packages); the id-range tree needs them.
    /// </summary>
    public IReadOnlyList<ShopSubcategoryRow> AllSubcategories { get; init; } = [];

    /// <summary>Gets resolved <c>shopDefaultSubCategory</c> ids keyed by main category id.</summary>
    public IReadOnlyDictionary<int, int> MainDefaultSubcategoryIds { get; }

    /// <summary>
    /// Gets every shop-visible main as an empty-sub stub (workbook order). Card-linked
    /// <see cref="Categories"/> may omit mains that have no on-sale cards.
    /// </summary>
    public IReadOnlyList<ShopCategoryRow> ShopVisibleMains { get; }

    /// <summary>Gets cards whose <c>moneyType</c> is neither cash variant (item shop / lucci).</summary>
    public IEnumerable<ShopCardRow> ItemShopCards =>
        Cards.Where(c => !IsCashMoney(c.MoneyType));

    /// <summary>
    /// Gets cards priced in a real-money currency (TC shop): <c>cash</c>, plus the
    /// CN workbooks' cashCN, which prices the same dialog in the CN cash wallet.
    /// </summary>
    public IEnumerable<ShopCardRow> CashShopCards =>
        Cards.Where(c => IsCashMoney(c.MoneyType));

    private static bool IsCashMoney(string money) =>
        string.Equals(money, "cash", StringComparison.OrdinalIgnoreCase)
        || string.Equals(money, "cashCN", StringComparison.OrdinalIgnoreCase);

    internal Dictionary<int, ShopCardRow> ByStockSetId { get; } = new();
    internal Dictionary<int, ShopCardRow> ByItemId { get; } = new();
    internal Dictionary<int, ShopCardRow> ByStockId { get; } = new();

    /// <summary>
    /// Gets every Stock and StockSet of the workbook that grants something, by key, on sale or not; rewards such as
    /// the tutorial first-clear gift name off-sale rows.
    /// </summary>
    internal IReadOnlyDictionary<int, ShopGift> Gifts { get; init; } = new Dictionary<int, ShopGift>();

    /// <summary>Finds a card by stock-set id, item id, or stock id.</summary>
    /// <param name="key">The C2S integer.</param>
    public ShopCardRow? Find(int key)
    {
        if (ByStockSetId.TryGetValue(key, out var bySet))
            return bySet;
        if (ByItemId.TryGetValue(key, out var byItem))
            return byItem;
        return ByStockId.TryGetValue(key, out var byStock) ? byStock : null;
    }

    /// <summary>Resolves the exact purchase identity used by 0xE1D720 and 0x1135250.</summary>
    /// <param name="key">A Stock or StockSet id from C2S 809.</param>
    public ShopPurchaseOffer? FindPurchase(int key)
    {
        if (ByStockId.TryGetValue(key, out var item))
        {
            var stock = item.Stocks.Single(s => s.StockId == key);
            return item.IsOnSale && stock.IsOnSale
                ? new ShopPurchaseOffer(key, 0, item.Name, item.MoneyType, stock.SellPrice, [stock])
                : null;
        }
        if (ByStockSetId.TryGetValue(key, out var set) && set.IsOnSale && set.Stocks.Count > 0)
            return new ShopPurchaseOffer(key, set.StockSetId, set.Name, set.MoneyType, set.SellPrice, set.Stocks);
        return null;
    }
}
