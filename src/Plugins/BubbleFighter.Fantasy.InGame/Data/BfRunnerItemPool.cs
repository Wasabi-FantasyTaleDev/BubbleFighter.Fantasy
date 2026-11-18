using System.Globalization;
using BubbleFighter.Fantasy.Contracts.ClientData;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>
/// The items a BFRunner item box can hold, read from the client's <c>Config/ItemSpec4.xml</c> sheet
/// <c>ItemSpecBFRunner</c> (the sheet kr-1256 sub_710EA0 selects for round mode 21). The sheet is transposed: its
/// first column names each field and every later column is one item. The client documents <c>isEnabled</c> as
/// "appears in the random box" and <c>itemWeight</c> as the item's drop chance; the weights are relative.
/// </summary>
internal sealed class BfRunnerItemPool
{
    /// <summary>The client path of the workbook that holds the sheet.</summary>
    internal const string WorkbookPath = "Config/ItemSpec4.xml";

    /// <summary>The worksheet name.</summary>
    internal const string SheetName = "ItemSpecBFRunner";

    private readonly (int Item, double Weight)[] _items;
    private readonly double _total;

    internal BfRunnerItemPool(IReadOnlyList<(int Item, double Weight)> items)
    {
        if (items.Count == 0) throw new InvalidDataException($"{SheetName} offers no item for the random box.");
        foreach (var (item, weight) in items)
            if (item <= 0 || !double.IsFinite(weight) || weight <= 0)
                throw new InvalidDataException($"{SheetName} item {item} has weight {weight}.");
        _items = [.. items];
        _total = _items.Sum(entry => entry.Weight);
    }

    /// <summary>Gets the box items and their relative weights.</summary>
    internal IReadOnlyList<(int Item, double Weight)> Items => _items;

    /// <summary>Picks one item by weight; <paramref name="roll"/> is uniform in [0, 1).</summary>
    internal int Pick(double roll)
    {
        var target = roll * _total;
        foreach (var (item, weight) in _items)
        {
            if (target < weight) return item;
            target -= weight;
        }
        return _items[^1].Item;
    }

    /// <summary>Reads the pool from the client data store.</summary>
    internal static BfRunnerItemPool Load(IClientDataStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var file = WorkbookFile.FromStore(store, WorkbookPath);
        var rows = SpreadsheetWorkbook.Load(file, file.Length, "bf_runner", [SheetName])[SheetName];
        return Parse(rows);
    }

    /// <summary>Builds the pool from the sheet's rows: enabled items with a positive weight.</summary>
    internal static BfRunnerItemPool Parse(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        IReadOnlyList<string> Row(string field) =>
            rows.FirstOrDefault(row => row.Count > 0 && row[0].Trim() == field)
            ?? throw new InvalidDataException($"{SheetName} has no '{field}' row.");
        var ids = Row("id");
        var enabled = Row("isEnabled");
        var weights = Row("itemWeight");
        List<(int, double)> items = [];
        // Column 0 names the field and column 1 describes it; the items start at column 2.
        for (var column = 2; column < ids.Count; column++)
        {
            var idText = ids[column].Trim();
            if (idText.Length == 0) continue;
            if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                throw new InvalidDataException($"{SheetName} item id '{idText}' is not a number.");
            if (Cell(enabled, column) != "1") continue;
            var weightText = Cell(weights, column);
            if (!double.TryParse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight))
                throw new InvalidDataException($"{SheetName} item {id} weight '{weightText}' is not a number.");
            if (weight > 0) items.Add((id, weight));
        }
        return new(items);
    }

    private static string Cell(IReadOnlyList<string> row, int column) => column < row.Count ? row[column].Trim() : string.Empty;
}
