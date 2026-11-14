using System.Collections.Frozen;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Titles.Data;

/// <summary>Selects the audited native source of medal-claim eligibility.</summary>
internal enum TitleCatalogSchema
{
    Legacy,
    MedalFlag,
}

/// <summary>One native medal definition, with its combined identity and catalogue components.</summary>
internal sealed record TitleMedalDefinition(int MedalId, int Mode, int Number);

/// <summary>The balance to debit for one native title acquisition requirement.</summary>
internal sealed record TitleMedalRequirement(int MedalId, int Count);

/// <summary>Separates the title definition from its stock grant and unimplemented acquisition conditions.</summary>
internal sealed record TitleDefinition(int TitleId, int StockId, InventoryGrantTerm GrantTerm, int Type,
    bool Concealed, bool UsesMedals, bool HasUnsupportedBonuses, bool HasUnsupportedConditions, bool RequiresPartner,
    string AcquisitionInterval, IReadOnlyList<TitleMedalRequirement> Requirements)
{
    internal TitleAcquisitionWindow? Window { get; init; }

    /// <summary>
    /// Gets the number of Master Favorite weapons that grants this title automatically, or null when the title has no Favorite
    /// condition the server supports (TitleConditionSet, condition id 6, kr-1256 sub_113D820).
    /// </summary>
    internal int? FavoriteMasters { get; init; }
    internal IReadOnlyList<InventoryGrantTerm> BonusGrants { get; init; } = [];
    internal IReadOnlyList<BubbleFighter.Fantasy.Contracts.Titles.TitleMedalCount> BonusMedals { get; init; } = [];
}

/// <summary>Joins bounded native title requirements, stock entries and inventory grant terms.</summary>
internal sealed class TitleCatalog
{
    private TitleCatalog(Dictionary<int, TitleDefinition> titles, Dictionary<int, TitleMedalDefinition> medals)
    {
        Titles = titles.ToFrozenDictionary();
        Medals = medals.ToFrozenDictionary();
    }

    internal IReadOnlyDictionary<int, TitleDefinition> Titles { get; }
    internal IReadOnlyDictionary<int, TitleMedalDefinition> Medals { get; }

    internal static TitleCatalog Load(IClientDataStore store, string titlePath, string shopPath,
        TitleCatalogSchema schema, int maxBytes, int maxRows, int maxColumns, int maxCellCharacters,
        int? bonusMedalQuantity = null, bool favoriteConditions = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(titlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(shopPath);
        if (!Enum.IsDefined(schema)) throw new ArgumentOutOfRangeException(nameof(schema));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxColumns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCellCharacters, 1);
        if (bonusMedalQuantity is <= 0) throw new ArgumentOutOfRangeException(nameof(bonusMedalQuantity));
        var native = Read(titlePath, favoriteConditions ? ["title_dec", "title", "medal", "bonus", "TitleConditionSet"] : ["title_dec", "title", "medal", "bonus"]);
        var conditions = favoriteConditions ? ParseConditions(native["TitleConditionSet"]) : new Dictionary<int, IReadOnlyList<TitleConditionRow>>();
        var bonusRows = ReadBonusReferences(native["bonus"]);
        var shop = Read(shopPath, bonusRows.Any(bonus => bonus.Kind == 2) ? ["Stock", "ShopItem", "StockSet"] : ["Stock", "ShopItem"]);
        var medals = ParseMedals(native["medal"]);
        var definitions = new TitleWorkbookTable("title_dec", native["title_dec"], 1,
            "title_id", "Stock_id", "type", "conceal", "gettitle_date");
        if (definitions.HasColumn("use_medal") != (schema == TitleCatalogSchema.MedalFlag))
            throw new InvalidDataException("The title use_medal column does not match the configured native schema.");
        var stockIds = new HashSet<int>();
        foreach (var row in definitions.Rows)
            if (definitions.TryKey(row, "title_id", out _)) stockIds.Add(definitions.Integer(row, "Stock_id"));
        var bonusStocks = new Dictionary<int, IReadOnlyList<int>>();
        foreach (var bonus in bonusRows.Where(bonus => bonus.Kind == 1)) stockIds.Add(bonus.Reference);
        if (bonusRows.Any(bonus => bonus.Kind == 2))
        {
            bonusStocks = ParseBonusSets(shop["StockSet"], bonusRows.Where(bonus => bonus.Kind == 2)
                .Select(bonus => bonus.Reference).ToHashSet());
            foreach (var stock in bonusStocks.Values.SelectMany(ids => ids)) stockIds.Add(stock);
        }
        var stocks = ParseStocks(shop["Stock"], stockIds);
        var items = ItemRows(shop["ShopItem"]);
        var titles = new Dictionary<int, TitleDefinition>();
        foreach (var row in definitions.Rows)
        {
            if (!definitions.TryKey(row, "title_id", out var id)) continue;
            var stockId = definitions.Integer(row, "Stock_id");
            if (!stocks.TryGetValue(stockId, out var stock))
                throw new InvalidDataException($"Title {id} references missing Stock {stockId}.");
            if (!items.Rows.TryGetValue(stock.ItemId, out var item))
                throw new InvalidDataException($"Title {id} references missing ShopItem {stock.ItemId}.");
            var type = definitions.Integer(row, "type");
            if (type < 0) throw new InvalidDataException($"Title {id} has a negative native type.");
            var interval = definitions.Cell(row, "gettitle_date");
            var concealed = Boolean(definitions, row, "conceal");
            // kr-812 sub_886100 excludes type 11; kr-1256 sub_42FBF0 loads use_medal for kr-1256 sub_EA9000.
            var usesMedals = schema == TitleCatalogSchema.Legacy ? type != 11 : Boolean(definitions, row, "use_medal");
            // kr-1256 sub_EAF010 owns type 3 conditions; kr-1256 sub_E879B0 specializes partner title 400080.
            var requiresPartner = type == 11 || id == 400080;
            // Only a single Favorite condition has an authoritative server state; every other challenge stays unsupported.
            conditions.TryGetValue(id, out var rows);
            int? favoriteMasters = type == 3 && !requiresPartner && rows is [{ Condition: "Favorite" } favorite] ? favorite.AchieveValue : null;
            var unsupported = type == 3 && favoriteMasters is null || requiresPartner;
            var title = new TitleDefinition(id, stockId, Grant(stock, items.Table, item), type, concealed,
                usesMedals, false, unsupported, requiresPartner, interval, [])
            { Window = TitleAcquisitionWindow.Parse(interval), FavoriteMasters = favoriteMasters };
            if (!titles.TryAdd(id, title)) throw new InvalidDataException($"Title catalog repeats title_id {id}.");
        }
        if (titles.Count == 0) throw new InvalidDataException("The native title catalog has no definitions.");
        foreach (var id in conditions.Keys)
            if (!titles.ContainsKey(id)) throw new InvalidDataException($"Title condition references missing title {id}.");
        AttachRequirements(native["title"], titles, medals);
        AttachBonuses(bonusRows, titles, stocks, bonusStocks, items.Table, items.Rows, medals, bonusMedalQuantity);
        return new TitleCatalog(titles, medals);

        Dictionary<string, IReadOnlyList<IReadOnlyList<string>>> Read(string path, IReadOnlyList<string> sheets)
        {
            var bytes = store.Read(path, maxBytes);
            if (bytes.Length == 0 || bytes.Length > maxBytes)
                throw new InvalidDataException($"Title workbook '{path}' is empty or exceeds max_workbook_bytes.");
            return TitleWorkbook.Read(bytes, path, maxRows, maxColumns, maxCellCharacters, sheets);
        }
    }

    private static Dictionary<int, IReadOnlyList<TitleConditionRow>> ParseConditions(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var table = new TitleWorkbookTable("TitleConditionSet", rows, 1, "TitleId", "ConditionId", "AchieveValue");
        var grouped = new Dictionary<int, List<TitleConditionRow>>();
        foreach (var row in table.Rows)
        {
            if (!table.TryKey(row, "TitleId", out var id)) continue;
            var value = table.Integer(row, "AchieveValue");
            if (value <= 0) throw new InvalidDataException($"Title {id} has a nonpositive condition value.");
            if (!grouped.TryGetValue(id, out var list)) grouped.Add(id, list = []);
            list.Add(new(table.Required(row, "ConditionId"), value));
        }
        return grouped.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<TitleConditionRow>)pair.Value.AsReadOnly());
    }

    private sealed record TitleConditionRow(string Condition, int AchieveValue);

    private static Dictionary<int, TitleMedalDefinition> ParseMedals(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var table = new TitleWorkbookTable("medal", rows, 1, "mode", "medalid", "Medal_id");
        var result = new Dictionary<int, TitleMedalDefinition>();
        foreach (var row in table.Rows)
        {
            if (!table.TryKey(row, "Medal_id", out var id)) continue;
            var mode = table.Integer(row, "mode");
            var number = table.Integer(row, "medalid");
            if (mode < 0 || number is < 0 or >= 10000 || (long)mode * 10000 + number != id)
                throw new InvalidDataException($"Medal {id} does not match its native mode/number components.");
            if (!result.TryAdd(id, new(id, mode, number)))
                throw new InvalidDataException($"Title catalog repeats Medal_id {id}.");
        }
        if (result.Count == 0) throw new InvalidDataException("The native title catalog has no medal definitions.");
        return result;
    }

    private static Dictionary<int, Stock> ParseStocks(IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlySet<int> titleStockIds)
    {
        var table = new TitleWorkbookTable("Stock", rows, 0, "stockId", "itemId", "type", "duration");
        var result = new Dictionary<int, Stock>();
        foreach (var row in table.Rows)
        {
            if (!table.TryKey(row, "stockId", out var id)) continue;
            // The KR812 Stock sheet repeats an unrelated sale entry; only title-referenced stocks belong to this catalogue.
            if (!titleStockIds.Contains(id)) continue;
            var item = table.Integer(row, "itemId");
            var type = table.Integer(row, "type");
            var duration = table.Integer(row, "duration");
            if (item <= 0 || type is < 0 or > ushort.MaxValue)
                throw new InvalidDataException($"Title catalog Stock {id} has invalid item/type metadata.");
            if (!result.TryAdd(id, new(id, item, type, duration)))
                throw new InvalidDataException($"Title catalog repeats Stock {id}.");
        }
        return result;
    }

    private static (TitleWorkbookTable Table, Dictionary<int, IReadOnlyList<string>> Rows) ItemRows(
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var table = new TitleWorkbookTable("ShopItem", rows, 0, "itemId", "durationType", "maxDuration");
        var result = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var row in table.Rows)
        {
            if (!table.TryKey(row, "itemId", out var id)) continue;
            if (!result.TryAdd(id, row)) throw new InvalidDataException($"Title catalog repeats ShopItem {id}.");
        }
        return (table, result);
    }

    private static InventoryGrantTerm Grant(Stock stock, TitleWorkbookTable table, IReadOnlyList<string> row)
    {
        var durationType = table.Required(row, "durationType");
        var maximum = table.Integer(row, "maxDuration");
        if (maximum <= 0) throw new InvalidDataException($"Title item {stock.ItemId} has nonpositive maxDuration.");
        // Native title stocks in KR812/KR1256 are type 9, duration -1; day terms retain their item cap.
        if (durationType == "day" && stock.Type == 9 && stock.Duration == -1)
            return new(stock.ItemId, InventoryGrantKind.Permanent, 1, maximum, false);
        if (durationType == "day" && stock.Duration > 0 && stock.Duration <= maximum)
            return new(stock.ItemId, InventoryGrantKind.Days, stock.Duration, maximum, false);
        throw new InvalidDataException($"Title Stock {stock.Id} has unsupported native grant terms.");
    }

    private static void AttachRequirements(IReadOnlyList<IReadOnlyList<string>> rows,
        Dictionary<int, TitleDefinition> titles, IReadOnlyDictionary<int, TitleMedalDefinition> medals)
    {
        var table = new TitleWorkbookTable("title", rows, 1, "title_id", "medal_id", "count");
        var byTitle = new Dictionary<int, Dictionary<int, int>>();
        foreach (var row in table.Rows)
        {
            if (!table.TryKey(row, "title_id", out var title)) continue;
            if (!titles.ContainsKey(title)) throw new InvalidDataException($"Medal requirement references missing title {title}.");
            var medal = table.Integer(row, "medal_id");
            var count = table.Integer(row, "count");
            // kr-812 sub_4FCAB0 and kr-1256 sub_42FBF0 skip the native zero-medal placeholder row.
            if (medal == 0 && count == 0) continue;
            if (!medals.ContainsKey(medal) || count <= 0)
                throw new InvalidDataException($"Title {title} has an invalid medal requirement {medal}/{count}.");
            if (!byTitle.TryGetValue(title, out var requirements))
                byTitle.Add(title, requirements = []);
            if (!requirements.TryAdd(medal, count))
                throw new InvalidDataException($"Title {title} repeats medal requirement {medal}.");
        }
        foreach (var (id, requirements) in byTitle)
            titles[id] = titles[id] with
            {
                Requirements = Array.AsReadOnly(requirements.OrderBy(pair => pair.Key)
                    .Select(pair => new TitleMedalRequirement(pair.Key, pair.Value)).ToArray()),
            };
    }

    private static IReadOnlyList<BonusReference> ReadBonusReferences(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var table = new TitleWorkbookTable("bonus", rows, 1, "title_id", "bonustype", "bonusnum");
        var result = new List<BonusReference>();
        foreach (var row in table.Rows)
        {
            if (!table.TryKey(row, "title_id", out var title)) continue;
            var kind = table.Integer(row, "bonustype");
            var reference = table.Integer(row, "bonusnum");
            if (kind is < 0 or > 2 || reference <= 0)
                throw new InvalidDataException($"Title {title} has malformed native bonus metadata.");
            result.Add(new(title, kind, reference));
        }
        return result.AsReadOnly();
    }

    private static Dictionary<int, IReadOnlyList<int>> ParseBonusSets(IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlySet<int> referenced)
    {
        var table = new TitleWorkbookTable("StockSet", rows, 0, "stockSetId", "lucci");
        var result = new Dictionary<int, IReadOnlyList<int>>();
        foreach (var row in table.Rows)
        {
            if (!table.TryKey(row, "stockSetId", out var id) || !referenced.Contains(id)) continue;
            if (table.Integer(row, "lucci") != 0)
                throw new InvalidDataException($"Title bonus StockSet {id} requires an unsupported wallet grant.");
            var stocks = new List<int>();
            for (var slot = 1; slot <= 10; slot++)
            {
                var column = $"stockId{slot:D2}";
                if (!table.HasColumn(column)) continue;
                var text = table.Cell(row, column);
                if (text.Length == 0 || text == "0") continue;
                var stock = table.Integer(row, column);
                if (stock <= 0) throw new InvalidDataException($"Title bonus StockSet {id} has an invalid stock reference.");
                stocks.Add(stock);
            }
            if (stocks.Count == 0 || !result.TryAdd(id, stocks.AsReadOnly()))
                throw new InvalidDataException($"Title bonus StockSet {id} is empty or repeated.");
        }
        if (referenced.Any(id => !result.ContainsKey(id)))
            throw new InvalidDataException("A title bonus references a missing native StockSet.");
        return result;
    }

    private static void AttachBonuses(IReadOnlyList<BonusReference> bonuses, Dictionary<int, TitleDefinition> titles,
        IReadOnlyDictionary<int, Stock> stocks, IReadOnlyDictionary<int, IReadOnlyList<int>> sets,
        TitleWorkbookTable itemTable, IReadOnlyDictionary<int, IReadOnlyList<string>> items,
        IReadOnlyDictionary<int, TitleMedalDefinition> medals, int? bonusMedalQuantity)
    {
        foreach (var group in bonuses.GroupBy(bonus => bonus.TitleId))
        {
            if (!titles.TryGetValue(group.Key, out var definition))
                throw new InvalidDataException($"Bonus references missing title {group.Key}.");
            var grants = new List<InventoryGrantTerm>();
            var medalCredits = new Dictionary<int, int>();
            var unsupported = false;
            foreach (var bonus in group)
            {
                // The native bonus sheet identifies a medal but supplies no credit count; an explicit policy supplies that amount.
                if (bonus.Kind == 0)
                {
                    if (!medals.ContainsKey(bonus.Reference))
                        throw new InvalidDataException($"Title {group.Key} bonus references a missing native medal.");
                    if (bonusMedalQuantity is null) unsupported = true;
                    else medalCredits[bonus.Reference] = checked(medalCredits.GetValueOrDefault(bonus.Reference) + bonusMedalQuantity.Value);
                    continue;
                }
                IReadOnlyList<int> keys = bonus.Kind == 1 ? [bonus.Reference] : sets[bonus.Reference];
                foreach (var key in keys)
                {
                    if (!stocks.TryGetValue(key, out var stock) || !items.TryGetValue(stock.ItemId, out var item))
                        throw new InvalidDataException($"Title {group.Key} bonus has no native stock/item grant.");
                    grants.Add(Grant(stock, itemTable, item));
                }
            }
            if (grants.Count >= InventoryLimits.MaxInventorySlotsPerBag || grants.Any(term => term.ItemId == definition.GrantTerm.ItemId))
                throw new InvalidDataException($"Title {group.Key} has invalid or excessive acquisition bonuses.");
            titles[group.Key] = definition with
            {
                HasUnsupportedBonuses = unsupported, BonusGrants = grants.AsReadOnly(),
                BonusMedals = Array.AsReadOnly(medalCredits.OrderBy(pair => pair.Key)
                    .Select(pair => new BubbleFighter.Fantasy.Contracts.Titles.TitleMedalCount(pair.Key, pair.Value)).ToArray()),
            };
        }
    }

    private sealed record BonusReference(int TitleId, int Kind, int Reference);

    private static bool Boolean(TitleWorkbookTable table, IReadOnlyList<string> row, string column) =>
        table.Cell(row, column) switch
        {
            "1" or "true" => true,
            "" or "0" or "false" => false,
            _ => throw new InvalidDataException($"Title catalog {column} is not a native boolean."),
        };

    private sealed record Stock(int Id, int ItemId, int Type, int Duration);
}
