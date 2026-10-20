namespace BubbleFighter.Fantasy.Shop.Data;

/// <summary>
/// One <c>Stock</c> row from <c>ShopItemPet.xml</c>. The pet workbook Stock sheet
/// carries eight columns: the seven shared with the nested Stock wire layout of
/// <c>sub_113EF70</c> plus a Korean product-name column. Unlike the main shop
/// table it has no <c>sellPriceCash</c> or <c>secondCash</c> columns.
/// </summary>
/// <param name="StockId">Sheet column <c>stockId</c>.</param>
/// <param name="ItemId">Sheet column <c>itemId</c>.</param>
/// <param name="IsOnSale">Sheet column <c>isOnSale</c> (non-zero is true).</param>
/// <param name="Type">Sheet column <c>type</c>; wire width is <c>u16</c>. The 1256
/// pet workbook holds the values 1 and 9; they are kept verbatim.</param>
/// <param name="Duration">Sheet column <c>duration</c>.</param>
/// <param name="ShowPrice">Sheet column <c>showPrice</c>.</param>
/// <param name="SellPrice">Sheet column <c>sellPrice</c>.</param>
/// <param name="ProductName">Sheet column named by the Korean text for "product
/// name"; Korean display text kept verbatim. Not part of the nested Stock wire.</param>
public sealed record PetStockRow(
    int StockId,
    int ItemId,
    bool IsOnSale,
    ushort Type,
    int Duration,
    int ShowPrice,
    int SellPrice,
    string ProductName);

/// <summary>
/// One on-sale <c>StockSet</c> row of <c>ShopItemPet.xml</c>. The pet workbook
/// StockSet sheet has no <c>sellPriceCash</c> column; it instead carries a
/// <c>lucci</c> column. Wire mapping for the pet shop path is not yet pinned, so
/// every sheet value is kept verbatim with no remapping.
/// </summary>
/// <param name="StockSetId">Sheet column <c>stockSetId</c>.</param>
/// <param name="Name">Sheet column <c>stockSetName</c>; Korean display text kept
/// verbatim.</param>
/// <param name="MoneyType">Sheet column <c>moneyType</c> (<c>lucci</c> or
/// <c>cash</c> in the 1256 pet workbook), kept verbatim.</param>
/// <param name="Refundable">Sheet column <c>refundable</c> (non-zero is true).</param>
/// <param name="ShowPrice">Sheet column <c>showPrice</c>.</param>
/// <param name="SellPrice">Sheet column <c>sellPrice</c>.</param>
/// <param name="Lucci">Sheet column <c>lucci</c>, kept verbatim. Row text in the
/// 1256 pet workbook describes a bonus lucci grant on purchase, but that meaning
/// is not yet wire-proven; the sheet value is carried without interpretation.</param>
/// <param name="MainCategory">Sheet column <c>mainCategory</c> name key.</param>
/// <param name="SubCategory">Sheet column <c>subCategory</c> name key.</param>
/// <param name="PrimaryItemId">First attached stock's <c>itemId</c>, else 0.</param>
/// <param name="Stocks">Stocks listed in <c>stockId01</c>..<c>stockId10</c>.</param>
public sealed record PetCardRow(
    int StockSetId,
    string Name,
    string MoneyType,
    bool Refundable,
    int ShowPrice,
    int SellPrice,
    int Lucci,
    string MainCategory,
    string SubCategory,
    int PrimaryItemId,
    IReadOnlyList<PetStockRow> Stocks);

/// <summary>
/// One shop-visible <c>MainCategory</c> row of <c>ShopItemPet.xml</c> plus its
/// shop-visible subcategories.
/// </summary>
/// <param name="Id">Sheet column <c>id</c>.</param>
/// <param name="Name">Korean display text from the sheet column <c>nameKr</c>;
/// the pet workbook keys categories on <c>name</c> and displays <c>nameKr</c>
/// (it has no <c>displayName</c> column).</param>
/// <param name="DisplayOrder">Sheet column <c>displayOrder</c>.</param>
/// <param name="Subcategories">Linked <c>SubCategory</c> rows.</param>
public sealed record PetCategoryRow(
    int Id,
    string Name,
    int DisplayOrder,
    IReadOnlyList<PetSubcategoryRow> Subcategories);

/// <summary>One <c>SubCategory</c> row of <c>ShopItemPet.xml</c>.</summary>
/// <param name="Id">Sheet column <c>id</c>.</param>
/// <param name="Name">Korean display text from the sheet column <c>nameKr</c>.</param>
/// <param name="DisplayOrder">Sheet column <c>displayOrder</c>.</param>
/// <param name="ShopView">Sheet column <c>shopView</c> (non-zero is shop-visible).</param>
public sealed record PetSubcategoryRow(int Id, string Name, int DisplayOrder, int ShopView);

/// <summary>In-memory pet shop catalog built from <c>Config/ShopItemPet.xml</c>.</summary>
public sealed class PetCatalog
{
    /// <summary>Creates a catalog. All collections must already be bounded.</summary>
    /// <param name="sourcePath">The file that was loaded.</param>
    /// <param name="cards">On-sale pet stock sets.</param>
    /// <param name="categories">Shop-visible main categories.</param>
    public PetCatalog(string sourcePath, IReadOnlyList<PetCardRow> cards, IReadOnlyList<PetCategoryRow> categories)
    {
        SourcePath = sourcePath;
        Cards = cards;
        Categories = categories;
        foreach (var card in cards)
        {
            ByStockSetId[card.StockSetId] = card;
            if (card.PrimaryItemId != 0)
                ByItemId.TryAdd(card.PrimaryItemId, card);
            foreach (var stock in card.Stocks)
            {
                ByStockId.TryAdd(stock.StockId, card);
                if (stock.ItemId != 0)
                    ByItemId.TryAdd(stock.ItemId, card);
            }
        }
    }

    /// <summary>Gets the catalog file path.</summary>
    public string SourcePath { get; }

    /// <summary>Gets every on-sale pet stock set.</summary>
    public IReadOnlyList<PetCardRow> Cards { get; }

    /// <summary>Gets shop-visible categories.</summary>
    public IReadOnlyList<PetCategoryRow> Categories { get; }

    /// <summary>Gets cards whose <c>moneyType</c> is not <c>cash</c> (item shop / lucci).</summary>
    public IEnumerable<PetCardRow> ItemShopCards =>
        Cards.Where(c => !string.Equals(c.MoneyType, "cash", StringComparison.OrdinalIgnoreCase));

    /// <summary>Gets cards whose <c>moneyType</c> is <c>cash</c> (TC shop).</summary>
    public IEnumerable<PetCardRow> CashShopCards =>
        Cards.Where(c => string.Equals(c.MoneyType, "cash", StringComparison.OrdinalIgnoreCase));

    internal Dictionary<int, PetCardRow> ByStockSetId { get; } = new();
    internal Dictionary<int, PetCardRow> ByItemId { get; } = new();
    internal Dictionary<int, PetCardRow> ByStockId { get; } = new();

    /// <summary>Finds a card by stock-set id, item id, or stock id.</summary>
    /// <param name="key">The catalog integer key.</param>
    public PetCardRow? Find(int key)
    {
        if (ByStockSetId.TryGetValue(key, out var bySet))
            return bySet;
        if (ByItemId.TryGetValue(key, out var byItem))
            return byItem;
        return ByStockId.TryGetValue(key, out var byStock) ? byStock : null;
    }
}
