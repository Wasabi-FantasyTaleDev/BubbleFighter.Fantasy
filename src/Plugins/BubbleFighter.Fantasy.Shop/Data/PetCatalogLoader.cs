using BubbleFighter.Fantasy.Contracts.ClientData;

namespace BubbleFighter.Fantasy.Shop.Data;

/// <summary>
/// Builds a <see cref="PetCatalog"/> from <c>Config/ShopItemPet.xml</c>, the pet
/// overlay workbook of the 1256 ConfigSh pack (14 worksheets; this loader consumes
/// Stock, StockSet, ShopItem, MainCategory, and SubCategory). The pet workbook has
/// no client loader address pinned yet, so the sheet and column semantics here come
/// from the workbook itself and mirror the proven main shop catalog loader.
/// Missing sheets, missing headers, zero on-sale rows, duplicate stock ids,
/// duplicate on-sale stock-set ids, and dangling stock or category references fail
/// loud. Placeholder rows (zero ids, note text, or empty rows) are skipped, and
/// Korean display strings are kept verbatim.
/// </summary>
public static class PetCatalogLoader
{
    private static readonly string[] RequiredSheets =
    [
        "Stock", "StockSet", "ShopItem", "MainCategory", "SubCategory",
    ];

    // The pet workbook Stock sheet names its display column with Korean text
    // ("product name"); the header key is required verbatim so schema drift in the
    // data file fails loud instead of silently mis-aligning columns.
    private const string ProductNameColumn = "\uC0C1\uD488 \uC774\uB984";

    /// <summary>Loads and validates the pet catalog from the bytes the client data store returns.</summary>
    /// <param name="file">The workbook, named by its client path.</param>
    /// <param name="maxBytes">Inclusive size cap.</param>
    public static PetCatalog Load(WorkbookFile file, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(file);
        var source = file.Name;
        var sheets = SpreadsheetWorkbook.Load(file, maxBytes, ShopCatalogLoader.LimitKey, RequiredSheets);
        var stocks = ParseStocks(RequireSheet(sheets, "Stock"));
        if (stocks.Count == 0)
            throw new InvalidOperationException($"Pet catalog {source} Stock sheet has no data rows.");
        var byStockId = new Dictionary<int, PetStockRow>(stocks.Count);
        foreach (var stock in stocks)
        {
            if (!byStockId.TryAdd(stock.StockId, stock))
                throw new InvalidOperationException(
                    $"Pet catalog {source} Stock sheet has duplicate stockId {stock.StockId}.");
        }

        var cards = ParseStockSets(RequireSheet(sheets, "StockSet"), byStockId, source);
        if (cards.Count == 0)
            throw new InvalidOperationException($"Pet catalog {source} has no isOnSale StockSet rows.");
        var items = ParseShopItems(RequireSheet(sheets, "ShopItem"));
        if (items.Count == 0)
            throw new InvalidOperationException($"Pet catalog {source} ShopItem sheet has no data rows.");
        var categories = ParseCategories(
            RequireSheet(sheets, "MainCategory"),
            RequireSheet(sheets, "SubCategory"),
            cards,
            source);
        if (categories.Count == 0)
            throw new InvalidOperationException($"Pet catalog {source} has no shop-visible MainCategory rows.");
        return new PetCatalog(source, cards, categories);
    }

    private static IReadOnlyList<IReadOnlyList<string>> RequireSheet(
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> sheets, string name) =>
        sheets[name];

    private static Dictionary<string, int> HeaderMap(IReadOnlyList<string> header)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Count; i++)
        {
            var key = header[i].Trim();
            if (key.Length > 0 && !map.ContainsKey(key))
                map[key] = i;
        }

        return map;
    }

    private static string Cell(IReadOnlyList<string> row, Dictionary<string, int> header, string name) =>
        header.TryGetValue(name, out var i) && i < row.Count ? row[i].Trim() : string.Empty;

    private static int IntCell(IReadOnlyList<string> row, Dictionary<string, int> header, string name)
    {
        var text = Cell(row, header, name);
        if (string.IsNullOrEmpty(text))
            return 0;
        if (!int.TryParse(text, out var value))
            throw new InvalidOperationException($"Pet catalog column {name} is not an i32: '{text}'.");
        return value;
    }

    /// <summary>
    /// Parses an optional i32 cell. Returns <see langword="false"/> for an empty cell
    /// and for the note rows the real workbook interleaves with data rows
    /// (non-numeric key cells such as a leading comment). Numeric rows still fail
    /// loud through <see cref="IntCell"/>.
    /// </summary>
    private static bool TryIntCell(IReadOnlyList<string> row, Dictionary<string, int> header, string name, out int value)
    {
        var text = Cell(row, header, name);
        if (string.IsNullOrEmpty(text))
        {
            value = 0;
            return false;
        }

        if (!int.TryParse(text, out value))
        {
            value = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reports whether a category-sheet row is a real data row. The 1256 pet workbook
    /// interleaves placeholder rows (id 0, note text in the id column, or fully empty
    /// trailing rows) with data rows; data rows keep the strict <see cref="IntCell"/>
    /// behavior for every other column.
    /// </summary>
    private static bool IsCategoryDataRow(IReadOnlyList<string> row, Dictionary<string, int> header) =>
        TryIntCell(row, header, "id", out var id) && id != 0;

    private static bool RequireHeader(Dictionary<string, int> header, params string[] names)
    {
        foreach (var name in names)
        {
            if (!header.ContainsKey(name))
                throw new InvalidOperationException($"Pet catalog is missing column '{name}'.");
        }

        return true;
    }

    private static List<PetStockRow> ParseStocks(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count < 2)
            return [];
        var header = HeaderMap(rows[0]);
        RequireHeader(header, "stockId", "itemId", "isOnSale", "type", "duration", "showPrice", "sellPrice", ProductNameColumn);
        var list = new List<PetStockRow>(rows.Count - 1);
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var stockId = IntCell(row, header, "stockId");
            if (stockId == 0)
                continue;
            var type = IntCell(row, header, "type");
            if (type is < 0 or > ushort.MaxValue)
                throw new InvalidOperationException($"Pet catalog Stock.type {type} does not fit u16.");
            list.Add(new PetStockRow(
                stockId,
                IntCell(row, header, "itemId"),
                IntCell(row, header, "isOnSale") != 0,
                (ushort)type,
                IntCell(row, header, "duration"),
                IntCell(row, header, "showPrice"),
                IntCell(row, header, "sellPrice"),
                Cell(row, header, ProductNameColumn)));
        }

        return list;
    }

    private static List<PetCardRow> ParseStockSets(
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyDictionary<int, PetStockRow> stocks,
        string path)
    {
        if (rows.Count < 2)
            return [];
        var header = HeaderMap(rows[0]);
        RequireHeader(
            header,
            "stockSetId", "stockSetName", "isOnSale", "showPrice", "sellPrice",
            "moneyType", "refundable", "mainCategory", "subCategory", "stockId01");
        var list = new List<PetCardRow>();
        var seenSetIds = new HashSet<int>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            if (IntCell(row, header, "isOnSale") == 0)
                continue;
            var setId = IntCell(row, header, "stockSetId");
            if (setId == 0)
                continue;
            if (!seenSetIds.Add(setId))
                throw new InvalidOperationException(
                    $"Pet catalog {path} StockSet sheet has duplicate on-sale stockSetId {setId}.");
            var attached = new List<PetStockRow>(10);
            for (var n = 1; n <= 10; n++)
            {
                var stockId = IntCell(row, header, $"stockId{n:00}");
                if (stockId == 0)
                    continue;
                if (!stocks.TryGetValue(stockId, out var stock))
                    throw new InvalidOperationException(
                        $"Pet catalog on-sale StockSet {setId} references missing stockId {stockId}.");
                attached.Add(stock);
            }

            var name = Cell(row, header, "stockSetName");
            if (name.Length == 0)
                throw new InvalidOperationException($"Pet catalog StockSet {setId} has an empty stockSetName.");
            list.Add(new PetCardRow(
                setId,
                name,
                Cell(row, header, "moneyType"),
                IntCell(row, header, "refundable") != 0,
                IntCell(row, header, "showPrice"),
                IntCell(row, header, "sellPrice"),
                IntCell(row, header, "lucci"),
                Cell(row, header, "mainCategory"),
                Cell(row, header, "subCategory"),
                attached.Count > 0 ? attached[0].ItemId : 0,
                attached));
        }

        return list;
    }

    private static HashSet<int> ParseShopItems(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count < 2)
            return [];
        var header = HeaderMap(rows[0]);
        RequireHeader(header, "itemId", "itemName");
        var ids = new HashSet<int>();
        for (var i = 1; i < rows.Count; i++)
        {
            var id = IntCell(rows[i], header, "itemId");
            if (id != 0)
                ids.Add(id);
        }

        return ids;
    }

    /// <summary>
    /// Parses the pet workbook category sheets. Cards reference the sheet <c>name</c>
    /// key column; the Korean <c>nameKr</c> column is the display text (the pet
    /// workbook has no <c>displayName</c> column). A name referenced by an on-sale
    /// card that has no real row in the referenced sheet fails loud; a row that
    /// exists but carries <c>shopView</c> 0 stays unemitted, like the main shop
    /// catalog loader.
    /// </summary>
    private static List<PetCategoryRow> ParseCategories(
        IReadOnlyList<IReadOnlyList<string>> mainRows,
        IReadOnlyList<IReadOnlyList<string>> subRows,
        IReadOnlyList<PetCardRow> cards,
        string path)
    {
        if (mainRows.Count < 2 || subRows.Count < 2)
            return [];
        var mainHeader = HeaderMap(mainRows[0]);
        var subHeader = HeaderMap(subRows[0]);
        RequireHeader(mainHeader, "id", "name", "nameKr", "displayOrder", "shopView");
        RequireHeader(subHeader, "id", "name", "nameKr", "displayOrder", "shopView");

        // Every real sheet row key backs the dangling-reference check; only rows
        // with shopView != 0 become emitted candidates.
        var subByKey = new Dictionary<string, PetSubcategoryRow>(StringComparer.OrdinalIgnoreCase);
        var subAny = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < subRows.Count; i++)
        {
            var row = subRows[i];
            if (!IsCategoryDataRow(row, subHeader))
                continue;
            var key = Cell(row, subHeader, "name");
            if (key.Length == 0)
                continue;
            subAny.Add(key);
            if (IntCell(row, subHeader, "shopView") == 0)
                continue;
            subByKey[key] = new PetSubcategoryRow(
                IntCell(row, subHeader, "id"),
                FirstNonEmpty(Cell(row, subHeader, "nameKr"), key),
                IntCell(row, subHeader, "displayOrder"),
                IntCell(row, subHeader, "shopView"));
        }

        foreach (var card in cards)
        {
            if (!subAny.Contains(card.SubCategory))
                throw new InvalidOperationException(
                    $"Pet catalog {path} on-sale StockSet {card.StockSetId} references missing subCategory '{card.SubCategory}'.");
        }

        var mainAny = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < mainRows.Count; i++)
        {
            var row = mainRows[i];
            if (!IsCategoryDataRow(row, mainHeader))
                continue;
            var key = Cell(row, mainHeader, "name");
            if (key.Length == 0)
                continue;
            mainAny.Add(key);
        }

        foreach (var card in cards)
        {
            if (!mainAny.Contains(card.MainCategory))
                throw new InvalidOperationException(
                    $"Pet catalog {path} on-sale StockSet {card.StockSetId} references missing mainCategory '{card.MainCategory}'.");
        }

        var usedMain = new HashSet<string>(cards.Select(c => c.MainCategory), StringComparer.OrdinalIgnoreCase);
        var mains = new List<PetCategoryRow>();
        for (var i = 1; i < mainRows.Count; i++)
        {
            var row = mainRows[i];
            if (!IsCategoryDataRow(row, mainHeader))
                continue;
            if (IntCell(row, mainHeader, "shopView") == 0)
                continue;
            var key = Cell(row, mainHeader, "name");
            if (key.Length == 0 || !usedMain.Contains(key))
                continue;
            var subs = cards
                .Where(c => string.Equals(c.MainCategory, key, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.SubCategory)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(n => subByKey.TryGetValue(n, out var s) ? s : null)
                .OfType<PetSubcategoryRow>()
                .ToList();
            mains.Add(new PetCategoryRow(
                IntCell(row, mainHeader, "id"),
                FirstNonEmpty(Cell(row, mainHeader, "nameKr"), key),
                IntCell(row, mainHeader, "displayOrder"),
                subs));
        }

        return mains;
    }

    private static string FirstNonEmpty(string a, string b) => a.Length > 0 ? a : b;
}
