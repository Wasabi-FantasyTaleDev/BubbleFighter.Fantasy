using System.Collections.Immutable;
using System.Globalization;
using BubbleFighter.Fantasy.Contracts.ClientData;

namespace BubbleFighter.Fantasy.Pets.Data;

/// <summary>Resolves pet rules from the build's native ShopItem2 workbook under explicit resource limits.</summary>
internal static class PetCatalogLoader
{
    /// <summary>Reads item identities, category links, skill selectors and their unscaled native buff ranges.</summary>
    internal static PetCatalog Load(IClientDataStore store, string path, int maxBytes,
        int maxRows, int maxColumns, int maxCellCharacters)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxColumns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCellCharacters, 1);
        // Three rows of a build load this catalog; a store with compiled client data reads it back instead of
        // parsing the workbook. The limits are inputs, so a catalog accepted under other limits is never returned.
        var inputs = new CompiledInputs().Add("path", path).Add("max_bytes", maxBytes).Add("max_rows", maxRows)
            .Add("max_columns", maxColumns).Add("max_cell_characters", maxCellCharacters).ToString();
        return CompiledClientData.Load(store, PackedPetCatalog.Catalog, inputs,
            data => Build(data, path, maxBytes, maxRows, maxColumns, maxCellCharacters));
    }

    private static PetCatalog Build(IClientDataStore store, string path, int maxBytes,
        int maxRows, int maxColumns, int maxCellCharacters)
    {
        // The store is given the limit, so it refuses an oversized workbook before unpacking it, and the workbook
        // is parsed as it is unpacked instead of being read whole. The size is checked here as well: a store is
        // not trusted to have enforced it.
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> tables;
        using (var stream = store.OpenRead(path, maxBytes))
        {
            var length = stream.Length;
            if (length == 0 || length > maxBytes)
                throw new InvalidDataException($"Pet workbook '{path}' is empty or exceeds max_workbook_bytes.");
            tables = PetWorkbook.Read(stream, length, path, maxRows, maxColumns, maxCellCharacters);
        }
        var categories = ParseCategories(tables["SubCategory"]);
        var items = ParseItems(tables["ShopItem"], categories);
        var petCategories = items.Values.Where(item => item.Function == PetCatalog.PetFunction)
            .Select(item => item.Category).Distinct().ToArray();
        if (petCategories.Length != 1 || petCategories[0] <= 0)
            throw new InvalidDataException("Pet catalog requires one resolved subcategory for function10001 items.");
        var buffs = ParseBuffs(tables["RandomTable"], out var inverted);
        var skills = ParseSkills(tables["ItemAttr"], buffs, inverted);
        return new PetCatalog(items, skills, buffs, petCategories[0]);
    }

    // kr-1256's own workbook names two subcategories "Packages" (801 and 1003). A repeated name is ambiguous, not
    // malformed: it maps to Ambiguous here and fails only if a pet item references it. A repeated id still fails.
    private const int Ambiguous = -1;

    private static Dictionary<string, int> ParseCategories(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var table = new Table("SubCategory", rows, "id", "name");
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var ids = new HashSet<int>();
        foreach (var row in table.Rows)
        {
            if (!table.TryKey(row, "id", out var id)) continue;
            var name = table.Required(row, "name");
            if (!ids.Add(id))
                throw new InvalidDataException($"Pet catalog repeats SubCategory id {id}.");
            if (!result.TryAdd(name, id)) result[name] = Ambiguous;
        }
        return result;
    }

    private static Dictionary<int, PetItem> ParseItems(IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyDictionary<string, int> categories)
    {
        var table = new Table("ShopItem", rows, "itemId", "function", "parameter", "subCategory");
        var result = new Dictionary<int, PetItem>();
        foreach (var row in table.Rows)
        {
            if (!table.TryKey(row, "itemId", out var id)) continue;
            var categoryName = table.Cell(row, "subCategory");
            var category = 0;
            // The workbook includes uncategorized inventory rows; they still belong in the complete item scope.
            if (categoryName.Length > 0 && !categories.TryGetValue(categoryName, out category))
                throw new InvalidDataException($"Pet catalog ShopItem {id} references unknown subCategory '{categoryName}'.");
            var function = table.Integer(row, "function");
            if (category == Ambiguous)
            {
                if (function == PetCatalog.PetFunction)
                    throw new InvalidDataException($"Pet catalog pet {id} references ambiguous subCategory '{categoryName}'.");
                category = 0;
            }
            var item = new PetItem(id, function, table.Integer(row, "parameter"), category)
            {
                DurationType = table.HasColumn("durationType") ? table.Cell(row, "durationType") : null,
                MaximumDuration = table.HasColumn("maxDuration") && table.Cell(row, "maxDuration").Length > 0
                    ? table.Integer(row, "maxDuration") : null,
            };
            if (!result.TryAdd(id, item))
                throw new InvalidDataException($"Pet catalog repeats ShopItem {id}.");
        }
        return result;
    }

    private static Dictionary<int, PetSkillDefinition> ParseSkills(IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyDictionary<int, PetBuffDefinition> buffs, IReadOnlySet<int> inverted)
    {
        var table = new Table("ItemAttr", rows, "index", "value", "category", "grade", "weight",
            "buffIndex_1", "buffIndex_2", "buffIndex_3", "buffIndex_4");
        var result = new Dictionary<int, PetSkillDefinition>();
        foreach (var row in table.Rows)
        {
            if (!table.TryKey(row, "index", out var selector)) continue;
            var category = table.Integer(row, "category");
            var grade = table.Integer(row, "grade");
            var value = table.Integer(row, "value");
            var weight = table.Integer(row, "weight", emptyIsZero: true);
            if (category <= 0 || grade < 0 || value < 0 || weight < 0)
                throw new InvalidDataException($"Pet catalog ItemAttr {selector} has invalid category, grade, value or weight.");
            var references = ImmutableArray.CreateBuilder<int>(4);
            // cn-717 sub_A494B0 and kr-1256 sub_A10730 retain all four columns, including empty references.
            for (var slot = 1; slot <= 4; slot++)
            {
                var reference = table.Integer(row, $"buffIndex_{slot}", emptyIsZero: true);
                if (reference < 0 || reference != 0 && !buffs.ContainsKey(reference))
                    throw new InvalidDataException($"Pet catalog ItemAttr {selector} references missing buff {reference}.");
                references.Add(reference);
            }
            // A skill whose roll range the client's own data inverts can never be drawn; it stays defined.
            if (references.Any(inverted.Contains)) weight = 0;
            if (!result.TryAdd(selector, new PetSkillDefinition(selector, category, grade, weight, value,
                    references.MoveToImmutable())))
                throw new InvalidDataException($"Pet catalog repeats ItemAttr {selector}.");
        }
        return result;
    }

    private static Dictionary<int, PetBuffDefinition> ParseBuffs(IReadOnlyList<IReadOnlyList<string>> rows,
        out IReadOnlySet<int> inverted)
    {
        var table = new Table("RandomTable", rows, "buffIndex", "buffName", "randomGroup", "min", "max", "conv");
        var result = new Dictionary<int, PetBuffDefinition>();
        var invertedRanges = new HashSet<int>();
        inverted = invertedRanges;
        foreach (var row in table.Rows)
        {
            if (!table.TryKey(row, "buffIndex", out var index)) continue;
            var minimumText = table.Required(row, "min");
            var maximumText = table.Required(row, "max");
            var rawMinimum = Decimal(minimumText, "min", index);
            var rawMaximum = Decimal(maximumText, "max", index);
            var minimum = NativeBound(minimumText, index);
            var maximum = NativeBound(maximumText, index);
            var group = table.Integer(row, "randomGroup");
            if (group < 0)
                throw new InvalidDataException($"Pet catalog RandomTable {index} has an invalid randomGroup.");
            // tw-454's own workbook states buff 75 as min 40, max 27 (kr-1256 and cn-774 say 40, 40). Skills using an
            // inverted range are withdrawn from the weighted pool rather than disabling every pet operation.
            if (minimum > maximum || rawMinimum > rawMaximum) invertedRanges.Add(index);
            var buff = new PetBuffDefinition(index, table.Required(row, "buffName"), group,
                minimum, maximum, Decimal(table.Required(row, "conv"), "conv", index), rawMinimum, rawMaximum);
            if (!result.TryAdd(index, buff))
                throw new InvalidDataException($"Pet catalog repeats RandomTable {index}.");
        }
        return result;
    }

    private static decimal Decimal(string text, string column, int index) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value
            : throw new InvalidDataException($"Pet catalog RandomTable {index} has invalid {column}: '{text}'.");

    private static int NativeBound(string text, int index)
    {
        // kr-812 sub_99C750 calls kr-812 sub_A58E60/wcstol for bounds: the native text 1.5 therefore becomes integer 1.
        var start = text[0] is '+' or '-' ? 1 : 0;
        var end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        if (end == start || !int.TryParse(text.AsSpan(0, end), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException($"Pet catalog RandomTable {index} has a bound outside native i32.");
        return value;
    }

    private sealed class Table
    {
        private readonly string _name;
        private readonly Dictionary<string, int> _columns = new(StringComparer.Ordinal);

        internal Table(string name, IReadOnlyList<IReadOnlyList<string>> rows, params string[] required)
        {
            _name = name;
            if (rows.Count == 0) throw new InvalidDataException($"Pet catalog {name} has no header.");
            for (var index = 0; index < rows[0].Count; index++)
            {
                var key = rows[0][index].Trim();
                if (key.Length != 0 && !_columns.TryAdd(key, index))
                    throw new InvalidDataException($"Pet catalog {name} repeats column '{key}'.");
            }
            foreach (var key in required)
                if (!_columns.ContainsKey(key))
                    throw new InvalidDataException($"Pet catalog {name} is missing column '{key}'.");
            Rows = rows.Skip(1);
        }

        internal IEnumerable<IReadOnlyList<string>> Rows { get; }

        internal bool HasColumn(string column) => _columns.ContainsKey(column);

        internal string Cell(IReadOnlyList<string> row, string column) =>
            _columns[column] < row.Count ? row[_columns[column]].Trim() : string.Empty;

        internal string Required(IReadOnlyList<string> row, string column)
        {
            var text = Cell(row, column);
            return text.Length != 0 ? text
                : throw new InvalidDataException($"Pet catalog {_name}.{column} is empty.");
        }

        internal int Integer(IReadOnlyList<string> row, string column, bool emptyIsZero = false)
        {
            var text = Cell(row, column);
            if (text.Length == 0 && emptyIsZero) return 0;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value
                : throw new InvalidDataException($"Pet catalog {_name}.{column} is not an i32: '{text}'.");
        }

        internal bool TryKey(IReadOnlyList<string> row, string column, out int key)
        {
            key = 0;
            var text = Cell(row, column);
            if (text.Length == 0 || text.StartsWith('#')) return false;
            key = Integer(row, column);
            if (key < 0) throw new InvalidDataException($"Pet catalog {_name}.{column} must not be negative.");
            return key != 0;
        }
    }
}
