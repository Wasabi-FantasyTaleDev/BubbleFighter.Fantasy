using System.Globalization;
using BubbleFighter.Fantasy.Contracts.ClientData;

namespace BubbleFighter.Fantasy.Equipment.Data;

/// <summary>
/// Reads the pet item identities from the build's <c>ShopItem2</c> workbook. A pet is a
/// <c>ShopItem</c> row whose <c>function</c> column equals <see cref="PetFunctionId"/>: the
/// kr-812 client keeps that column at catalog record +292 (read by kr-812 sub_9782C0,
/// sub_978350, sub_980960 and five more sites) and the kr-1256 client at record +252
/// (kr-1256 sub_806530, sub_8066B0, sub_813B40 and eight more sites). Every such row in
/// both workbooks (kr-812 itemIds 301001..301014, kr-1256 26 rows from 301001) leaves the
/// four location columns empty, so pets hold no catalog slot and only the function column
/// can identify them. Missing headers, non-numeric cells, duplicate item ids and a sheet
/// with no pet rows fail loud instead of yielding an empty rule.
/// </summary>
internal static class PetItemCatalogLoader
{
    /// <summary>
    /// The pet function id. Both clients test the catalog record's function column against
    /// exactly this value (kr-812 rec+292, kr-1256 rec+252); it is a client constant, not a
    /// deployment tunable.
    /// </summary>
    internal const int PetFunctionId = 10001;

    /// <summary>Loads the identities of every pet item in the workbook.</summary>
    /// <param name="file">The <c>ShopItem2</c> workbook, named by its client path.</param>
    /// <param name="maxBytes">Inclusive size cap.</param>
    /// <param name="limitKey">The snake_case row option that owns <paramref name="maxBytes"/>.</param>
    /// <exception cref="InvalidOperationException">The sheet lacks <c>itemId</c> or
    /// <c>function</c>, a data cell is not an i32, an item id repeats, or no row carries the
    /// pet function.</exception>
    internal static IReadOnlySet<int> Load(WorkbookFile file, long maxBytes, string limitKey)
    {
        ArgumentNullException.ThrowIfNull(file);
        var rows = SpreadsheetWorkbook.Load(file, maxBytes, limitKey, ["ShopItem"])["ShopItem"];
        if (rows.Count == 0)
            throw new InvalidOperationException($"Pet item table {file.Name} has no header.");
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in new[] { "itemId", "function" })
        {
            var indices = rows[0].Select((value, index) => (value, index)).Where(p => p.value.Trim() == name).ToArray();
            if (indices.Length != 1)
                throw new InvalidOperationException($"Pet item table {file.Name} requires exactly one {name} column.");
            columns.Add(name, indices[0].index);
        }

        string Cell(IReadOnlyList<string> row, string name) =>
            columns[name] < row.Count ? row[columns[name]].Trim() : string.Empty;
        int Number(IReadOnlyList<string> row, string name)
        {
            var text = Cell(row, name);
            // The function column is numeric on every real row: the shop terms reader parses
            // it the same strict way for every mounted build.
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
                throw new InvalidOperationException($"Pet item table {file.Name} item {Cell(row, "itemId")} has invalid {name}: '{text}'.");
            return result;
        }

        var pets = new HashSet<int>();
        var seen = new HashSet<int>();
        foreach (var row in rows.Skip(1))
        {
            var key = Cell(row, "itemId");
            // Placeholder rows (empty ids or a leading comment) carry no item; real rows are
            // parsed strictly.
            if (string.IsNullOrEmpty(key) || key.StartsWith('#'))
                continue;
            var id = Number(row, "itemId");
            if (id <= 0)
                throw new InvalidOperationException($"Pet item table {file.Name} has nonpositive itemId {id}.");
            if (!seen.Add(id))
                throw new InvalidOperationException($"Pet item table {file.Name} repeats itemId {id}.");
            if (Number(row, "function") == PetFunctionId)
                pets.Add(id);
        }

        if (pets.Count == 0)
            throw new InvalidOperationException(
                $"Pet item table {file.Name} has no function-{PetFunctionId} rows; the shared pet slot rule would guard nothing.");
        return pets;
    }
}
