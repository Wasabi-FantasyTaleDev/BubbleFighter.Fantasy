using System.Globalization;
using System.Xml;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Resolves weapon resources and ordinary catalog critical attributes from accepted equipment.</summary>
internal sealed partial class WeaponCriticalCatalog(
    IReadOnlyDictionary<int, WeaponCriticalCatalog.Specification> specifications,
    IReadOnlyDictionary<int, int> itemSlots, IReadOnlyDictionary<int, string> modeFields,
    IReadOnlyDictionary<int, WeaponCriticalCatalog.CatalogEffect[]> catalogEffects,
    int primaryDefault, int sidearmDefault, int maxWornItems, bool viewRefreshesWeapons, PetSeedEffects? pets = null)
{
    internal bool ViewRefreshesWeapons => viewRefreshesWeapons;
    internal sealed record Specification(string Family, float CriticalPercent, IReadOnlyDictionary<string, int> Replacements);
    private static readonly string[] Families = ["Pistol", "Rifle", "Shotgun", "SniperRifle"];
    private static readonly string[] ReplacementFields = ["zombiePveModeId", "hordeModeId", "sniperModeId",
        "zombieModeId", "itemModeId", "singlePvpModeId", "teamSurvivalId"];

    internal static WeaponCriticalCatalog Load(IClientDataStore data, PacketTables tables, string build,
        SceneSeedsOptions options, PetSeedEffects? pets = null)
    {
        var values = tables.ValuesOf(build);
        if (!values.TryGetValue("weapon_critical_seed_builds", out var membersRaw) || membersRaw is not string[] members
            || !members.Contains(build, StringComparer.Ordinal))
            throw new ConfigException($"Build '{build}' has no audited weapon-aware seed path.");
        if (!values.TryGetValue("weapon_critical_view_refresh", out var viewRaw) || viewRaw is not string viewText
            || !bool.TryParse(viewText, out var viewRefreshesWeapons))
            throw new ConfigException($"Build '{build}' must state the audited boolean weapon_critical_view_refresh policy.");
        if (!values.TryGetValue("weapon_critical_mode_fields", out var modesRaw) || modesRaw is not Dictionary<string, object?> modes)
            throw new ConfigException("Weapon critical seeds require audited mode replacement fields.");
        var modeFields = new Dictionary<int, string>();
        foreach (var (key, value) in modes)
        {
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var mode) || mode <= 0
                || value is not string field || !ReplacementFields.Contains(field, StringComparer.Ordinal)
                || !modeFields.TryAdd(mode, field))
                throw new ConfigException("Weapon critical mode mapping is malformed.");
        }
        if (modeFields.Count == 0) throw new ConfigException("Weapon critical mode mapping is empty.");
        if (!values.TryGetValue("weapon_critical_defaults", out var defaultsRaw)
            || defaultsRaw is not Dictionary<string, object?> defaults || defaults.Count != 2)
            throw new ConfigException("Weapon critical seeds require both native slot defaults.");
        var primaryDefault = Default("primary");
        var sidearmDefault = Default("sidearm");
        int Default(string slot) => defaults.TryGetValue(slot, out var value)
            ? Positive(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", $"{slot} default")
            : throw new ConfigException($"Weapon critical seeds lack the native {slot} default.");

        var masters = Properties(Read(options.WeaponMasterClientPath, ["WeaponSpecMaster"])["WeaponSpecMaster"]);
        var categories = Required(masters, "weaponCategory");
        var masterIds = Required(masters, "id");
        var details = Read(options.WeaponDetailClientPath, Families);
        var specifications = new Dictionary<int, Specification>();
        foreach (var family in Families)
        {
            var columns = categories.Where(pair => pair.Key >= 2 && pair.Value == family).Select(pair => pair.Key).ToArray();
            if (columns.Length != 1) throw new ConfigException($"Weapon master must identify '{family}' exactly once.");
            var column = columns[0];
            _ = Positive(Cell(masterIds, column), "master id");
            var masterChance = Chance(Property(masters, "crtChancePercent", column), "master critical chance");
            var masterReplacements = ReplacementFields.ToDictionary(field => field,
                field => Selector(Property(masters, field, column), 0), StringComparer.Ordinal);
            var properties = Properties(details[family]);
            foreach (var (index, text) in Required(properties, "id").Where(pair => pair.Key >= 2 && pair.Value.Length > 0))
            {
                var id = Positive(text, $"{family} id");
                // cn-324 sub_61DEF0 adds the parsed detail float to its category master; it is not an override.
                var chance = masterChance + Chance(Property(properties, "crtChancePercent", index), $"weapon {id} critical chance");
                if (!float.IsFinite(chance) || chance < 0 || chance > 100)
                    throw new ConfigException($"Weapon {id} has an invalid combined critical chance.");
                var spec = new Specification(family, chance, ReplacementFields.ToDictionary(field => field,
                    field => Selector(Property(properties, field, index), masterReplacements[field]), StringComparer.Ordinal));
                // id-400 sub_6B1060 reapplies duplicate columns against their master, not the preceding detail.
                // Equivalent critical projections are safe; conflicting projections still require an audited policy.
                if (!specifications.TryAdd(id, spec)
                    && (specifications[id].Family != spec.Family || specifications[id].CriticalPercent != spec.CriticalPercent
                        || ReplacementFields.Any(field => specifications[id].Replacements[field] != spec.Replacements[field])))
                    throw new ConfigException($"Weapon specification {id} has conflicting duplicate critical properties.");
                if (specifications.Count > options.WeaponCatalogMaxRecords)
                    throw new ConfigException("Weapon specifications exceed weapon_catalog_max_records.");
            }
        }
        foreach (var (id, spec) in specifications)
            foreach (var replacement in spec.Replacements.Values.Where(value => value != 0))
                if (!specifications.TryGetValue(replacement, out var target) || target.Family != spec.Family)
                    throw new ConfigException($"Weapon {id} references missing or incompatible replacement {replacement}.");

        var itemSheets = Read(options.WeaponItemsClientPath, ["ShopItem", "ShopItemSpec"]);
        var shop = itemSheets["ShopItem"];
        var header = shop.FirstOrDefault(row => row.Values.Contains("itemId") && row.Values.Contains("slotLocation"))
            ?? throw new ConfigException("Weapon item catalog lacks itemId and slotLocation columns.");
        var itemColumn = header.Single(pair => pair.Value == "itemId").Key;
        var slotColumn = header.Single(pair => pair.Value == "slotLocation").Key;
        var slots = new Dictionary<int, int>();
        foreach (var row in shop)
        {
            if (!int.TryParse(Cell(row, itemColumn), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || id is < 200000 or >= 300000) continue;
            var names = Cell(row, slotColumn).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var primary = names.Contains("sWeapon1", StringComparer.Ordinal);
            var sidearm = names.Contains("sWeapon2", StringComparer.Ordinal);
            if (!primary && !sidearm)
            {
                if (!slots.TryAdd(id, -1)) throw new ConfigException($"Weapon item {id} is duplicated.");
                continue;
            }
            if (primary && sidearm) throw new ConfigException($"Weapon item {id} occupies both seed slots.");
            // cn-324 sub_455FD0 classifies these item ids as weapons; cn-324 sub_805DE0 subtracts 200000.
            // Native shop lists include unavailable specifications; reject them when selected, before publishing a round.
            if ((specifications.TryGetValue(id - 200000, out var spec) && (spec.Family == "Pistol") != sidearm)
                || !slots.TryAdd(id, primary ? 0 : 1))
                throw new ConfigException($"Weapon item {id} has an incompatible or duplicate seed-slot specification.");
        }
        if (!specifications.TryGetValue(primaryDefault, out var primarySpec) || primarySpec.Family != "Rifle"
            || !specifications.TryGetValue(sidearmDefault, out var sidearmSpec) || sidearmSpec.Family != "Pistol")
            throw new ConfigException("Weapon catalog lacks the native primary or sidearm default.");
        var effects = CriticalEffects(itemSheets["ShopItemSpec"]);
        return new(specifications, slots, modeFields, effects, primaryDefault, sidearmDefault, options.WeaponMaxWornItems,
            viewRefreshesWeapons, pets);

        Dictionary<string, List<Dictionary<int, string>>> Read(string path, string[] wanted)
        {
            try
            {
                // Streamed from the store: reading the workbook whole first put a large array per file on the heap.
                var length = data.Length(path);
                if (length == 0 || length > options.WeaponWorkbookMaxBytes)
                    throw new ConfigException($"'{path}' exceeds weapon_workbook_max_bytes or is empty.");
                using var stream = data.OpenRead(path);
                return ReadWorkbook(stream, path, wanted, options.WeaponCatalogMaxRecords, options.WeaponWorkbookMaxColumns,
                    options.WeaponWorkbookMaxBytes);
            }
            catch (Exception failure) when (failure is FileNotFoundException or XmlException or InvalidOperationException)
            {
                throw new ConfigException($"Weapon critical catalog cannot read '{path}': {failure.Message}");
            }
        }
    }

    /// <summary>
    /// Reads the wanted worksheets as rows of cells by column. Streamed rather than loaded as a document tree, which
    /// allocated tens of megabytes per build while the server started. A cell's column follows <c>ss:Index</c> and
    /// the previous cell's <c>ss:MergeAcross</c>; its value is the trimmed text of its first direct <c>ss:Data</c>
    /// child, every text run included, as the document reader it replaced read it.
    /// </summary>
    internal static Dictionary<string, List<Dictionary<int, string>>> ReadWorkbook(byte[] bytes, string path, string[] wanted,
        int maxRecords, int maxColumns, int maxCharacters)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return ReadWorkbook(stream, path, wanted, maxRecords, maxColumns, maxCharacters);
    }

    /// <summary>Reads the wanted worksheets from a workbook stream; see the byte overload for the rules.</summary>
    internal static Dictionary<string, List<Dictionary<int, string>>> ReadWorkbook(Stream stream, string path, string[] wanted,
        int maxRecords, int maxColumns, int maxCharacters)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maxCharacters,
        });
        const string ss = "urn:schemas-microsoft-com:office:spreadsheet";
        var result = new Dictionary<string, List<Dictionary<int, string>>>(StringComparer.Ordinal);
        List<Dictionary<int, string>>? rows = null;
        var sheetDepth = -1;
        var tableDepth = -1;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.Depth == tableDepth) tableDepth = -1;
                else if (reader.Depth == sheetDepth) { rows = null; sheetDepth = -1; }
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != ss) continue;
            if (reader.LocalName == "Worksheet" && rows is null)
            {
                var name = reader.GetAttribute("Name", ss);
                if (name is null || !wanted.Contains(name, StringComparer.Ordinal) || reader.IsEmptyElement)
                {
                    if (name is not null && wanted.Contains(name, StringComparer.Ordinal) && !result.TryAdd(name, []))
                        throw new ConfigException($"'{path}' repeats worksheet '{name}'.");
                    continue;
                }
                rows = [];
                if (!result.TryAdd(name, rows)) throw new ConfigException($"'{path}' repeats worksheet '{name}'.");
                sheetDepth = reader.Depth;
            }
            else if (rows is not null && tableDepth < 0 && reader.LocalName == "Table" && reader.Depth == sheetDepth + 1)
            {
                if (!reader.IsEmptyElement) tableDepth = reader.Depth;
            }
            else if (rows is not null && tableDepth >= 0 && reader.LocalName == "Row" && reader.Depth == tableDepth + 1)
            {
                if (rows.Count >= maxRecords)
                    throw new ConfigException($"'{path}' exceeds weapon_catalog_max_records.");
                rows.Add(ReadRow(reader, path, maxColumns));
            }
        }
        foreach (var name in wanted)
            if (!result.ContainsKey(name)) throw new ConfigException($"'{path}' lacks worksheet '{name}'.");
        return result;

        static Dictionary<int, string> ReadRow(XmlReader reader, string path, int maxColumns)
        {
            var cells = new Dictionary<int, string>();
            if (reader.IsEmptyElement) return cells;
            var rowDepth = reader.Depth;
            var next = 0;
            while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == rowDepth))
            {
                if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != ss || reader.LocalName != "Cell"
                    || reader.Depth != rowDepth + 1) continue;
                var index = reader.GetAttribute("Index", ss) is { } position ? Positive(position, "cell index") - 1 : next;
                var merged = reader.GetAttribute("MergeAcross", ss) is { } span ? Selector(span, 0) : 0;
                if (index < next || index >= maxColumns || merged >= maxColumns - index)
                    throw new ConfigException($"'{path}' has an overlapping or out-of-bound workbook cell.");
                cells.Add(index, ReadCell(reader));
                next = index + merged + 1;
            }
            return cells;
        }

        static string ReadCell(XmlReader reader)
        {
            if (reader.IsEmptyElement) return "";
            var cellDepth = reader.Depth;
            string? first = null;
            System.Text.StringBuilder? text = null;
            var dataDepth = -1;
            var read = false;
            while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == cellDepth))
            {
                if (dataDepth < 0)
                {
                    if (!read && reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == ss && reader.LocalName == "Data"
                        && reader.Depth == cellDepth + 1)
                    {
                        read = true;
                        if (!reader.IsEmptyElement) dataDepth = reader.Depth;
                    }
                    continue;
                }
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == dataDepth) { dataDepth = -1; continue; }
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                {
                    if (first is null) first = reader.Value;
                    else (text ??= new System.Text.StringBuilder(first)).Append(reader.Value);
                }
            }
            return (text?.ToString() ?? first ?? "").Trim();
        }
    }

    // cn-324 sub_4A94C0 selects seeded player decisions without a mode filter.
    internal bool AppliesTo(int modeId) => modeId > 0;

    internal (double Primary, double Sidearm) Contributions(MatchSeedEquipment? equipment,
        int? effectivePrimary = null, int? effectiveSidearm = null, bool timedEffectActive = false)
    {
        if (equipment is null) throw new InvalidOperationException("Weapon critical seeds require authoritative room equipment.");
        if (!AppliesTo(equipment.ModeId)) throw new InvalidOperationException("Weapon critical seeds require a positive round mode.");
        if (equipment.WornItemIds.Count > maxWornItems)
            throw new InvalidOperationException("Seed equipment exceeds weapon_max_worn_items.");
        var attributes = Attributes(equipment);
        // cn-324 sub_4C84B0 tests the running timer before reading the current amount; eligibility is checked only at activation.
        var ordinary = (double)attributes.OrdinaryPercent + (timedEffectActive ? attributes.TimedPercent : 0);
        var installed = EffectiveSpecifications(equipment, effectivePrimary, effectiveSidearm);
        return ((specifications[installed.Primary].CriticalPercent + ordinary) / 100.0,
            (specifications[installed.Sidearm].CriticalPercent + ordinary) / 100.0);
    }

    internal (int Primary, int Sidearm) EffectiveSpecifications(MatchSeedEquipment equipment,
        int? effectivePrimary = null, int? effectiveSidearm = null)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        if (!AppliesTo(equipment.ModeId) || equipment.WornItemIds.Count > maxWornItems)
            throw new InvalidOperationException("Effective weapon resolution requires a positive mode and bounded equipment.");
        modeFields.TryGetValue(equipment.ModeId, out var field);
        var selected = new int?[2];
        foreach (var item in equipment.WornItemIds)
        {
            if (item is < 200000 or >= 300000) continue;
            if (!itemSlots.TryGetValue(item, out var slot))
                throw new InvalidOperationException($"Worn weapon {item} has no catalog seed-slot mapping.");
            if (slot < 0) continue;
            if (selected[slot].HasValue) throw new InvalidOperationException($"Seed slot {slot} has multiple worn weapons.");
            selected[slot] = item - 200000;
        }
        // The build's own fallback applies only when its corresponding worn slot is empty.
        return (Resolve(effectivePrimary ?? equipment.PrimarySpecification ?? selected[0] ?? primaryDefault, false, effectivePrimary.HasValue),
            Resolve(effectiveSidearm ?? equipment.SidearmSpecification ?? selected[1] ?? sidearmDefault, true, effectiveSidearm.HasValue));

        int Resolve(int id, bool sidearm, bool effective)
        {
            if (!specifications.TryGetValue(id, out var spec) || (!effective && (spec.Family == "Pistol") != sidearm))
                throw new InvalidOperationException($"Weapon specification {id} does not belong to its seed slot.");
            // cn-324 sub_614DE0 selects one replacement, then installs it; it does not follow replacement chains.
            var replacement = effective || field is null ? 0 : spec.Replacements[field];
            return replacement != 0 ? replacement : id;
        }
    }

    private static Dictionary<string, Dictionary<int, string>> Properties(List<Dictionary<int, string>> rows)
    {
        var result = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var name = Cell(row, 0);
            if (name is not ("id" or "weaponCategory" or "crtChancePercent")
                && !ReplacementFields.Contains(name, StringComparer.Ordinal)) continue;
            if (!result.TryAdd(name, row)) throw new ConfigException($"Weapon property '{name}' is duplicated.");
        }
        return result;
    }

    private static Dictionary<int, string> Required(Dictionary<string, Dictionary<int, string>> properties, string name) =>
        properties.TryGetValue(name, out var row) ? row : throw new ConfigException($"Weapon sheet lacks '{name}'.");

    private static string Property(Dictionary<string, Dictionary<int, string>> properties, string name, int column) =>
        properties.TryGetValue(name, out var row) ? Cell(row, column) : "";

    private static string Cell(Dictionary<int, string> row, int column) => row.GetValueOrDefault(column, "");

    private static int Positive(string text, string name) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value : throw new ConfigException($"Weapon catalog {name} is not a positive integer: '{text}'.");

    private static int Selector(string text, int fallback) => text.Length == 0 ? fallback
        : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value : throw new ConfigException($"Weapon catalog selector is invalid: '{text}'.");

    // cn-324 sub_61DEF0 adds a signed detail increment; only the combined weapon probability has a 0-100 bound.
    private static float Chance(string text, string name) => text.Length == 0 ? 0
        : float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value)
            ? value : throw new ConfigException($"Weapon catalog {name} is not a finite value: '{text}'.");
}
