using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.InventoryView;

/// <summary>
/// Carries the character-exclusivity rule of one catalog row, as the client's own wear gate reads it:
/// kr-1256 <c>sub_5F59D0</c> ANDs <c>1 &lt;&lt; characterType</c> with the record's mask (record +220, built
/// from the <c>allowChr</c>/<c>denyChr</c> columns) and shows the "%s는 %s 전용 아이템입니다." notice on failure.
/// </summary>
/// <param name="AllowChr">Character names the article may be worn on; null is the column's <c>all</c>, no restriction.</param>
/// <param name="DenyChr">Character names the article may not be worn on; empty is the column's <c>none</c>.</param>
internal sealed record CatalogCharacterExclusivity(IReadOnlySet<string>? AllowChr, IReadOnlySet<string> DenyChr);

/// <summary>
/// Reads the equipment conflict vocabulary straight from the build's own catalog workbook. Per item it takes the
/// four location columns of the <c>ShopItem</c> sheet — <c>baseLocation</c>, <c>slotLocation</c>,
/// <c>partLocation</c>, <c>secPartLocation</c> (sheet reader kr-1256 <c>sub_A17CF0</c>, which pushes those column
/// names at 0xa19feb..0xa1a272; kr-812's workbook carries the same column set) — and resolves each name through the
/// vocabulary sheets of the same workbook (<c>SlotLocation</c>, <c>PartLocation</c>; sheet dispatcher
/// kr-1256 <c>sub_A08BB0</c>). Two worn articles conflict when their slot name sets share a position or their part
/// name sets share a body part: two rifles share <c>sWeapon1</c>, a shirt and a penguin costume share
/// <c>BodyU</c>, and a penguin costume's secondary <c>Feet</c> part displaces shoes. The character seat
/// (items 300001-300031) stays a one-choice position whose location columns are not conflicts — kr-1256 item
/// 300009 carries <c>all -sHeadBC -sTail2</c>, which describes the model that character replaces, not what its bag
/// row would exclude. The client tests none of this before sending: the wear request (kr-1256 <c>sub_805950</c>,
/// kr-812 <c>sub_977E70</c>) carries the item key and the server decides, so these columns are free to define the
/// server's rule. Names stay exact-name tokens: <c>all</c> expands to the vocabulary sheet minus the
/// <c>-name</c> exclusions, and a name no sheet lists (<c>sVehicle</c>, <c>sWeaponePreset</c>) remains an opaque
/// position whose meaning is not traced.
/// </summary>
internal sealed class ItemCatalogMasks
{
    /// <summary>First bag item of the character seat: FighterConfig Field 1..31, <c>id = type + 300000</c> (kr-1256 <c>sub_EF12E0</c>).</summary>
    internal const int SeatFirstItemId = 300001;

    /// <summary>Inclusive last seat item; Field 31's. The seat stays numeric and single-choice, as every consumer of the worn set assumes.</summary>
    internal const int SeatLastItemId = 300031;

    /// <summary>The workbook byte cap the row has always parsed these files under; not a deployment tunable.</summary>
    internal const long MaxWorkbookBytes = 134_217_728;

    /// <summary>The row bound <see cref="ItemWearLocations"/> also enforces on the same sheet.</summary>
    private const int MaxItemRows = 65536;

    /// <summary>The vocabulary bound: 31 slots and 11 parts exist today; far beyond that is a misread sheet, not a catalog.</summary>
    private const int MaxVocabularyNames = 256;

    private readonly Dictionary<int, CatalogCharacterExclusivity> _exclusive;
    private readonly Dictionary<int, string> _characterNames;

    private ItemCatalogMasks(
        IReadOnlyList<InventoryWearGroup> groups, Dictionary<int, CatalogCharacterExclusivity> exclusive, Dictionary<int, string> characterNames)
    {
        Groups = groups;
        _exclusive = exclusive;
        _characterNames = characterNames;
    }

    /// <summary>Gets the conflict positions: the character seat plus one <see cref="InventoryWearGroup"/> per catalog location name.</summary>
    internal IReadOnlyList<InventoryWearGroup> Groups { get; }

    /// <summary>
    /// Tests one article against one worn character the way the client's local gate does (kr-1256 <c>sub_5F59D0</c>):
    /// the character must be inside <c>allowChr</c> and outside <c>denyChr</c>. A character the FighterConfig name row
    /// does not list fails the AND the same way an excluded name does; a character of 0 (no worn seat item yet) has
    /// nothing to exclude, and the same commit's seat repair installs the default character.
    /// </summary>
    /// <param name="itemId">The article being worn.</param>
    /// <param name="characterItemId">The worn seat item, or 0 when the bag has none.</param>
    /// <returns>True when the article may be worn on that character.</returns>
    internal bool AllowsCharacter(int itemId, int characterItemId)
    {
        if (characterItemId == 0 || !_exclusive.TryGetValue(itemId, out var rule))
            return true;
        if (!_characterNames.TryGetValue(characterItemId, out var name))
            return false;
        if (rule.DenyChr.Contains(name))
            return false;
        return rule.AllowChr is null || rule.AllowChr.Contains(name);
    }

    /// <summary>
    /// Loads the masks from the build's catalog workbook and its FighterConfig name row. Every structural failure of
    /// either workbook fails loud as <see cref="ConfigException"/> naming the sheet and the offending value.
    /// </summary>
    /// <param name="shopItemWorkbook">The build's <c>Config/ShopItem2.xml</c>, as the clientdata row or a file path provided it.</param>
    /// <param name="fighterConfigWorkbook">The build's <c>Config/FighterConfig.xml</c>, whose Field 1..31 name row names the characters.</param>
    internal static ItemCatalogMasks Load(WorkbookFile shopItemWorkbook, WorkbookFile fighterConfigWorkbook)
    {
        var characterNames = CharacterNameItems(Sheets(fighterConfigWorkbook, "fighter_config_client_path", "FighterConfig")["FighterConfig"]);
        var shopSheets = Sheets(shopItemWorkbook, "item_locations_client_path", "ShopItem", "SlotLocation", "PartLocation");
        var slotVocabulary = VocabularyNames(shopSheets["SlotLocation"], shopItemWorkbook.Name, "SlotLocation");
        var partVocabulary = VocabularyNames(shopSheets["PartLocation"], shopItemWorkbook.Name, "PartLocation");

        var rows = shopSheets["ShopItem"];
        if (rows.Count <= 1)
            throw new ConfigException($"Catalog masks {shopItemWorkbook.Name} ShopItem sheet has no item rows.");
        if (rows.Count > MaxItemRows)
            throw new ConfigException($"Catalog masks {shopItemWorkbook.Name} ShopItem sheet exceeds {MaxItemRows} rows.");
        var header = HeaderMap(rows[0], shopItemWorkbook.Name);

        var slots = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var parts = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var exclusive = new Dictionary<int, CatalogCharacterExclusivity>();
        var seen = new HashSet<int>();
        foreach (var row in rows.Skip(1))
        {
            var id = IntCell(row, header, "itemId", shopItemWorkbook.Name);
            if (!seen.Add(id))
                throw new ConfigException($"Catalog masks {shopItemWorkbook.Name} repeats itemId {id}.");
            // The seat is one choice among characters, not a location conflict; its columns (kr-1256 300009's
            // all-expression among them) describe the model, so they never reach the location groups.
            if (id is >= SeatFirstItemId and <= SeatLastItemId)
                continue;
            string Cell(string column) => header.TryGetValue(column, out var index) && index < row.Count ? row[index].Trim() : "";
            var slotNames = LocationNames(Cell("slotLocation"), slotVocabulary, shopItemWorkbook.Name, "slotLocation");
            var partNames = LocationNames(Cell("partLocation"), partVocabulary, shopItemWorkbook.Name, "partLocation");
            partNames.UnionWith(LocationNames(Cell("secPartLocation"), partVocabulary, shopItemWorkbook.Name, "secPartLocation"));
            // baseLocation names the primary attach point; only a row whose own column is empty inherits it. Where
            // the two disagree (kr-1256 100026 base sEye, slot sMask) the slot column governs the conflict; the base's
            // conflict meaning is not traced. An unknown base token is a slot by shape, as today's accessory rule reads it.
            var @base = Cell("baseLocation");
            if (slotNames.Count == 0 && @base.Length > 0 && !partVocabulary.Contains(@base))
                slotNames.Add(@base);
            else if (partNames.Count == 0 && partVocabulary.Contains(@base))
                partNames.Add(@base);
            foreach (var name in slotNames) Membership(slots, name).Add(id);
            foreach (var name in partNames) Membership(parts, name).Add(id);

            var rule = Exclusivity(id, Cell("allowChr"), Cell("denyChr"), characterNames, shopItemWorkbook.Name);
            if (rule is not null && !exclusive.TryAdd(id, rule))
                throw new ConfigException($"Catalog masks {shopItemWorkbook.Name} repeats itemId {id}.");
        }
        if (slots.Count == 0 && parts.Count == 0)
            throw new ConfigException(
                $"Catalog masks {shopItemWorkbook.Name} holds no wearable location outside the character seat; the selection would refuse nothing.");
        if (slots.Count + parts.Count > 63)
            throw new ConfigException(
                $"Catalog masks {shopItemWorkbook.Name} derive {slots.Count + parts.Count} positions; the selection engine bound is 63 beside the character seat.");

        var groups = new List<InventoryWearGroup>(1 + slots.Count + parts.Count)
        {
            new(SeatFirstItemId, SeatLastItemId, SeatFirstItemId, RequireEquipped: true),
        };
        groups.AddRange(slots.Values.Concat(parts.Values)
            .Select(ids => new InventoryWearGroup(ids.Min(), ids.Max(), ids.Min()) { ItemIds = ids.Order().ToArray() }));
        // Runtime exclusivity looks the character up by item id; the load-time resolution worked by name.
        return new ItemCatalogMasks(groups, exclusive, characterNames.ToDictionary(pair => pair.Value, pair => pair.Key));
    }

    /// <summary>Expands one location column's name-set expression: plain names, <c>all</c>, and <c>-name</c> exclusions.</summary>
    private static HashSet<string> LocationNames(
        string expression, IReadOnlySet<string> vocabulary, string source, string column)
    {
        var tokens = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        var all = false;
        var exclusions = new List<string>();
        foreach (var token in tokens)
        {
            if (token == "all")
                all = true;
            else if (token[0] == '-')
                exclusions.Add(token[1..]);
            else
                names.Add(token);
        }
        if (!all)
        {
            if (exclusions.Count > 0)
                throw new ConfigException($"Catalog masks {source} {column} '{expression}' subtracts names without stating all.");
            return names;
        }
        // The only native expression is kr-1256 300009's "all -sHeadBC -sTail2": the vocabulary sheet minus the
        // named positions. An exclusion the sheet does not name is a disagreement, and it is reported, not smoothed.
        foreach (var excluded in exclusions)
            if (!vocabulary.Contains(excluded))
                throw new ConfigException($"Catalog masks {source} {column} subtracts '{excluded}', which the vocabulary sheet does not name.");
        var expanded = vocabulary.Where(name => !exclusions.Contains(name)).ToHashSet(StringComparer.Ordinal);
        expanded.UnionWith(names);
        return expanded;
    }

    /// <summary>Parses the exclusivity columns; null when neither column restricts the article.</summary>
    private static CatalogCharacterExclusivity? Exclusivity(
        int itemId, string allowChr, string denyChr, IReadOnlyDictionary<string, int> characterNames, string source)
    {
        IReadOnlySet<string>? allow = null;
        if (allowChr.Length > 0 && allowChr != "all")
            allow = CharacterNames(itemId, allowChr, characterNames, source, "allowChr");
        var deny = denyChr.Length > 0 && denyChr != "none"
            ? CharacterNames(itemId, denyChr, characterNames, source, "denyChr")
            : new HashSet<string>(StringComparer.Ordinal);
        return allow is null && deny.Count == 0 ? null : new CatalogCharacterExclusivity(allow, deny);
    }

    /// <summary>Resolves one exclusivity column's names; a name FighterConfig does not list fails loud, not silently.</summary>
    private static IReadOnlySet<string> CharacterNames(
        int itemId, string column, IReadOnlyDictionary<string, int> characterNames, string source, string columnName)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var token in column.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!characterNames.ContainsKey(token))
                throw new ConfigException(
                    $"Catalog masks {source} itemId {itemId} {columnName} names '{token}', which FighterConfig Field 1..31 does not.");
            names.Add(token);
        }
        return names;
    }

    /// <summary>Maps the FighterConfig name row's Field 1..31 names to their bag items, the seat vocabulary of <c>allowChr</c>.</summary>
    private static Dictionary<string, int> CharacterNameItems(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var fieldRow = KeyedRow(rows, "[Field]");
        var nameRow = KeyedRow(rows, "name");
        Dictionary<string, int> byName = new(StringComparer.Ordinal);
        for (var i = 2; i < fieldRow.Count; i++)
        {
            var field = fieldRow[i].Trim();
            if (field.Length == 0 || !int.TryParse(field, out var fieldId) || fieldId is < 1 or > 31)
                continue;
            var name = i < nameRow.Count ? nameRow[i].Trim() : string.Empty;
            if (name.Length == 0)
                continue;
            if (!byName.TryAdd(name, 300000 + fieldId))
                throw new ConfigException($"FighterConfig names '{name}' for two Field 1..31 columns.");
        }
        if (byName.Count == 0)
            throw new ConfigException("FighterConfig names no Field 1..31 character; allowChr has nothing to resolve through.");
        return byName;
    }

    private static HashSet<int> Membership(Dictionary<string, HashSet<int>> positions, string name) =>
        positions.TryGetValue(name, out var ids) ? ids : positions[name] = [];

    private static IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> Sheets(
        WorkbookFile workbook, string limitKey, params string[] sheetNames)
    {
        try
        {
            return SpreadsheetWorkbook.Load(workbook, MaxWorkbookBytes, limitKey, sheetNames);
        }
        catch (InvalidOperationException error)
        {
            // This row's data is its configuration: the shared reader's structure failures surface as row errors.
            throw new ConfigException(error.Message);
        }
    }

    private static IReadOnlyList<string> KeyedRow(IReadOnlyList<IReadOnlyList<string>> rows, string key)
    {
        foreach (var row in rows)
            if (row.Count > 0 && row[0].Trim() == key)
                return row;
        throw new ConfigException($"FighterConfig sheet is missing the '{key}' row.");
    }

    private static IReadOnlySet<string> VocabularyNames(IReadOnlyList<IReadOnlyList<string>> rows, string source, string sheet)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var row in rows.Skip(1))
        {
            if (row.Count == 0)
                continue;
            var name = row[0].Trim();
            if (name.Length == 0)
                continue;
            if (!names.Add(name))
                throw new ConfigException($"Catalog masks {source} {sheet} sheet repeats the name '{name}'.");
        }
        if (names.Count == 0 || names.Count > MaxVocabularyNames)
            throw new ConfigException($"Catalog masks {source} {sheet} sheet must hold between 1 and {MaxVocabularyNames} names.");
        return names;
    }

    private static Dictionary<string, int> HeaderMap(IReadOnlyList<string> header, string source)
    {
        Dictionary<string, int> map = new(StringComparer.Ordinal);
        for (var i = 0; i < header.Count; i++)
        {
            var name = header[i].Trim();
            if (name.Length == 0)
                continue;
            if (!map.TryAdd(name, i))
                throw new ConfigException($"Catalog masks {source} ShopItem header repeats the column '{name}'.");
        }
        foreach (var column in new[] { "itemId", "baseLocation", "slotLocation", "partLocation", "secPartLocation", "allowChr", "denyChr" })
            if (!map.ContainsKey(column))
                throw new ConfigException($"Catalog masks {source} ShopItem header is missing the '{column}' column.");
        return map;
    }

    private static int IntCell(IReadOnlyList<string> row, Dictionary<string, int> header, string column, string source)
    {
        var text = header.TryGetValue(column, out var index) && index < row.Count ? row[index].Trim() : string.Empty;
        if (!int.TryParse(text, out var value) || value <= 0)
            throw new ConfigException($"Catalog masks {source} ShopItem row carries itemId '{text}', which is not a positive integer.");
        return value;
    }
}
