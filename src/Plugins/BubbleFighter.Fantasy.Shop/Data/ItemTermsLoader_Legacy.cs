using System.Globalization;
using BubbleFighter.Fantasy.Shop.Purchasing;

namespace BubbleFighter.Fantasy.Shop.Data;

internal sealed record ItemTerms_Legacy(int ItemId, string DurationType, int MaxDuration, int BuyAgain, int Function)
{
    internal PurchaseGrantTerm ForStock(ShopStockRow stock) => ForStock(stock, ItemTermRules.Baseline);

    internal PurchaseGrantTerm ForStock(ShopStockRow stock, ItemTermRules rules)
    {
        if (stock.ItemId != ItemId || MaxDuration <= 0)
            throw new PurchaseRejectedException("The stock and item terms do not agree.");
        // KR812 0x979500, CN717 0xA22020, ID400 0x8EB030 use count/day and bounded renewals.
        // CN279 0x662910 displays the unsigned -1 duration as unlimited; its workbook uses stock type 1.
        var kind = DurationType switch
        {
            "count" when stock.Duration > 0 => PurchaseGrantKind.Count,
            // kr-812 sub_993080 gives "endurance" item kind 3, and the shop shows the stock duration as
            // "Unlimited (Life: N)" (kr-812 sub_6D4EB0): the stock grants N life, with no time limit.
            "endurance" when rules.EnduranceIsLife && stock.Duration > 0 => PurchaseGrantKind.Life,
            "day" when stock.Duration == -1 && rules.PermanentDayStockTypes.Contains(stock.Type) => PurchaseGrantKind.Permanent,
            "day" when stock.Duration > 0 => PurchaseGrantKind.Days,
            _ => throw new PurchaseRejectedException($"Stock {stock.StockId} has unsupported {DurationType} terms."),
        };
        return new(ItemId, kind, kind == PurchaseGrantKind.Permanent ? 1 : stock.Duration,
            MaxDuration, Function == 10001 && rules.RejectRepeatFunction10001);
    }
}

/// <summary>Reads ShopItem columns without treating buyAgain as a boolean: KR812 0x9A04D0,
/// CN717 0xA4DBB0, ID400 0x91A540, CN324 0x821DE0, CN279 0x659E00.</summary>
internal static class ItemTermsLoader_Legacy
{
    internal static IReadOnlyDictionary<int, ItemTerms_Legacy> Load(WorkbookFile file, long maxBytes) =>
        Parse(SpreadsheetWorkbook.Load(file, maxBytes, ["ShopItem"])["ShopItem"]);

    internal static IReadOnlyDictionary<int, ItemTerms_Legacy> Parse(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0)
            throw new InvalidOperationException("ShopItem terms have no header.");
        var names = new[] { "itemId", "durationType", "maxDuration", "buyAgain", "function" };
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var indices = rows[0].Select((value, index) => (value, index)).Where(p => p.value.Trim() == name).ToArray();
            if (indices.Length != 1)
                throw new InvalidOperationException($"ShopItem terms require exactly one {name} column.");
            columns.Add(name, indices[0].index);
        }
        string Cell(IReadOnlyList<string> row, string name) =>
            columns[name] < row.Count ? row[columns[name]].Trim() : string.Empty;
        int Number(IReadOnlyList<string> row, string name)
        {
            var text = Cell(row, name);
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
                throw new InvalidOperationException($"ShopItem {Cell(row, "itemId")} has invalid {name}: '{text}'.");
            return result;
        }

        var result = new Dictionary<int, ItemTerms_Legacy>();
        foreach (var row in rows.Skip(1))
        {
            var key = Cell(row, "itemId");
            if (string.IsNullOrEmpty(key) || key.StartsWith('#'))
                continue;
            var id = Number(row, "itemId");
            if (id <= 0)
                throw new InvalidOperationException("ShopItem identities must be positive.");
            var durationType = Cell(row, "durationType");
            if (durationType is not ("count" or "day" or "endurance"))
                throw new InvalidOperationException($"ShopItem {id} has unknown durationType '{durationType}'.");
            var maximum = Number(row, "maxDuration");
            if (maximum <= 0)
                throw new InvalidOperationException($"ShopItem {id} has a nonpositive maxDuration.");
            var terms = new ItemTerms_Legacy(id, durationType, maximum, Number(row, "buyAgain"), Number(row, "function"));
            if (!result.TryAdd(id, terms))
                throw new InvalidOperationException($"ShopItem terms repeat itemId {id}.");
        }
        if (result.Count == 0)
            throw new InvalidOperationException("ShopItem terms contain no items.");
        return result;
    }
}

// The purchase rules a build's client applies; the shop row states them, kr-812's are the defaults.
internal sealed record ItemTermRules(IReadOnlyCollection<ushort> PermanentDayStockTypes, bool RejectRepeatFunction10001,
    bool EnduranceIsLife = false)
{
    internal static readonly ItemTermRules Baseline = new([9], RejectRepeatFunction10001: true);
}
