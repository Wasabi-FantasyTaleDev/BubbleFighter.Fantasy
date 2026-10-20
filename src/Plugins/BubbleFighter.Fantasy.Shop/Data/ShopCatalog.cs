namespace BubbleFighter.Fantasy.Shop.Data;

/// <summary>
/// One <c>Stock</c> row from <c>ShopItem2.xml</c> sheet <c>Stock</c>. Column order
/// matches the nested <c>Stock</c> wire in <c>sub_113EF70</c>.
/// </summary>
/// <param name="StockId">Sheet column <c>stockId</c>.</param>
/// <param name="ItemId">Sheet column <c>itemId</c>.</param>
/// <param name="IsOnSale">Sheet column <c>isOnSale</c> (non-zero is true).</param>
/// <param name="Type">Sheet column <c>type</c>; wire width is <c>u16</c>.</param>
/// <param name="Duration">Sheet column <c>duration</c>.</param>
/// <param name="ShowPrice">Sheet column <c>showPrice</c>.</param>
/// <param name="SellPrice">Sheet column <c>sellPrice</c>.</param>
/// <param name="SellPriceCash">Sheet column <c>sellPriceCash</c>; not on the nested Stock wire.</param>
/// <param name="SecondCash">Sheet column <c>secondCash</c>; not on the nested Stock wire.</param>
public sealed record ShopStockRow(
    int StockId,
    int ItemId,
    bool IsOnSale,
    ushort Type,
    int Duration,
    int ShowPrice,
    int SellPrice,
    int SellPriceCash,
    int SecondCash);

/// <summary>
/// One purchasable Stock or StockSet, with its display metadata and grant contents.
/// </summary>
/// <param name="StockSetId">Sheet column <c>stockSetId</c>.</param>
/// <param name="Name">Sheet column <c>stockSetName</c>.</param>
/// <param name="MoneyType">Sheet column <c>moneyType</c> (<c>lucci</c> or <c>cash</c>).</param>
/// <param name="IsOnSale">Sheet column <c>isOnSale</c>.</param>
/// <param name="Refundable">Sheet column <c>refundable</c>.</param>
/// <param name="ShowPrice">Sheet column <c>showPrice</c>.</param>
/// <param name="SellPrice">Sheet column <c>sellPrice</c>.</param>
/// <param name="SellPriceCash">Sheet column <c>sellPriceCash</c>.</param>
/// <param name="MainCategory">Sheet column <c>mainCategory</c> name.</param>
/// <param name="SubCategory">Sheet column <c>subCategory</c> name.</param>
/// <param name="PrimaryItemId">First attached stock's <c>itemId</c>, else 0.</param>
/// <param name="Stocks">Stocks listed in <c>stockId01</c>..<c>stockId10</c>.</param>
/// <param name="MainCategoryId">Resolved <c>MainCategory.id</c>, compared with the selected main tab at client <c>0x64E210</c>.</param>
/// <param name="SubCategoryId">Resolved <c>SubCategory.id</c>, compared with the selected sub-tab at client <c>0x64E210</c>.</param>
/// <param name="DisplayOrder">Sheet display order; client <c>0x651A80</c> sorts card +36.</param>
public sealed record ShopCardRow(
    int StockSetId,
    string Name,
    string MoneyType,
    bool IsOnSale,
    bool Refundable,
    int ShowPrice,
    int SellPrice,
    int SellPriceCash,
    string MainCategory,
    string SubCategory,
    int PrimaryItemId,
    IReadOnlyList<ShopStockRow> Stocks,
    int MainCategoryId,
    int SubCategoryId,
    int DisplayOrder = 0)
{
    /// <summary>Gets the ShopItem or StockSet identity used by client 0xA01310.</summary>
    public int CardId => StockSetId != 0 ? StockSetId : PrimaryItemId;

    /// <summary>Gets the resolver selector: 1 selects StockSet at 0x6463AE; 0 selects ShopItem.</summary>
    public int CardKind => StockSetId != 0 ? 1 : 0;

    /// <summary>Gets whether deployment supplements reserve this item before bounded catalog truncation.</summary>
    public bool SupplementalPriority { get; init; }

    /// <summary>Gets each grant item's native category classification used by server sale-term filtering.</summary>
    public IReadOnlyList<string> GrantCategories { get; init; } = [];

    /// <summary>Gets the schema-specific catalog terms when a set's display row differs from its grant contents.</summary>
    public IReadOnlyList<ShopStockRow>? CatalogTerms { get; init; }

    /// <summary>
    /// Gets the selectable purchase terms, separate from the items granted by a set.
    /// A set uses its own key, availability and prices; its first grant retains the
    /// existing type/duration display fields, whose set-specific semantics are unverified.
    /// </summary>
    public IReadOnlyList<ShopStockRow> PurchaseTerms => CatalogTerms ?? (StockSetId == 0 || Stocks.Count == 0
        ? Stocks
        : [Stocks[0] with
        {
            StockId = StockSetId,
            ItemId = StockSetId,
            IsOnSale = IsOnSale,
            ShowPrice = ShowPrice,
            SellPrice = SellPrice,
            SellPriceCash = SellPriceCash,
        }]);
}

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

/// <summary>One <c>ShopMainCategory</c> plus its shop-visible subcategories.</summary>
/// <param name="Id">Sheet column <c>id</c>.</param>
/// <param name="Name">Display name from the <c>MainCategory</c> sheet.</param>
/// <param name="DisplayOrder">Sheet column <c>displayOrder</c>.</param>
/// <param name="Subcategories">Linked <c>SubCategory</c> rows.</param>
public sealed record ShopCategoryRow(
    int Id,
    string Name,
    int DisplayOrder,
    IReadOnlyList<ShopSubcategoryRow> Subcategories);

/// <summary>One <c>ShopSubCategory</c> wire row.</summary>
/// <param name="Id">Sheet column <c>id</c>.</param>
/// <param name="Name">Display name from the <c>SubCategory</c> sheet.</param>
/// <param name="DisplayOrder">Sheet column <c>displayOrder</c>.</param>
/// <param name="ShopView">Sheet column <c>shopView</c>.</param>
/// <param name="PageType">Sheet column <c>pageType</c>: 0 filters by subcategory, 1 uses local promotions, 2 filters by main category (<c>0x64E210</c>).</param>
public sealed record ShopSubcategoryRow(int Id, string Name, int DisplayOrder, int ShopView, int PageType);

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
