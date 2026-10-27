namespace BubbleFighter.Fantasy.Contracts.Shop;

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
