namespace BubbleFighter.Fantasy.Shop.Data;

/// <summary>
/// Builds a <see cref="ShopCatalog"/> from <c>Config/ShopItem2.xml</c>. Loader
/// <c>sub_9FE8F0</c> opens that path through <c>sub_1021330</c>. Missing sheets,
/// missing headers, or zero on-sale rows fail loud.
/// </summary>
public static class ShopCatalogLoader
{
    private static readonly string[] RequiredSheets =
    [
        "Stock", "StockSet", "ShopItem", "MainCategory", "SubCategory",
    ];

    /// <summary>Loads and validates the catalog from the bytes the client data store returns.</summary>
    /// <param name="file">The workbook, named by its client path.</param>
    /// <param name="maxBytes">Inclusive size cap.</param>
    public static ShopCatalog Load(WorkbookFile file, long maxBytes) =>
        Load(file, maxBytes, ShopCatalogSchema.Kr1256, displayColumn: null, diagnostic: null);

    /// <summary>Loads a workbook with an explicit client column schema, preserving strict required-column checks.</summary>
    /// <param name="file">The workbook, named by its client path.</param>
    /// <param name="maxBytes">Inclusive workbook byte limit.</param>
    /// <param name="schema">The verified column set; it is never inferred from missing columns.</param>
    public static ShopCatalog Load(WorkbookFile file, long maxBytes, ShopCatalogSchema schema) =>
        Load(file, maxBytes, schema, displayColumn: null, diagnostic: null);

    /// <summary>Loads a workbook with an explicit client column schema and an optional diagnostic sink.</summary>
    /// <param name="file">The workbook, named by its client path.</param>
    /// <param name="maxBytes">Inclusive workbook byte limit.</param>
    /// <param name="schema">The verified column set; it is never inferred from missing columns.</param>
    /// <param name="diagnostic">Receives bounded load diagnostics; null discards them.</param>
    public static ShopCatalog Load(WorkbookFile file, long maxBytes, ShopCatalogSchema schema, Action<string>? diagnostic) =>
        Load(file, maxBytes, schema, displayColumn: null, diagnostic: diagnostic);

    /// <summary>
    /// Loads a workbook read from the client data store. The display column is a per-client-localization choice,
    /// not part of the column set: the kr-812 catalog carries the nameKr strings while the CN and ID clients
    /// localize through name, and the sheets name those columns inconsistently (cn-717's SubCategory uses
    /// name_kor).
    /// </summary>
    /// <param name="file">The selected client's Config/ShopItem2.xml.</param>
    /// <param name="maxBytes">Inclusive workbook byte limit.</param>
    /// <param name="schema">The verified column set; it is never inferred from missing columns.</param>
    /// <param name="displayColumn">Category name column sent to the client; null picks the schema default.</param>
    /// <param name="diagnostic">Receives bounded load diagnostics; null discards them.</param>
    /// <param name="supplementalOffers">Existing items with restored native terms or explicitly configured fallback day sales.</param>
    /// <param name="clientProfile">Client profile identity used to select version-scoped supplements.</param>
    public static ShopCatalog Load(
        WorkbookFile file, long maxBytes, ShopCatalogSchema schema, string? displayColumn, Action<string>? diagnostic,
        IReadOnlyList<ShopSupplementOffer>? supplementalOffers = null, string clientProfile = "")
    {
        var path = file.Name;
        var (secondaryCashColumns, defaultDisplayColumn) = schema switch
        {
            ShopCatalogSchema.Kr1256 => (true, "displayName"),
            ShopCatalogSchema.Legacy => (false, "nameKr"),
            _ => throw new ArgumentOutOfRangeException(nameof(schema), schema, "Unsupported shop workbook schema."),
        };
        var display = displayColumn ?? defaultDisplayColumn;
        // The 812 client's category lookups (StockSet loader 0x99E8A0 calling 0x993190
        // main / 0x993280 sub) return 0 for an unmatched name and keep the row, so the
        // real workbook's Packages references resolve to 0 there. The Kr812 schema
        // mirrors that; Kr1256 keeps failing loud. Diagnostics stay bounded: distinct
        // sheet+name pairs only, then a suppressed counter.
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var suppressed = 0;
        void Report(string sheet, string key)
        {
            suppressed++;
            if (reported.Add($"{sheet}|{key}"))
                diagnostic?.Invoke($"Shop catalog {sheet} name '{key}' has no category row; the client lookup stores 0.");
        }

        var sheets = SpreadsheetWorkbook.Load(file, maxBytes, RequiredSheets);
        var stocks = ParseStocks(RequireSheet(sheets, "Stock"), secondaryCashColumns);
        if (stocks.Count == 0)
            throw new InvalidOperationException($"Shop catalog {path} Stock sheet has no data rows.");
        var byStockId = new Dictionary<int, ShopStockRow>(stocks.Count);
        foreach (var stock in stocks)
        {
            if (!byStockId.TryAdd(stock.StockId, stock))
            {
                // The 812 workbook repeats one identical off-sale Stock row; conflicting definitions still fail.
                if (schema == ShopCatalogSchema.Legacy && byStockId[stock.StockId] == stock)
                    continue;
                throw new InvalidOperationException(
                    $"Shop catalog {path} Stock sheet has duplicate stockId {stock.StockId}.");
            }
        }

        var supplements = new Dictionary<int, ShopSupplementOffer>();
        var itemRows = RequireSheet(sheets, "ShopItem");
        var itemHeader = HeaderMap(itemRows[0]);
        var activeOffers = (supplementalOffers ?? []).Where(offer => offer.ClientProfiles.Length == 0
            || offer.ClientProfiles.Contains(clientProfile, StringComparer.Ordinal)).ToArray();
        foreach (var offer in activeOffers.Where(offer => offer.ItemId > 0))
        {
            var terms = byStockId.Values.Where(stock => stock.ItemId == offer.ItemId).ToArray();
            var itemRow = itemRows.Skip(1).SingleOrDefault(row => IntCell(row, itemHeader, "itemId") == offer.ItemId);
            if ((itemRow is null || (terms.Length == 0 && offer.StockId == 0)) && offer.SkipIfMissing)
            {
                diagnostic?.Invoke($"Shop supplement item {offer.ItemId} has no native item row or sale stock in this client workbook; skipped.");
                continue;
            }
            if (terms.Length == 0 && offer.StockId == 0)
                throw new InvalidOperationException($"Shop supplement item {offer.ItemId} has no existing workbook stock.");
            var itemVisible = itemRows.Skip(1).Any(row => IntCell(row, itemHeader, "itemId") == offer.ItemId
                && IntCell(row, itemHeader, "isOnSale") != 0);
            if (itemVisible && terms.Any(stock => stock.IsOnSale))
                continue;
            if (itemRow is null)
                throw new InvalidOperationException($"Shop supplement item {offer.ItemId} has no ShopItem row.");
            var originalMoney = Cell(itemRow, itemHeader, "moneyType");
            var clientMoney = originalMoney == "cashCN" ? "cash" : originalMoney;
            // cn-324 sub_821DE0 loads moneyType locally; the native card wire has no currency field.
            if (offer.MoneyType != "client" && clientMoney != offer.MoneyType)
                throw new InvalidOperationException($"Shop supplement item {offer.ItemId} requires client currency '{clientMoney}', not '{offer.MoneyType}'.");
            if (terms.Length == 0)
            {
                if (schema != ShopCatalogSchema.Legacy || offer.StockId is < 1_000_000 or >= 2_000_000
                    || offer.DurationDays <= 0 || offer.DurationDays > IntCell(itemRow, itemHeader, "maxDuration")
                    || Cell(itemRow, itemHeader, "durationType") != "day")
                    throw new InvalidOperationException($"Shop supplement item {offer.ItemId} has invalid server-defined day terms.");
                var setRows = RequireSheet(sheets, "StockSet");
                var setHeader = HeaderMap(setRows[0]);
                if (byStockId.ContainsKey(offer.StockId) || setRows.Skip(1).Any(row => IntCell(row, setHeader, "stockSetId") == offer.StockId))
                    throw new InvalidOperationException($"Shop supplement stockId {offer.StockId} conflicts with an existing sale identity.");
                // cn-324 sub_69AFB0 reads complete Stock objects; sub_7D38A0 sends the received sale identity.
                terms = [new ShopStockRow(offer.StockId, offer.ItemId, true, 1, offer.DurationDays,
                    offer.Price, offer.Price, 0, 0)];
                byStockId.Add(offer.StockId, terms[0]);
                diagnostic?.Invoke($"Shop supplement item {offer.ItemId}: server-defined Stock {offer.StockId}, {offer.DurationDays} days, {offer.Price} {clientMoney}; native client acceptance pending.");
            }
            supplements.Add(offer.ItemId, offer);
            var selected = terms.OrderByDescending(stock => stock.Duration < 0)
                .ThenByDescending(stock => stock.Duration).ThenBy(stock => stock.StockId).First();
            byStockId[selected.StockId] = selected with
            {
                IsOnSale = true, ShowPrice = offer.Price, SellPrice = offer.Price,
                SellPriceCash = secondaryCashColumns ? clientMoney == "cash" ? offer.Price : 0 : selected.SellPriceCash,
            };
        }

        var mainIds = CategoryIds(RequireSheet(sheets, "MainCategory"), "MainCategory");
        var subIds = CategoryIds(RequireSheet(sheets, "SubCategory"), "SubCategory");
        var cards = ParseShopItems(RequireSheet(sheets, "ShopItem"), byStockId.Values.ToArray(), mainIds, subIds, schema, Report, supplements);
        foreach (var itemId in supplements.Keys)
            if (!cards.Any(card => card.PrimaryItemId == itemId))
                throw new InvalidOperationException($"Shop supplement item {itemId} has no renderable ShopItem row.");
        var priorityIds = activeOffers.Select(offer => offer.ItemId).ToHashSet();
        for (var index = 0; index < cards.Count; index++)
            if (priorityIds.Contains(cards[index].PrimaryItemId))
                cards[index] = cards[index] with { SupplementalPriority = true };
        var setOffers = activeOffers.Where(offer => offer.StockSetId > 0).ToDictionary(offer => offer.StockSetId);
        var sets = ParseStockSets(RequireSheet(sheets, "StockSet"), byStockId, mainIds, subIds, path, secondaryCashColumns, schema, Report, setOffers);
        foreach (var offer in setOffers.Values)
            if (!sets.Any(card => card.StockSetId == offer.StockSetId))
            {
                if (offer.SkipIfMissing && !RequireSheet(sheets, "StockSet").Skip(1).Any(row =>
                        IntCell(row, HeaderMap(RequireSheet(sheets, "StockSet")[0]), "stockSetId") == offer.StockSetId))
                    diagnostic?.Invoke($"Shop supplement set {offer.StockSetId} is absent from this client workbook; skipped.");
                else
                    throw new InvalidOperationException($"Shop supplement set {offer.StockSetId} has no renderable StockSet row.");
            }
        cards.AddRange(sets);
        var grantCategories = itemRows.Skip(1).Where(row => IntCell(row, itemHeader, "itemId") > 0)
            .ToDictionary(row => IntCell(row, itemHeader, "itemId"), row =>
                (Cell(row, itemHeader, "mainCategory"), Cell(row, itemHeader, "subCategory")) switch
                {
                    ("Clothes", _) => "clothes",
                    ("Weapons", "MeleeWeapon") => "hammers",
                    ("Weapons", "SpecialWeapon") => "grenades",
                    ("Weapons", "MainWeapon" or "SecondaryWeapon" or "SubWeapon") => "guns",
                    _ => "other",
                });
        for (var index = 0; index < cards.Count; index++)
            cards[index] = cards[index] with
            {
                GrantCategories = cards[index].Stocks.Select(stock => grantCategories.GetValueOrDefault(stock.ItemId, "other")).ToArray(),
            };
        if (cards.Count == 0)
            throw new InvalidOperationException($"Shop catalog {path} has no purchasable rows.");
        var items = RequireSheet(sheets, "ShopItem");
        if (items.Count == 0)
            throw new InvalidOperationException($"Shop catalog {path} ShopItem sheet has no data rows.");
        var (categories, shopVisibleSubs, mainDefaults, shopVisibleMains, allSubs) = ParseCategories(
            RequireSheet(sheets, "MainCategory"),
            RequireSheet(sheets, "SubCategory"),
            cards, display);
        if (categories.Count == 0)
            throw new InvalidOperationException($"Shop catalog {path} has no shop-visible MainCategory rows.");
        if (suppressed > reported.Count)
            diagnostic?.Invoke($"Shop catalog suppressed {suppressed - reported.Count} further missing-category diagnostics.");
        return new ShopCatalog(path, cards, categories, shopVisibleSubs, mainDefaults, shopVisibleMains)
        {
            AllSubcategories = allSubs,
            Gifts = ParseGifts(RequireSheet(sheets, "StockSet"), byStockId),
        };
    }

    // Every Stock with an item, and every StockSet whose slots all resolve and that grants items or Lucci, on sale or
    // not. The Lucci column is "lucci" in the kr-812 workbook and "Lucci" in id-400's, so it is matched without case.
    private static Dictionary<int, ShopGift> ParseGifts(IReadOnlyList<IReadOnlyList<string>> setRows,
        IReadOnlyDictionary<int, ShopStockRow> stocks)
    {
        var gifts = new Dictionary<int, ShopGift>(stocks.Count);
        foreach (var stock in stocks.Values)
            if (stock.ItemId > 0)
                gifts[stock.StockId] = new ShopGift(stock.StockId, 0, 0, [stock]);
        if (setRows.Count < 2)
            return gifts;
        var header = HeaderMap(setRows[0]);
        var lucciColumn = header.Keys.SingleOrDefault(name => string.Equals(name, "lucci", StringComparison.OrdinalIgnoreCase));
        for (var i = 1; i < setRows.Count; i++)
        {
            var row = setRows[i];
            if (!TryIntCell(row, header, "stockSetId", out var setId) || setId <= 0 || gifts.ContainsKey(setId))
                continue;
            var granted = new List<ShopStockRow>();
            var resolved = true;
            for (var slot = 1; slot <= 10; slot++)
            {
                var column = $"stockId{slot:D2}";
                if (!header.ContainsKey(column) || !TryIntCell(row, header, column, out var stockId) || stockId == 0)
                    continue;
                if (stocks.TryGetValue(stockId, out var stock) && stock.ItemId > 0)
                    granted.Add(stock);
                else
                    resolved = false;
            }
            var lucci = lucciColumn is not null && TryIntCell(row, header, lucciColumn, out var amount) ? amount : 0;
            if (resolved && (granted.Count > 0 || lucci > 0))
                gifts.Add(setId, new ShopGift(setId, setId, lucci, granted));
        }
        return gifts;
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
            throw new InvalidOperationException($"Shop catalog column {name} is not an i32: '{text}'.");
        return value;
    }

    /// <summary>
    /// Parses an optional i32 cell. Returns <see langword="null"/> for an empty cell and
    /// for the note rows the real workbook interleaves with data rows (non-numeric key
    /// cells such as a leading comment). Numeric rows still fail loud through
    /// <see cref="IntCell"/>.
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
    /// Reports whether a category-sheet row is a real data row. The 1256 workbook
    /// interleaves placeholder rows (id 0 or a leading note text) with data rows; the
    /// client loader skips them, and so must this one. Data rows keep the strict
    /// <see cref="IntCell"/> behavior for every other column.
    /// </summary>
    private static bool IsCategoryDataRow(IReadOnlyList<string> row, Dictionary<string, int> header) =>
        TryIntCell(row, header, "id", out var id) && id != 0;

    private static bool RequireHeader(Dictionary<string, int> header, params string[] names)
    {
        foreach (var name in names)
        {
            if (!header.ContainsKey(name))
                throw new InvalidOperationException($"Shop catalog is missing column '{name}'.");
        }

        return true;
    }

    private static List<ShopStockRow> ParseStocks(IReadOnlyList<IReadOnlyList<string>> rows, bool secondaryCashColumns)
    {
        if (rows.Count < 2)
            return [];
        var header = HeaderMap(rows[0]);
        RequireHeader(header, "stockId", "itemId", "isOnSale", "type", "duration", "showPrice", "sellPrice");
        if (secondaryCashColumns)
            RequireHeader(header, "sellPriceCash", "secondCash");
        var list = new List<ShopStockRow>(rows.Count - 1);
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var stockId = IntCell(row, header, "stockId");
            if (stockId == 0)
                continue;
            var type = IntCell(row, header, "type");
            if (type is < 0 or > ushort.MaxValue)
                throw new InvalidOperationException($"Shop catalog Stock.type {type} does not fit u16.");
            list.Add(new ShopStockRow(
                stockId,
                IntCell(row, header, "itemId"),
                IntCell(row, header, "isOnSale") != 0,
                (ushort)type,
                IntCell(row, header, "duration"),
                IntCell(row, header, "showPrice"),
                IntCell(row, header, "sellPrice"),
                secondaryCashColumns ? IntCell(row, header, "sellPriceCash") : 0,
                secondaryCashColumns ? IntCell(row, header, "secondCash") : 0));
        }

        return list;
    }

    private static List<ShopCardRow> ParseStockSets(
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyDictionary<int, ShopStockRow> stocks,
        IReadOnlyDictionary<string, int> mainIds,
        IReadOnlyDictionary<string, int> subIds,
        string path, bool secondaryCashColumns, ShopCatalogSchema schema, Action<string, string> reportMissing,
        IReadOnlyDictionary<int, ShopSupplementOffer> supplements)
    {
        if (rows.Count < 2)
            return [];
        var header = HeaderMap(rows[0]);
        RequireHeader(
            header,
            "stockSetId", "stockSetName", "isOnSale", "showPrice", "sellPrice",
            "moneyType", "refundable", "mainCategory", "subCategory", "stockId01", "displayOrder");
        if (secondaryCashColumns)
            RequireHeader(header, "sellPriceCash");
        var list = new List<ShopCardRow>();
        var seenSetIds = new HashSet<int>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var setId = IntCell(row, header, "stockSetId");
            supplements.TryGetValue(setId, out var supplement);
            var visible = IntCell(row, header, "isOnSale") != 0;
            if (!visible && supplement is null)
                continue;
            var money = Cell(row, header, "moneyType");
            var clientMoney = money == "cashCN" ? "cash" : money;
            var showPrice = IntCell(row, header, "showPrice");
            var sellPrice = IntCell(row, header, "sellPrice");
            var cashPrice = secondaryCashColumns ? IntCell(row, header, "sellPriceCash") : 0;
            if (!visible && supplement is not null)
            {
                if (supplement.MoneyType != "client" && supplement.MoneyType != clientMoney)
                    throw new InvalidOperationException($"Shop supplement set {setId} requires client currency '{clientMoney}', not '{supplement.MoneyType}'.");
                showPrice = sellPrice = supplement.Price;
                cashPrice = secondaryCashColumns && clientMoney == "cash" ? supplement.Price : 0;
            }
            if (setId == 0)
                continue;
            if (!seenSetIds.Add(setId))
                throw new InvalidOperationException(
                    $"Shop catalog {path} StockSet sheet has duplicate on-sale stockSetId {setId}.");
            var attached = new List<ShopStockRow>(10);
            for (var n = 1; n <= 10; n++)
            {
                var stockId = IntCell(row, header, $"stockId{n:00}");
                if (stockId == 0)
                    continue;
                if (!stocks.TryGetValue(stockId, out var stock))
                {
                    // The 812 loader stores the raw stockId numbers and joins them to
                    // Stock rows at display time, so an unresolvable slot cannot fail
                    // the load; the slot is skipped and a set with no resolvable
                    // option cannot be priced and is dropped below.
                    if (schema == ShopCatalogSchema.Legacy && supplement is null)
                    {
                        reportMissing("StockSet", $"stockId {stockId} (set {setId})");
                        continue;
                    }
                    throw new InvalidOperationException(
                        $"Shop catalog on-sale StockSet {setId} references missing stockId {stockId}.");
                }
                attached.Add(stock);
            }
            if (attached.Count == 0)
            {
                if (schema == ShopCatalogSchema.Legacy)
                {
                    reportMissing("StockSet", $"set {setId} has no resolvable stock slots");
                    continue;
                }
                throw new InvalidOperationException(
                    $"Shop catalog on-sale StockSet {setId} has no resolvable stock slots.");
            }

            var name = Cell(row, header, "stockSetName");
            if (name.Length == 0)
                throw new InvalidOperationException($"Shop catalog StockSet {setId} has an empty stockSetName.");
            ValidatePrices(money, [sellPrice], setId, schema);
            // The legacy set card prices itself: the first
            // set card carries an inline stock whose id and itemId are both the set
            // id, duration 0 and the set's own price, with the vector repeating it -
            // not the gift contents.
            var terms = schema == ShopCatalogSchema.Legacy
                ? new List<ShopStockRow>
                {
                    new(setId, setId, true, (ushort)IntCell(row, header, "type"), 0,
                        showPrice, sellPrice, cashPrice, 0),
                }
                : attached;
            list.Add(new ShopCardRow(
                setId,
                name,
                money,
                true,
                IntCell(row, header, "refundable") != 0,
                showPrice,
                sellPrice,
                cashPrice,
                Cell(row, header, "mainCategory"),
                Cell(row, header, "subCategory"),
                attached.Count > 0 ? attached[0].ItemId : 0,
                attached,
                ResolveCategory(mainIds, Cell(row, header, "mainCategory"), "MainCategory", setId, schema, reportMissing),
                ResolveCategory(subIds, Cell(row, header, "subCategory"), "SubCategory", setId, schema, reportMissing),
                IntCell(row, header, "displayOrder"))
            {
                CatalogTerms = schema == ShopCatalogSchema.Legacy ? terms : null,
                SupplementalPriority = supplement is not null,
            });
        }

        return list;
    }

    private static List<ShopCardRow> ParseShopItems(IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyList<ShopStockRow> stocks, IReadOnlyDictionary<string, int> mainIds,
        IReadOnlyDictionary<string, int> subIds, ShopCatalogSchema schema, Action<string, string> reportMissing,
        IReadOnlyDictionary<int, ShopSupplementOffer> supplements)
    {
        if (rows.Count < 2)
            return [];
        var header = HeaderMap(rows[0]);
        RequireHeader(header, "itemId", "itemName", "isOnSale", "moneyType", "refundable",
            "mainCategory", "subCategory", "displayOrder");
        var byItem = stocks.Where(s => s.IsOnSale).GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.ToArray());
        var cards = new List<ShopCardRow>();
        var seen = new HashSet<int>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var id = IntCell(row, header, "itemId");
            if (id == 0 || (IntCell(row, header, "isOnSale") == 0 && !supplements.ContainsKey(id)) || !byItem.TryGetValue(id, out var terms))
                continue;
            if (!seen.Add(id))
                throw new InvalidOperationException($"Shop catalog has duplicate on-sale itemId {id}.");
            var name = Cell(row, header, "itemName");
            if (name.Length == 0)
                throw new InvalidOperationException($"ShopItem {id} has an empty name.");
            var main = Cell(row, header, "mainCategory");
            var sub = Cell(row, header, "subCategory");
            var money = supplements.TryGetValue(id, out var supplement) && supplement.MoneyType != "client"
                ? supplement.MoneyType : Cell(row, header, "moneyType");
            ValidatePrices(money, terms.Select(s => s.SellPrice), id, schema);
            var first = terms[0];
            if (schema == ShopCatalogSchema.Legacy)
            {
                // The legacy dialog sells ONE card per item whose stock vector carries
                // the duration choices: catalog card 0 lists
                // itemId 100001 once with inline stock 1000001 (1 day) and the vector
                // [1000001 (1d), 1000002 (7d), 1000003 (30d)] - the vector repeats the
                // inline first and then the other terms. Per-term cards would duplicate the item across tabs.
                cards.Add(new ShopCardRow(0, name, money, true, IntCell(row, header, "refundable") != 0,
                    first.ShowPrice, first.SellPrice, first.SellPriceCash, main, sub, id, terms,
                    ResolveCategory(mainIds, main, "MainCategory", id, schema, reportMissing),
                    ResolveCategory(subIds, sub, "SubCategory", id, schema, reportMissing),
                    IntCell(row, header, "displayOrder")));
            }
            else
            {
                // Each offer gets its own inline stock and matching choice.
                foreach (var term in terms)
                    cards.Add(new ShopCardRow(0, name, money, true, IntCell(row, header, "refundable") != 0,
                        term.ShowPrice, term.SellPrice, term.SellPriceCash, main, sub, id, [term],
                        ResolveCategory(mainIds, main, "MainCategory", id, schema, reportMissing),
                        ResolveCategory(subIds, sub, "SubCategory", id, schema, reportMissing),
                        IntCell(row, header, "displayOrder")));
            }
        }

        return cards;
    }

    private static (
        List<ShopCategoryRow> Categories,
        IReadOnlyList<ShopSubcategoryRow> ShopVisibleSubs,
        IReadOnlyDictionary<int, int> MainDefaultSubIds,
        IReadOnlyList<ShopCategoryRow> ShopVisibleMains,
        IReadOnlyList<ShopSubcategoryRow> AllSubs) ParseCategories(
        IReadOnlyList<IReadOnlyList<string>> mainRows,
        IReadOnlyList<IReadOnlyList<string>> subRows,
        IReadOnlyList<ShopCardRow> cards, string displayColumn)
    {
        if (mainRows.Count < 2 || subRows.Count < 2)
            return ([], [], new Dictionary<int, int>(), [], []);
        var mainHeader = HeaderMap(mainRows[0]);
        var subHeader = HeaderMap(subRows[0]);
        RequireHeader(mainHeader, "id", "name", displayColumn, "displayOrder", "shopView");
        RequireHeader(subHeader, "id", "name", displayColumn, "displayOrder", "shopView", "pageType");

        // Cards reference the sheet name key; localized text comes from the selected schema.
        var usedSub = new HashSet<string>(cards.Select(c => c.SubCategory), StringComparer.OrdinalIgnoreCase);

        var shopVisibleSubs = new List<ShopSubcategoryRow>();
        var allSubsByKey = new Dictionary<string, ShopSubcategoryRow>(StringComparer.OrdinalIgnoreCase);
        var subByKey = new Dictionary<string, ShopSubcategoryRow>(StringComparer.OrdinalIgnoreCase);
        var allSubs = new List<ShopSubcategoryRow>();
        for (var i = 1; i < subRows.Count; i++)
        {
            var row = subRows[i];
            if (!IsCategoryDataRow(row, subHeader))
                continue;
            if (Cell(row, subHeader, "name").Length > 0 && IntCell(row, subHeader, "pageType") is >= 0 and <= 2)
                allSubs.Add(new ShopSubcategoryRow(
                    IntCell(row, subHeader, "id"),
                    FirstNonEmpty(Cell(row, subHeader, displayColumn), Cell(row, subHeader, "name")),
                    IntCell(row, subHeader, "displayOrder"),
                    IntCell(row, subHeader, "shopView"),
                    IntCell(row, subHeader, "pageType")));
            if (IntCell(row, subHeader, "shopView") == 0)
                continue;
            var key = Cell(row, subHeader, "name");
            if (key.Length == 0)
                continue;
            var pageType = IntCell(row, subHeader, "pageType");
            if (pageType is < 0 or > 2)
                throw new InvalidOperationException($"Shop SubCategory {key} has unsupported pageType {pageType}.");
            var sub = new ShopSubcategoryRow(
                IntCell(row, subHeader, "id"),
                FirstNonEmpty(Cell(row, subHeader, displayColumn), key),
                IntCell(row, subHeader, "displayOrder"),
                IntCell(row, subHeader, "shopView"),
                pageType);
            shopVisibleSubs.Add(sub);
            allSubsByKey[key] = sub;
            // Card-linked trees still omit unused names; id-400 reattaches defaults later.
            if (usedSub.Contains(key))
                subByKey[key] = sub;
        }

        var usedMain = new HashSet<string>(cards.Select(c => c.MainCategory), StringComparer.OrdinalIgnoreCase);
        var mainDefaultIds = new Dictionary<int, int>();
        var mains = new List<ShopCategoryRow>();
        var shopVisibleMains = new List<ShopCategoryRow>();
        for (var i = 1; i < mainRows.Count; i++)
        {
            var row = mainRows[i];
            if (!IsCategoryDataRow(row, mainHeader))
                continue;
            if (IntCell(row, mainHeader, "shopView") == 0)
                continue;
            var key = Cell(row, mainHeader, "name");
            if (key.Length == 0)
                continue;
            var mainId = IntCell(row, mainHeader, "id");
            var displayName = FirstNonEmpty(Cell(row, mainHeader, displayColumn), key);
            var displayOrder = IntCell(row, mainHeader, "displayOrder");
            // Optional column: id-400 MainCategory.shopDefaultSubCategory (Characters/Etc/Main).
            // Recorded for every shop-visible main, including mains with no on-sale cards.
            var defaultSub = Cell(row, mainHeader, "shopDefaultSubCategory");
            if (defaultSub.Length > 0 && allSubsByKey.TryGetValue(defaultSub, out var defaultRow))
                mainDefaultIds[mainId] = defaultRow.Id;
            shopVisibleMains.Add(new ShopCategoryRow(mainId, displayName, displayOrder, []));
            if (!usedMain.Contains(key))
                continue;
            var subs = cards
                .Where(c => string.Equals(c.MainCategory, key, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.SubCategory)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(n => subByKey.TryGetValue(n, out var s) ? s : null)
                .OfType<ShopSubcategoryRow>()
                .ToList();
            mains.Add(new ShopCategoryRow(mainId, displayName, displayOrder, subs));
        }

        return (mains, shopVisibleSubs, mainDefaultIds, shopVisibleMains, allSubs);
    }

    private static string FirstNonEmpty(string a, string b) => a.Length > 0 ? a : b;

    private static Dictionary<string, int> CategoryIds(IReadOnlyList<IReadOnlyList<string>> rows, string sheet)
    {
        if (rows.Count == 0)
            throw new InvalidOperationException($"Shop catalog {sheet} has no header.");
        var header = HeaderMap(rows[0]);
        RequireHeader(header, "id", "name");
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows.Skip(1))
        {
            if (!IsCategoryDataRow(row, header))
                continue;
            var key = Cell(row, header, "name");
            if (key.Length == 0)
                throw new InvalidOperationException($"Shop catalog {sheet} has an empty name.");
            // sub_A01910/sub_A01990 return the first exact name match in sheet order.
            ids.TryAdd(key, IntCell(row, header, "id"));
        }
        return ids;
    }

    private static void ValidatePrices(string money, IEnumerable<int> prices, int key, ShopCatalogSchema schema)
    {
        // The CN legacy workbooks price items in a third currency, cashCN; the
        // catalog carries the label through to the card and the wallet layer of a
        // CN build resolves the charge.
        if (money is not ("lucci" or "cash")
            && !(schema == ShopCatalogSchema.Legacy && money == "cashCN"))
            throw new InvalidOperationException($"Shop catalog key {key} has unsupported moneyType '{money}'.");
        if (prices.Any(p => p < 0))
            throw new InvalidOperationException($"Shop catalog key {key} has a negative price.");
    }

    private static int ResolveCategory(
        IReadOnlyDictionary<string, int> ids,
        string key,
        string sheet,
        int rowKey,
        ShopCatalogSchema schema,
        Action<string, string> reportMissing)
    {
        if (ids.TryGetValue(key, out var id))
            return id;
        if (schema == ShopCatalogSchema.Legacy)
        {
            // The 812 lookups (0x993190 main / 0x993280 sub) return 0 for an unmatched
            // name and the StockSet loader keeps the row; mirror that instead of failing.
            reportMissing(sheet, key);
            return 0;
        }
        throw new InvalidOperationException(
            $"Shop catalog StockSet {rowKey} references missing {sheet} '{key}'.");
    }
}
