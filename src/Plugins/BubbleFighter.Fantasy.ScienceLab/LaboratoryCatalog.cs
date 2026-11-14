using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.ScienceLab;

internal sealed record LaboratoryItem(int Id, int Category, string DurationType, int Maximum, bool Available);
internal sealed record LaboratoryStock(int Id, int ItemId, int Duration);
internal sealed record LaboratoryAssistant(int Id, int Operation, bool Additive, int Effect);
internal sealed record LaboratorySelection(int Reference, IReadOnlyList<int> Members, int Category);

internal sealed class LaboratoryCatalog(
    IReadOnlyDictionary<int, LaboratoryItem> items,
    IReadOnlyDictionary<int, IReadOnlyList<int[]>> groups,
    IReadOnlyDictionary<int, LaboratoryAssistant> assistants,
    IReadOnlyDictionary<int, LaboratoryStock>? stocks = null)
{
    internal IReadOnlyDictionary<int, LaboratoryItem> Items { get; } = items;

    internal static LaboratoryCatalog Load(IClientDataStore store, ScienceLabOptions options)
    {
        var sheets = ReadWorkbook(store.Read(options.CatalogPath, options.MaxWorkbookBytes), options);
        var subcategories = new Table(sheets["SubCategory"], "id", "name");
        var categories = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in subcategories.Rows)
        {
            if (!subcategories.TryId(row, "id", out var id)) continue;
            var name = subcategories.Cell(row, "name");
            if (!categories.TryAdd(name, id)) categories[name] = -1;
        }
        var catalog = new Table(sheets["ShopItem"], "itemId", "subCategory", "durationType", "maxDuration", "using", "lockType");
        var items = new Dictionary<int, LaboratoryItem>();
        foreach (var row in catalog.Rows)
        {
            if (!catalog.TryId(row, "itemId", out var id)) continue;
            categories.TryGetValue(catalog.Cell(row, "subCategory"), out var sub);
            // kr-812 sub_9EA680 defines these material categories; grouped selections use category one.
            var category = sub is >= 302 and <= 306 ? 0 : sub is >= 102 and <= 110 ? 1 : sub is >= 202 and <= 205 ? 2 : -1;
            if (!items.TryAdd(id, new(id, category, catalog.Cell(row, "durationType"), catalog.Integer(row, "maxDuration"),
                catalog.Integer(row, "using") == 1 && catalog.Integer(row, "lockType") == 0)))
                throw new InvalidDataException($"Science-room catalog repeats item {id}.");
        }
        var sets = new Table(sheets["SetItem"], "setId", "setCnt", "stockSetId");
        var groups = new Dictionary<int, List<int[]>>();
        foreach (var row in sets.Rows)
        {
            if (!sets.TryId(row, "setId", out _)) continue;
            var stock = sets.Integer(row, "stockSetId");
            var count = sets.Integer(row, "setCnt");
            if (stock <= 0 || count is < 1 or > 10) throw new InvalidDataException("Science-room set has invalid native membership.");
            var members = Enumerable.Range(1, count).Select(index => sets.Integer(row, $"ShopItemID{index:00}")).ToArray();
            if (members.Distinct().Count() != count || members.Any(id => !items.ContainsKey(id)))
                throw new InvalidDataException($"Science-room stock set {stock} has invalid members.");
            if (!groups.TryGetValue(stock, out var alternatives)) groups.Add(stock, alternatives = []);
            if (!alternatives.Any(candidate => candidate.SequenceEqual(members))) alternatives.Add(members);
        }
        var assistants = ReadAssistants(store.Read(options.ConfigurationPath, options.MaxWorkbookBytes), options);
        var stockTable = new Table(sheets["Stock"], "stockId", "itemId", "duration");
        var stockRows = new Dictionary<int, LaboratoryStock>();
        foreach (var row in stockTable.Rows)
        {
            if (!stockTable.TryId(row, "stockId", out var id)) continue;
            var stock = new LaboratoryStock(id, stockTable.Integer(row, "itemId"), stockTable.Integer(row, "duration"));
            // kr-812 sub_9DBED0 consumes Stock itemId/duration; equivalent native rows preserve both result terms.
            if (!stockRows.TryAdd(id, stock) && stockRows[id] != stock)
                throw new InvalidDataException($"Science-room catalog has conflicting terms for stock {id}.");
        }
        return new(items, groups.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<int[]>)pair.Value.AsReadOnly()), assistants, stockRows);
    }

    internal void RequirePolicy(ScienceLabOptions options, InventoryLoadoutScope scope)
    {
        if (scope.Key != options.BuildKey) throw new InvalidDataException("Science-room build_key does not match its inventory scope.");
        if (options.SynthesisCardItemId > 0) Card(options.SynthesisCardItemId, LaboratoryOperation.Synthesis);
        foreach (var recipe in options.Decomposition)
        {
            Card(recipe.CardItemId, LaboratoryOperation.Decomposition);
            foreach (var reward in recipe.Rewards) Reward(reward);
        }
        if (options.PermanentAdditiveItemId > 0) Additive(options.PermanentAdditiveItemId, 3);
        if (options.BonusAdditiveItemId > 0) Additive(options.BonusAdditiveItemId, 2);
        foreach (var reward in new[] { options.BonusReward, options.PermanentFailureReward }.OfType<LaboratoryRewardOptions>()) Reward(reward);

        void Card(int id, LaboratoryOperation operation)
        {
            Counted(id);
            if (!assistants.TryGetValue(id, out var entry) || entry.Additive || entry.Operation != (int)operation)
                throw new InvalidDataException($"Science-room card {id} does not select {operation} in native ASSISTINFO.");
        }
        void Additive(int id, int effect)
        {
            Counted(id);
            if (!assistants.TryGetValue(id, out var entry) || !entry.Additive || entry.Operation != 0 || entry.Effect != effect)
                throw new InvalidDataException($"Science-room additive {id} does not match its native effect.");
        }
        void Counted(int id)
        {
            if (!scope.Supports(id) || !Items.TryGetValue(id, out var item) || item.DurationType != "count" || !item.Available)
                throw new InvalidDataException($"Science-room material {id} is not an available counted native item.");
        }
        void Reward(LaboratoryRewardOptions reward)
        {
            reward.Term().Validate();
            RequireRewardStock(reward);
            if (!scope.Supports(reward.ItemId) || !Items.TryGetValue(reward.ItemId, out var item) || !item.Available
                || item.DurationType != (reward.Kind == InventoryGrantKind.Count ? "count" : "day")
                || reward.Kind == InventoryGrantKind.Count && reward.Maximum > item.Maximum
                || reward.Kind == InventoryGrantKind.Days && reward.Maximum > item.Maximum)
                throw new InvalidDataException($"Science-room reward {reward.ItemId} disagrees with its native catalog terms.");
        }
    }

    internal void RequireRewardStock(LaboratoryRewardOptions reward)
    {
        // kr-812 sub_99E300 keys Stock by stockId; kr-812 sub_9DBED0 reads its itemId and duration for decomposition.
        // kr-812 sub_9EB4E0 does not display Stock.duration -1 as unlimited; no permanent Stock result is admitted.
        if (reward.StockId is < 1000000 or >= 2000000 || stocks is null || !stocks.TryGetValue(reward.StockId, out var stock)
            || stock.ItemId != reward.ItemId || reward.Kind == InventoryGrantKind.Permanent
            || stock.Duration <= 0 || reward.Amount != stock.Duration)
            throw new InvalidDataException("Science-room reward stock_id must identify its exact native item and awarded quantity or duration.");
    }

    internal IReadOnlyList<LaboratorySelection>? Select(LaboratoryRequested request)
    {
        // kr-812 sub_9DA2F0 has three selections; native SetItem stores at most ten members plus each group reference.
        if (request.Materials.Count < 1 || request.Materials.Count > 3 * 11 || request.Category is < 0 or > 2) return null;
        var selections = new List<LaboratorySelection>();
        var index = 0;
        while (index < request.Materials.Count)
        {
            var reference = request.Materials[index++];
            if (request.Grouped)
            {
                if (reference >= -1 || reference == int.MinValue || !groups.TryGetValue(-reference, out var alternatives)) return null;
                var matches = alternatives.Where(members => request.Materials.Skip(index).Take(members.Length).SequenceEqual(members)).Take(2).ToArray();
                if (matches.Length != 1) return null;
                selections.Add(new(-reference, Array.AsReadOnly(matches[0]), 1));
                index += matches[0].Length;
            }
            else
            {
                if (reference <= 0 || !Items.TryGetValue(reference, out var item)) return null;
                selections.Add(new(reference, Array.AsReadOnly(new[] { reference }), item.Category));
            }
        }
        var count = request.Operation == LaboratoryOperation.Synthesis ? 3 : request.Operation == LaboratoryOperation.Decomposition ? 1 : 0;
        if (count == 0 || selections.Count != count || selections.Any(selection => selection.Category != request.Category)
            || selections.SelectMany(selection => selection.Members).Distinct().Count() != selections.Sum(selection => selection.Members.Count)) return null;
        return selections;
    }

    private static IReadOnlyDictionary<int, LaboratoryAssistant> ReadAssistants(byte[] bytes, ScienceLabOptions options)
    {
        if (bytes.Length == 0 || bytes.Length > options.MaxWorkbookBytes) throw new InvalidDataException("Science-room configuration exceeds its byte bound.");
        var text = new UTF8Encoding(false, true).GetString(bytes);
        var lines = text.Split('\n');
        if (lines.Length > options.MaxCatalogRows || lines.Any(line => line.Length > options.MaxCellCharacters))
            throw new InvalidDataException("Science-room configuration exceeds its row or text bounds.");
        var active = false;
        int? count = null;
        var entries = new Dictionary<int, LaboratoryAssistant>();
        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.StartsWith('[')) { active = line == "[ASSISTINFO]"; continue; }
            if (!active || line.Length == 0 || line.StartsWith(';')) continue;
            var pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2) throw new InvalidDataException("Malformed native science-room assistant row.");
            if (pair[0] == "COUNT") { count = Integer(pair[1]); continue; }
            if (!pair[0].StartsWith("ASSISTITEM_", StringComparison.Ordinal)) continue;
            var values = pair[1].Split('#');
            if (values.Length < 8) throw new InvalidDataException("Native science-room assistant lacks required fields.");
            var id = Integer(values[0]);
            var additive = Integer(values[2]);
            if (id <= 0 || additive is not (0 or 1) || !entries.TryAdd(id, new(id, Integer(values[1]), additive == 1, Integer(values[3]))))
                throw new InvalidDataException("Native science-room assistant has an invalid or duplicate identity.");
        }
        if (count != entries.Count || entries.Count == 0) throw new InvalidDataException("Native science-room assistant count disagrees with its rows.");
        return entries;
    }

    private static Dictionary<string, IReadOnlyList<IReadOnlyList<string>>> ReadWorkbook(byte[] bytes, ScienceLabOptions options)
    {
        if (bytes.Length == 0 || bytes.Length > options.MaxWorkbookBytes) throw new InvalidDataException("Science-room workbook exceeds its byte bound.");
        const string spreadsheet = "urn:schemas-microsoft-com:office:spreadsheet";
        XNamespace ns = spreadsheet;
        var sheets = new Dictionary<string, IReadOnlyList<IReadOnlyList<string>>>(StringComparer.Ordinal);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = options.MaxWorkbookBytes });
        var workbook = XDocument.Load(reader);
        var count = 0;
        foreach (var sheet in workbook.Descendants(ns + "Worksheet"))
        {
            var name = (string?)sheet.Attribute(ns + "Name");
            if (name is not ("ShopItem" or "SubCategory" or "SetItem" or "Stock")) continue;
            var rows = new List<IReadOnlyList<string>>();
            foreach (var row in sheet.Descendants(ns + "Row"))
            {
                if (++count > options.MaxCatalogRows) throw new InvalidDataException("Science-room workbook exceeds max_catalog_rows.");
                var values = new List<string>();
                var previous = 0;
                foreach (var cell in row.Elements(ns + "Cell"))
                {
                    if (previous == int.MaxValue) throw new InvalidDataException("Science-room sparse column overflows Int32.");
                    var index = cell.Attribute(ns + "Index") is { } attribute ? Integer(attribute.Value) : previous + 1;
                    if (index <= previous) throw new InvalidDataException("Science-room sparse columns overlap or move backwards.");
                    previous = index;
                    var text = cell.Element(ns + "Data")?.Value.Trim() ?? "";
                    if (text.Length > options.MaxCellCharacters) throw new InvalidDataException("Science-room cell exceeds max_cell_characters.");
                    // Native worksheets style empty trailing columns; only nonempty values allocate bounded sparse padding.
                    if (text.Length == 0) continue;
                    if (index > options.MaxCatalogColumns) throw new InvalidDataException("Science-room workbook exceeds max_catalog_columns.");
                    while (values.Count < index - 1) values.Add("");
                    values.Add(text);
                }
                rows.Add(values.AsReadOnly());
            }
            if (!sheets.TryAdd(name, rows.AsReadOnly())) throw new InvalidDataException($"Science-room workbook repeats worksheet {name}.");
        }
        if (sheets.Count != 4) throw new InvalidDataException("Science-room workbook lacks native item, subcategory, set or stock data.");
        return sheets;
    }

    private static int Integer(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
        ? result : throw new InvalidDataException("Science-room native value is not a signed integer.");

    private sealed class Table
    {
        private readonly Dictionary<string, int> _columns = new(StringComparer.Ordinal);
        internal Table(IReadOnlyList<IReadOnlyList<string>> rows, params string[] required)
        {
            if (rows.Count == 0) throw new InvalidDataException("Science-room native worksheet is empty.");
            for (var index = 0; index < rows[0].Count; index++)
                if (rows[0][index].Length > 0 && !_columns.TryAdd(rows[0][index], index))
                    throw new InvalidDataException("Science-room worksheet repeats a column.");
            if (required.Any(column => !_columns.ContainsKey(column))) throw new InvalidDataException("Science-room worksheet lacks required columns.");
            Rows = rows.Skip(1);
        }
        internal IEnumerable<IReadOnlyList<string>> Rows { get; }
        internal string Cell(IReadOnlyList<string> row, string column) => _columns.TryGetValue(column, out var index)
            ? index < row.Count ? row[index] : "" : throw new InvalidDataException($"Science-room worksheet lacks {column}.");
        internal int Integer(IReadOnlyList<string> row, string column) => LaboratoryCatalog.Integer(Cell(row, column));
        internal bool TryId(IReadOnlyList<string> row, string column, out int id) =>
            int.TryParse(Cell(row, column), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0;
    }
}
