using System.Collections.Frozen;
using System.Globalization;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.ItemEnhancement.Data;

/// <summary>Native item eligibility, equipment category and duration encoding for enhancement validation.</summary>
internal sealed record EnhancementItem(int Id, bool IsEnchant, ItemEnhancementKind Kind, string DurationType,
    bool IsModificatable = false, string SubCategory = "", bool IsUnlimited = false, int AdvancementClass = 0)
{
    internal bool IsModificationWeapon => IsModificatable && Kind == ItemEnhancementKind.Weapon
        && SubCategory is "Weapon_Rifle" or "Weapon_ShotGun" or "Weapon_Sniper" or "SubWeapon";
    internal bool RequiresArticle => IsModificationWeapon || IsEnchant && Kind is ItemEnhancementKind.Weapon or ItemEnhancementKind.Clothing;

    /// <summary>
    /// Whether a permanent conversion of this item yields equipment that needs an Article identity. The native flag
    /// is separate from Enchant and Modification eligibility, so some converted outputs are covered only by it.
    /// </summary>
    internal bool ConversionRequiresArticle => IsUnlimited && Kind is ItemEnhancementKind.Weapon or ItemEnhancementKind.Clothing;
}

/// <summary>One native set: the member items the client's set row lists, in column order.</summary>
internal sealed record EnhancementSet(int Id, IReadOnlyList<int> Members);

/// <summary>Resolves enhancement eligibility and presence attributes from one client's bounded native workbook.</summary>
internal sealed class EnhancementCatalog
{
    private readonly IReadOnlyDictionary<int, AttributeDefinition> _attributes;
    private readonly IReadOnlyDictionary<int, BuffDefinition> _buffs;
    private readonly IReadOnlyDictionary<ItemEnhancementKind, int> _categories;

    private EnhancementCatalog(Dictionary<int, EnhancementItem> items, Dictionary<int, AttributeDefinition> attributes,
        Dictionary<int, BuffDefinition> buffs, Dictionary<ItemEnhancementKind, int> categories, Dictionary<int, EnhancementSet> sets)
    {
        Sets = sets.ToFrozenDictionary();
        Items = items.ToFrozenDictionary();
        _attributes = attributes.ToFrozenDictionary();
        _buffs = buffs.ToFrozenDictionary();
        _categories = categories.ToFrozenDictionary();
        AttributeIds = attributes.Keys.ToFrozenSet();
    }

    internal IReadOnlyDictionary<int, EnhancementItem> Items { get; }

    /// <summary>Gets the native sets by identity; empty unless the catalog was loaded with its set sheet.</summary>
    internal IReadOnlyDictionary<int, EnhancementSet> Sets { get; }
    internal IReadOnlySet<int> AttributeIds { get; }

    internal void RequireModificationAttribute(int id)
    {
        // kr-1256 sub_A0DED0 applies paired signed content values using their native RandomTable conversions.
        if (id is < 101 or > 156 || !_attributes.TryGetValue(id, out var attribute) || attribute.Value != 3
            || attribute.Category != _categories[ItemEnhancementKind.Weapon]
            || attribute.FirstBuff <= 0 || attribute.SecondBuff <= 0 || attribute.FirstBuff == attribute.SecondBuff
            || attribute.ThirdBuff != 0 || attribute.FourthBuff != 0
            || !_buffs.TryGetValue(attribute.FirstBuff, out var first) || !_buffs.TryGetValue(attribute.SecondBuff, out var second)
            || !IsModificationEffect(first) || !IsModificationEffect(second))
            throw new InvalidDataException($"ItemAttr {id} is not a native paired modification effect.");
    }

    /// <summary>
    /// Requires a native laboratory grade-up attribute and returns the value word of each of its buffs. The client reads one
    /// signed word per buff and multiplies it by the buff's conversion (kr-1256 sub_A0DED0), so a fixed RandomTable range
    /// (minimum equal to maximum, as every kr-1256 grade-up buff has) is the one value the Article can carry.
    /// </summary>
    internal int[] AdvancementValues(int id)
    {
        if (!_attributes.TryGetValue(id, out var attribute) || attribute.Category != _categories[ItemEnhancementKind.Clothing]
            || attribute.Value != 0 || attribute.Grade != 0 || attribute.Weight != 0)
            throw new InvalidDataException($"ItemAttr {id} is not a native clothing grade-up attribute.");
        var buffs = new[] { attribute.FirstBuff, attribute.SecondBuff, attribute.ThirdBuff, attribute.FourthBuff };
        var count = buffs.TakeWhile(buff => buff != 0).Count();
        if (count == 0 || buffs.Skip(count).Any(buff => buff != 0))
            throw new InvalidDataException($"ItemAttr {id} has no contiguous native buff list.");
        return buffs.Take(count).Select(buff =>
        {
            if (!_buffs.TryGetValue(buff, out var definition) || definition.Minimum != definition.Maximum
                || definition.Minimum != decimal.Truncate(definition.Minimum) || definition.Minimum is < 0 or > int.MaxValue)
                throw new InvalidDataException($"ItemAttr {id} buff {buff} does not carry one fixed whole value.");
            return (int)definition.Minimum;
        }).ToArray();
    }

    private static bool IsModificationEffect(BuffDefinition buff) => buff.Name switch
    {
        "modificationAccuracy" or "modificationReloadSpeed" or "modificationRecoil" => buff.Conversion == -0.01m,
        "modificationDamage" or "modificationCritical" or "modificationVelocity" or "modificationMoveSpeed" or "modificationEiz" => buff.Conversion == 0.01m,
        _ => false,
    };

    /// <summary>Loads the native item and attribute relationships without borrowing another build's selectors.</summary>
    internal static EnhancementCatalog Load(IClientDataStore store, string path, int maxBytes,
        int maxRows, int maxColumns, int maxCellCharacters, bool includeSets = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxColumns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCellCharacters, 1);
        // The shop and pet rows read the same workbook while the server starts; the store's shared parse serves all of them.
        string[] names = includeSets ? [.. Workbook.RequiredSheets, Workbook.SetSheet] : Workbook.RequiredSheets;
        var sheets = SpreadsheetWorkbook.Load(WorkbookFile.FromStore(store, path), maxBytes, "max_workbook_bytes", names);
        Workbook.RequireLimits(sheets, path, maxRows, maxColumns, maxCellCharacters, names);
        var main = Categories(sheets["MainCategory"], "MainCategory", allowRepeatedNames: false);
        var sub = Categories(sheets["SubCategory"], "SubCategory", allowRepeatedNames: true);
        var categories = new Dictionary<ItemEnhancementKind, int>();
        foreach (var (name, kind) in new[] { ("Weapons", ItemEnhancementKind.Weapon), ("Clothes", ItemEnhancementKind.Clothing) })
        {
            if (!main.TryGetValue(name, out var id))
                throw new InvalidDataException($"Enhancement catalog has no MainCategory '{name}'.");
            categories.Add(kind, id);
        }
        return new EnhancementCatalog(ParseItems(sheets["ShopItem"], main, sub, categories),
            ParseAttributes(sheets["ItemAttr"]), ParseBuffs(sheets["RandomTable"]), categories,
            includeSets ? ParseSets(sheets[Workbook.SetSheet]) : []);
    }

    private static Dictionary<int, EnhancementSet> ParseSets(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var table = new Table("SetItem", rows, "setId", "setCnt");
        var result = new Dictionary<int, EnhancementSet>();
        foreach (var row in table.Rows)
        {
            if (!table.TryKey(row, "setId", out var id)) continue;
            // kr-1256 sub_DCA620 reads set+56 members from set+64; the native sheet has ten member columns.
            // A row may list more ids than setCnt, and the client uses only the first setCnt of them.
            var count = table.Integer(row, "setCnt");
            if (count is < 1 or > 10) throw new InvalidDataException($"SetItem {id} has invalid setCnt {count}.");
            var members = new int[count];
            for (var index = 0; index < count; index++)
            {
                var column = $"ShopItemID{index + 1:00}";
                if (!table.HasColumn(column)) throw new InvalidDataException($"Enhancement catalog SetItem is missing '{column}'.");
                members[index] = table.Integer(row, column, emptyIsZero: true);
            }
            if (members.Any(member => member <= 0) || members.Distinct().Count() != count)
                throw new InvalidDataException($"SetItem {id} has an empty or repeated member inside its setCnt.");
            if (!result.TryAdd(id, new(id, Array.AsReadOnly(members))))
                throw new InvalidDataException($"Enhancement catalog repeats SetItem {id}.");
        }
        return result;
    }

    /// <summary>Requires the configured selector to be the native one-slot zero-valued enhancement marker.</summary>
    internal void RequirePresenceAttribute(int attributeId, int randomTableId, ItemEnhancementKind kind)
    {
        if (!_categories.TryGetValue(kind, out var category))
            throw new InvalidDataException($"Enhancement presence attribute has unsupported kind '{kind}'.");
        if (!_attributes.TryGetValue(attributeId, out var attribute) || attribute.Category != category
            || attribute.Value != 2 || attribute.Grade != 1 || attribute.Weight <= 0
            || attribute.FirstBuff != randomTableId || attribute.SecondBuff != 0
            || attribute.ThirdBuff != 0 || attribute.FourthBuff != 0)
            throw new InvalidDataException($"ItemAttr {attributeId} is not a single-slot presence attribute for {kind}.");
        if (!_buffs.TryGetValue(randomTableId, out var buff) || buff.Minimum != 0 || buff.Maximum != 0 || buff.Conversion != 0)
            throw new InvalidDataException($"RandomTable {randomTableId} does not encode presence with a zero value.");
        var expectedName = kind == ItemEnhancementKind.Weapon ? "enchantWeapon1st" : "enchantArmor";
        if (!string.Equals(buff.Name, expectedName, StringComparison.Ordinal))
            throw new InvalidDataException($"RandomTable {randomTableId} is not the native {kind} enhancement effect.");
    }

    private static Dictionary<string, int> Categories(IReadOnlyList<IReadOnlyList<string>> rows, string name, bool allowRepeatedNames)
    {
        var table = new Table(name, rows, "id", "name");
        var ids = new HashSet<int>();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in table.Rows)
        {
            if (!table.TryKey(row, "id", out var id)) continue;
            var category = table.Required(row, "name");
            if (!ids.Add(id)) throw new InvalidDataException($"Enhancement catalog repeats {name} id {id}.");
            if (!result.TryAdd(category, id))
            {
                if (!allowRepeatedNames) throw new InvalidDataException($"Enhancement catalog repeats {name} name '{category}'.");
                // Native catalogues repeat unrelated subcategory names; they cannot establish a unique identity.
                result[category] = -1;
            }
        }
        return result;
    }

    private static Dictionary<int, EnhancementItem> ParseItems(IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyDictionary<string, int> main, IReadOnlyDictionary<string, int> sub,
        IReadOnlyDictionary<ItemEnhancementKind, int> categories)
    {
        var table = new Table("ShopItem", rows, "itemId", "mainCategory", "subCategory", "durationType");
        var result = new Dictionary<int, EnhancementItem>();
        foreach (var row in table.Rows)
        {
            if (!table.TryKey(row, "itemId", out var id)) continue;
            var mainName = table.Cell(row, "mainCategory");
            var subName = table.Cell(row, "subCategory");
            var category = 0;
            if (mainName.Length != 0 && !main.TryGetValue(mainName, out category))
                throw new InvalidDataException($"ShopItem {id} references missing MainCategory '{mainName}'.");
            if (subName.Length != 0 && !sub.ContainsKey(subName))
                throw new InvalidDataException($"ShopItem {id} references missing SubCategory '{subName}'.");
            var kind = category == categories[ItemEnhancementKind.Weapon] ? ItemEnhancementKind.Weapon
                : category == categories[ItemEnhancementKind.Clothing] ? ItemEnhancementKind.Clothing : ItemEnhancementKind.None;
            // kr-1256 ShopItem2 declares the flag separately from MainCategory; kr-812 has no isEnchant column.
            var eligibility = table.HasColumn("isEnchant") ? table.Integer(row, "isEnchant", emptyIsZero: true) : 0;
            if (eligibility is not (0 or 1)) throw new InvalidDataException($"ShopItem {id} has invalid isEnchant {eligibility}.");
            // kr-1256 sub_A17CF0 loads metadata +308; kr-1256 sub_D3A740 action 12 checks it for Article targets.
            // Count-based part registration in kr-1256 sub_DF4120 uses inventory quantity and configured membership instead.
            var modification = table.HasColumn("isModificatable") ? table.Integer(row, "isModificatable", emptyIsZero: true) : 0;
            if (modification is not (0 or 1)) throw new InvalidDataException($"ShopItem {id} has invalid isModificatable {modification}.");
            // kr-1256 sub_A1E3B0 admits a ChangeUnlimited target (or every member of a set) only when metadata +312 is 1;
            // kr-812 has no isUnlimited column.
            var unlimited = table.HasColumn("isUnlimited") ? table.Integer(row, "isUnlimited", emptyIsZero: true) : 0;
            if (unlimited is not (0 or 1)) throw new InvalidDataException($"ShopItem {id} has invalid isUnlimited {unlimited}.");
            // kr-1256 sub_A1CDB0 takes a set's class from the largest member isAdvancedable (metadata +304);
            // kr-812 has no such column.
            var advancement = table.HasColumn("isAdvancedable") ? table.Integer(row, "isAdvancedable", emptyIsZero: true) : 0;
            if (advancement is < 0 or > 4) throw new InvalidDataException($"ShopItem {id} has invalid isAdvancedable {advancement}.");
            if (!result.TryAdd(id, new(id, eligibility == 1, kind, table.Required(row, "durationType"), modification == 1, subName,
                    unlimited == 1, advancement)))
                throw new InvalidDataException($"Enhancement catalog repeats ShopItem {id}.");
        }
        return result;
    }

    private static Dictionary<int, AttributeDefinition> ParseAttributes(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var table = new Table("ItemAttr", rows, "index", "value", "category", "grade", "weight",
            "buffIndex_1", "buffIndex_2", "buffIndex_3", "buffIndex_4");
        var result = new Dictionary<int, AttributeDefinition>();
        foreach (var row in table.Rows)
        {
            if (!table.TryKey(row, "index", out var id)) continue;
            var value = new AttributeDefinition(table.Integer(row, "category"), table.Integer(row, "value"),
                table.Integer(row, "grade"), table.Integer(row, "weight", emptyIsZero: true),
                table.Integer(row, "buffIndex_1", emptyIsZero: true), table.Integer(row, "buffIndex_2", emptyIsZero: true),
                table.Integer(row, "buffIndex_3", emptyIsZero: true), table.Integer(row, "buffIndex_4", emptyIsZero: true));
            if (value.Category <= 0 || value.Value < 0 || value.Grade < 0 || value.Weight < 0
                || value.FirstBuff < 0 || value.SecondBuff < 0 || value.ThirdBuff < 0 || value.FourthBuff < 0)
                throw new InvalidDataException($"Enhancement catalog ItemAttr {id} has invalid metadata.");
            if (!result.TryAdd(id, value)) throw new InvalidDataException($"Enhancement catalog repeats ItemAttr {id}.");
        }
        return result;
    }

    private static Dictionary<int, BuffDefinition> ParseBuffs(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var table = new Table("RandomTable", rows, "buffIndex", "buffName", "min", "max", "conv");
        var result = new Dictionary<int, BuffDefinition>();
        foreach (var row in table.Rows)
        {
            if (!table.TryKey(row, "buffIndex", out var id)) continue;
            var value = new BuffDefinition(table.Required(row, "buffName"), table.Decimal(row, "min"),
                table.Decimal(row, "max"), table.Decimal(row, "conv"));
            if (!result.TryAdd(id, value)) throw new InvalidDataException($"Enhancement catalog repeats RandomTable {id}.");
        }
        return result;
    }

    private sealed record AttributeDefinition(int Category, int Value, int Grade, int Weight,
        int FirstBuff, int SecondBuff, int ThirdBuff, int FourthBuff);
    private sealed record BuffDefinition(string Name, decimal Minimum, decimal Maximum, decimal Conversion);

    private sealed class Table
    {
        private readonly string _name;
        private readonly Dictionary<string, int> _columns = new(StringComparer.Ordinal);

        internal Table(string name, IReadOnlyList<IReadOnlyList<string>> rows, params string[] required)
        {
            _name = name;
            if (rows.Count == 0) throw new InvalidDataException($"Enhancement catalog {name} has no header.");
            for (var index = 0; index < rows[0].Count; index++)
            {
                var column = rows[0][index].Trim();
                if (column.Length > 0 && !_columns.TryAdd(column, index))
                    throw new InvalidDataException($"Enhancement catalog {name} repeats column '{column}'.");
            }
            foreach (var column in required)
                if (!_columns.ContainsKey(column)) throw new InvalidDataException($"Enhancement catalog {name} is missing '{column}'.");
            Rows = rows.Skip(1);
        }

        internal IEnumerable<IReadOnlyList<string>> Rows { get; }
        internal bool HasColumn(string column) => _columns.ContainsKey(column);
        internal string Cell(IReadOnlyList<string> row, string column) =>
            _columns[column] < row.Count ? row[_columns[column]].Trim() : string.Empty;
        internal string Required(IReadOnlyList<string> row, string column) => Cell(row, column) is { Length: > 0 } text ? text
            : throw new InvalidDataException($"Enhancement catalog {_name}.{column} is empty.");
        internal int Integer(IReadOnlyList<string> row, string column, bool emptyIsZero = false)
        {
            var text = Cell(row, column);
            if (emptyIsZero && text.Length == 0) return 0;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value
                : throw new InvalidDataException($"Enhancement catalog {_name}.{column} is not an i32.");
        }
        internal decimal Decimal(IReadOnlyList<string> row, string column) =>
            decimal.TryParse(Required(row, column), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value
                : throw new InvalidDataException($"Enhancement catalog {_name}.{column} is not a finite decimal.");
        internal bool TryKey(IReadOnlyList<string> row, string column, out int id)
        {
            id = 0;
            var text = Cell(row, column);
            if (text.Length == 0 || text.StartsWith('#')) return false;
            id = Integer(row, column);
            if (id < 0) throw new InvalidDataException($"Enhancement catalog {_name}.{column} is negative.");
            return id != 0;
        }
    }

    private static class Workbook
    {
        internal static readonly string[] RequiredSheets = ["ShopItem", "MainCategory", "SubCategory", "ItemAttr", "RandomTable"];
        internal const string SetSheet = "SetItem";

        /// <summary>Applies the row's catalog bounds to the parsed sheets, which the shared parse does not know.</summary>
        internal static void RequireLimits(IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> sheets, string path,
            int maxRows, int maxColumns, int maxCellCharacters, IEnumerable<string> names)
        {
            var rows = 0;
            foreach (var name in names)
                foreach (var row in sheets[name])
                {
                    if (++rows > maxRows)
                        throw new InvalidDataException($"Enhancement workbook '{path}' exceeds max_catalog_rows.");
                    for (var column = 0; column < row.Count; column++)
                    {
                        var text = row[column];
                        if (text.Length == 0) continue;
                        // Empty trailing cells are styling, not columns; a value past the bound is not.
                        if (column >= maxColumns)
                            throw new InvalidDataException($"Enhancement workbook '{path}' exceeds max_catalog_columns.");
                        if (text.Length > maxCellCharacters)
                            throw new InvalidDataException($"Enhancement workbook '{path}' exceeds max_cell_characters.");
                    }
                }
        }
    }
}
