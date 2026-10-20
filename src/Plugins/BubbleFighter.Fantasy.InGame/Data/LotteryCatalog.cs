using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.InGame.Data;

internal static class LotteryCatalog
{
    private static readonly XNamespace Spreadsheet = "urn:schemas-microsoft-com:office:spreadsheet";

    internal static void Validate(string path, IReadOnlyList<LotteryItem> prizes)
    {
        if (!File.Exists(path)) throw new ConfigException($"Reward-card workbook does not exist: {path}.");
        if (new FileInfo(path).Length > 64 * 1024 * 1024)
            throw new ConfigException("Reward-card workbook exceeds its byte bound.");
        using var stream = File.OpenRead(path);
        Validate(stream, prizes);
    }

    internal static void Validate(byte[] workbook, IReadOnlyList<LotteryItem> prizes)
    {
        if (workbook.Length > 64 * 1024 * 1024)
            throw new ConfigException("Reward-card workbook exceeds its byte bound.");
        using var stream = new MemoryStream(workbook, writable: false);
        Validate(stream, prizes);
    }

    private static void Validate(Stream stream, IReadOnlyList<LotteryItem> prizes)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024,
        });
        Validate(XDocument.Load(reader), prizes);
    }

    internal static void Validate(XDocument document, IReadOnlyList<LotteryItem> prizes)
    {
        ValidateBundles(document, prizes);
        prizes = prizes.SelectMany(p => p.Components.Length > 0 ? p.Components : p.Lucci > 0 ? [] : new[] { p }).ToArray();
        var sheet = document.Root?.Elements(Spreadsheet + "Worksheet")
            .SingleOrDefault(row => (string?)row.Attribute(Spreadsheet + "Name") == "Stock")
            ?? throw new ConfigException("Reward-card workbook has no Stock worksheet.");
        var rows = sheet.Element(Spreadsheet + "Table")?.Elements(Spreadsheet + "Row").ToArray()
            ?? throw new ConfigException("Reward-card Stock worksheet has no table.");
        if (rows.Length is < 1 or > 65536) throw new ConfigException("Reward-card Stock worksheet exceeds its row bound.");
        var header = Cells(rows[0]);
        var idColumn = Array.IndexOf(header, "stockId");
        var itemColumn = Array.IndexOf(header, "itemId");
        var durationColumn = Array.IndexOf(header, "duration");
        var typeColumn = Array.IndexOf(header, "type");
        if (new[] { idColumn, itemColumn, durationColumn, typeColumn }.Any(index => index < 0))
            throw new ConfigException("Reward-card Stock worksheet lacks native identity or duration columns.");
        var selected = new Dictionary<int, (int Item, int Duration, int Type)>();
        var requested = prizes.Select(prize => prize.DisplayId).ToHashSet();
        foreach (var row in rows.Skip(1))
        {
            var cells = Cells(row);
            if (cells.Length <= idColumn || !int.TryParse(cells[idColumn], CultureInfo.InvariantCulture, out var id)
                || !requested.Contains(id)) continue;
            if (cells.Length <= Math.Max(itemColumn, Math.Max(durationColumn, typeColumn))
                || !int.TryParse(cells[itemColumn], CultureInfo.InvariantCulture, out var item)
                || !int.TryParse(cells[durationColumn], CultureInfo.InvariantCulture, out var duration)
                || !int.TryParse(cells[typeColumn], CultureInfo.InvariantCulture, out var type)
                || !selected.TryAdd(id, (item, duration, type)))
                throw new ConfigException($"Reward-card Stock {id} has malformed or duplicate native terms.");
        }
        foreach (var prize in prizes)
        {
            var expected = prize.Stackable ? prize.Quantity : prize.Days == 0 ? -1 : prize.Days;
            if (!selected.TryGetValue(prize.DisplayId, out var stock) || stock.Item != prize.ItemId
                || stock.Duration != expected || stock.Type != (expected == -1 ? 9 : 1))
                throw new ConfigException($"Reward display {prize.DisplayId} must resolve to equipment {prize.ItemId} and its configured native amount or duration.");
        }
    }

    internal static bool IsValidPrize(LotteryItem prize)
    {
        if (prize.DisplayId is < 1000000 or >= 3000000 || prize.Weight is < 1 or > 100000
            || prize.Components.Length > 10 || prize.Lucci is < 0 or > 1000000) return false;
        if (prize.DisplayId >= 2000000)
            return prize.ItemId == 0 && (prize.Lucci > 0 && prize.Components.Length == 0
                || prize.Lucci == 0 && prize.Components.Length > 0
                && prize.Components.All(p => p.Components.Length == 0 && p.Lucci == 0 && IsValidPrize(p))
                && prize.Components.Select(p => p.ItemId).Distinct().Count() == prize.Components.Length);
        return prize.ItemId > 0 && prize.Lucci == 0 && prize.Components.Length == 0
            && prize.Quantity is >= 1 and <= 100000 && prize.Days is >= 0 and <= 3650
            && (prize.Stackable ? prize.Days == 0 : prize.Quantity == 1);
    }

    private static void ValidateBundles(XDocument document, IReadOnlyList<LotteryItem> prizes)
    {
        var bundles = prizes.Where(p => p.DisplayId >= 2000000).ToArray();
        if (bundles.Length == 0) return;
        var sheet = document.Root?.Elements(Spreadsheet + "Worksheet")
            .SingleOrDefault(row => (string?)row.Attribute(Spreadsheet + "Name") == "StockSet")
            ?? throw new ConfigException("Reward-card workbook has no StockSet worksheet.");
        var rows = sheet.Element(Spreadsheet + "Table")?.Elements(Spreadsheet + "Row").ToArray()
            ?? throw new ConfigException("Reward-card StockSet worksheet has no table.");
        if (rows.Length is < 1 or > 65536) throw new ConfigException("Reward-card StockSet worksheet exceeds its row bound.");
        var header = Cells(rows[0]);
        var idColumn = Array.IndexOf(header, "stockSetId");
        var lucciColumn = Array.FindIndex(header, h => h.Equals("lucci", StringComparison.OrdinalIgnoreCase));
        var componentColumns = Enumerable.Range(1, 10).Select(i => Array.IndexOf(header, $"stockId{i:00}")).ToArray();
        if (idColumn < 0 || lucciColumn < 0 || componentColumns.Any(i => i < 0))
            throw new ConfigException("Reward-card StockSet worksheet lacks native identity, currency or component columns.");
        var requested = bundles.Select(p => p.DisplayId).ToHashSet();
        var found = new HashSet<int>();
        foreach (var row in rows.Skip(1))
        {
            var cells = Cells(row);
            if (cells.Length <= idColumn || !int.TryParse(cells[idColumn], CultureInfo.InvariantCulture, out var id)
                || !requested.Contains(id)) continue;
            if (!found.Add(id)) throw new ConfigException($"Duplicate reward StockSet {id}.");
            int Number(int column) => column < cells.Length && int.TryParse(cells[column], CultureInfo.InvariantCulture, out var value) ? value : 0;
            var prize = bundles.Single(p => p.DisplayId == id);
            var native = componentColumns.Select(Number).Where(value => value != 0).Order().ToArray();
            if (Number(lucciColumn) != prize.Lucci || !native.SequenceEqual(prize.Components.Select(p => p.DisplayId).Order()))
                throw new ConfigException($"Reward StockSet {id} does not match its configured currency and components.");
        }
        if (found.Count != requested.Count) throw new ConfigException("A reward StockSet is absent from the native workbook.");
    }

    private static string[] Cells(XElement row)
    {
        var values = new List<string>();
        var previousIndex = 0;
        foreach (var cell in row.Elements(Spreadsheet + "Cell"))
        {
            var index = (int?)cell.Attribute(Spreadsheet + "Index") ?? previousIndex + 1;
            if (index <= previousIndex || index > 16384) throw new ConfigException("Reward-card workbook has an invalid cell index.");
            previousIndex = index;
            var value = cell.Element(Spreadsheet + "Data")?.Value ?? string.Empty;
            // Native KR2015/CN2015 workbooks retain empty cells through the spreadsheet column limit.
            // Keep the bounded identity table while rejecting meaningful data outside its supported columns.
            if (index > 256)
            {
                if (!string.IsNullOrWhiteSpace(value)) throw new ConfigException("Reward-card workbook has data beyond its supported columns.");
                continue;
            }
            while (values.Count < index - 1) values.Add(string.Empty);
            values.Add(value);
        }
        return values.ToArray();
    }
}
