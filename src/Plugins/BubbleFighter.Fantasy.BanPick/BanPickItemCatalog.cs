using System.Globalization;
using BubbleFighter.Fantasy.Contracts.BanPick;
using BubbleFighter.Fantasy.Contracts.ClientData;

namespace BubbleFighter.Fantasy.BanPick;

/// <summary>One item of a build's own catalog, with the columns ban groups select by.</summary>
/// <param name="Id">The item id (<c>ShopItem.itemId</c>).</param>
/// <param name="Name">The display name in the build's language (<c>ShopItem.itemName</c>).</param>
/// <param name="MainCategory">The main category id, or zero when the row names none the build defines.</param>
/// <param name="SubCategory">The sub category id, or zero when the row names none the build defines.</param>
/// <param name="Function">The function value, or zero when the cell is empty.</param>
internal sealed record BanPickItem(int Id, string Name, int MainCategory, int SubCategory, int Function);

/// <summary>
/// The items of one build's <c>ShopItem2</c> workbook. Rows name their categories by the <c>MainCategory</c> and
/// <c>SubCategory</c> sheets' names, which are translated per build, so the names are turned back into the sheets' ids;
/// the ids are the same in every build (for example sub category 304 is the sniper rifles in CN2011, KR2015 and CN2015).
/// </summary>
internal sealed class BanPickItemCatalog
{
    private readonly Dictionary<int, BanPickItem> _items;

    internal BanPickItemCatalog(IEnumerable<BanPickItem> items)
    {
        _items = [];
        foreach (var item in items)
            if (!_items.TryAdd(item.Id, item))
                throw new InvalidOperationException($"Ban-pick item catalog repeats item {item.Id}.");
    }

    internal int Count => _items.Count;

    internal BanPickItem? Find(int id) => _items.GetValueOrDefault(id);

    /// <summary>The item's display name, or its id when the build has no such item.</summary>
    internal string NameOf(int id) => _items.TryGetValue(id, out var item) && item.Name.Length > 0
        ? item.Name : id.ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether the item belongs to the group in this build.</summary>
    internal bool InGroup(int id, BanPickGroup group)
    {
        if (group.Items.Contains(id)) return true;
        if (!_items.TryGetValue(id, out var item)) return false;
        return item.MainCategory != 0 && group.MainCategories.Contains(item.MainCategory)
            || item.SubCategory != 0 && group.SubCategories.Contains(item.SubCategory)
            || item.Function != 0 && group.Functions.Contains(item.Function);
    }

    /// <summary>Whether the group selects at least one item of this build.</summary>
    internal bool HasAny(BanPickGroup group) => group.Items.Any(_items.ContainsKey) || _items.Keys.Any(id => InGroup(id, group));

    /// <summary>Items whose name contains the text, ignoring case, in id order.</summary>
    internal IReadOnlyList<BanPickItem> Search(string text, int limit) => [.. _items.Values
        .Where(item => item.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
        .OrderBy(item => item.Id).Take(limit)];

    /// <summary>Loads the <c>ShopItem</c>, <c>MainCategory</c> and <c>SubCategory</c> sheets.</summary>
    /// <param name="file">The workbook, named by its client path.</param>
    /// <param name="maxBytes">Inclusive size cap.</param>
    /// <param name="limitKey">The row option that owns <paramref name="maxBytes"/>.</param>
    /// <exception cref="InvalidOperationException">A sheet or column is missing, an id is not a number, or an id repeats.</exception>
    internal static BanPickItemCatalog Load(WorkbookFile file, long maxBytes, string limitKey)
    {
        var sheets = SpreadsheetWorkbook.Load(file, maxBytes, limitKey, ["ShopItem", "MainCategory", "SubCategory"]);
        var mains = CategoryIds(file.Name, sheets["MainCategory"]);
        var subs = CategoryIds(file.Name, sheets["SubCategory"]);
        var rows = sheets["ShopItem"];
        if (rows.Count == 0) throw new InvalidOperationException($"Ban-pick item table {file.Name} has no ShopItem header.");
        var id = Column(file.Name, rows[0], "itemId");
        var name = Column(file.Name, rows[0], "itemName");
        var main = Column(file.Name, rows[0], "mainCategory");
        var sub = Column(file.Name, rows[0], "subCategory");
        var function = Column(file.Name, rows[0], "function");
        var items = new List<BanPickItem>();
        foreach (var row in rows.Skip(1))
        {
            var key = Cell(row, id);
            // Placeholder rows (empty ids or a leading comment) carry no item.
            if (key.Length == 0 || key.StartsWith('#')) continue;
            if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId) || itemId <= 0)
                throw new InvalidOperationException($"Ban-pick item table {file.Name} has invalid itemId '{key}'.");
            var functionText = Cell(row, function);
            var functionValue = 0;
            if (functionText.Length > 0 && !int.TryParse(functionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out functionValue))
                throw new InvalidOperationException($"Ban-pick item table {file.Name} item {itemId} has invalid function '{functionText}'.");
            items.Add(new(itemId, Cell(row, name), mains.GetValueOrDefault(Cell(row, main)),
                subs.GetValueOrDefault(Cell(row, sub)), functionValue));
        }
        return new(items);
    }

    // Category names repeat across main categories in some builds (a sub category "Weapon" under PCRoom); the first
    // id a name has wins, which is the ordinary category the shop lists first.
    private static Dictionary<string, int> CategoryIds(string file, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0) throw new InvalidOperationException($"Ban-pick item table {file} has an empty category sheet.");
        var id = Column(file, rows[0], "id");
        var name = Column(file, rows[0], "name");
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows.Skip(1))
        {
            var text = Cell(row, name);
            if (text.Length == 0 || !int.TryParse(Cell(row, id), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value == 0)
                continue;
            result.TryAdd(text, value);
        }
        return result;
    }

    private static int Column(string file, IReadOnlyList<string> header, string name)
    {
        var indices = header.Select((value, index) => (value, index)).Where(pair => pair.value.Trim() == name).ToArray();
        return indices.Length == 1 ? indices[0].index
            : throw new InvalidOperationException($"Ban-pick item table {file} requires exactly one {name} column.");
    }

    private static string Cell(IReadOnlyList<string> row, int index) => index < row.Count ? row[index].Trim() : string.Empty;
}
