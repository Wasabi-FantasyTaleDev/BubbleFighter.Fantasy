using System.Globalization;
using System.Text;
using System.Xml;
using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.InGame.Data;

internal static class LotteryCatalog
{
    private const string Ss = "urn:schemas-microsoft-com:office:spreadsheet";
    private const int MaxRows = 65536;

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

    /// <summary>Validates the workbook at a client path, parsing it as the store unpacks it.</summary>
    internal static void Validate(BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore data, string clientPath,
        IReadOnlyList<LotteryItem> prizes)
    {
        // The verdict depends only on the workbook and the configured prizes, so a store with compiled client data
        // keeps that this pair passed and the next start does not read the workbook. A failure is never kept.
        var inputs = new BubbleFighter.Fantasy.Contracts.ClientData.CompiledInputs().Add("path", clientPath);
        AddPrizes(inputs, "prizes", prizes);
        BubbleFighter.Fantasy.Contracts.ClientData.CompiledClientData.Load(data, ValidatedLottery.Instance, inputs.ToString(), store =>
        {
            if (store.Length(clientPath) > 64 * 1024 * 1024)
                throw new ConfigException("Reward-card workbook exceeds its byte bound.");
            using var stream = store.OpenRead(clientPath);
            Validate(stream, prizes);
            return true;
        });
    }

    // Every field the validation reads, components included; the inputs text keeps names and lengths apart.
    private static void AddPrizes(BubbleFighter.Fantasy.Contracts.ClientData.CompiledInputs inputs, string name,
        IReadOnlyList<LotteryItem> prizes)
    {
        inputs.Add(name + ".count", prizes.Count);
        for (var index = 0; index < prizes.Count; index++)
        {
            var prize = prizes[index];
            var prefix = name + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
            inputs.Add(prefix + ".display", prize.DisplayId).Add(prefix + ".item", prize.ItemId)
                .Add(prefix + ".quantity", prize.Quantity).Add(prefix + ".days", prize.Days)
                .Add(prefix + ".stackable", prize.Stackable).Add(prefix + ".lucci", prize.Lucci);
            AddPrizes(inputs, prefix + ".components", prize.Components);
        }
    }

    /// <summary>The kept fact that a workbook passed validation for a set of prizes; it reads back only as passed.</summary>
    internal static class ValidatedLottery
    {
        internal static readonly BubbleFighter.Fantasy.Contracts.ClientData.ICompiledCatalog<bool> Instance =
            new BubbleFighter.Fantasy.Contracts.ClientData.PackedCatalog<bool, bool>("lottery-validation", passed => passed,
                passed => passed ? true : throw new InvalidDataException("The compiled reward-card validation is not a passed validation."));
    }

    // The workbook is read forward once without building a document: the native Stock worksheets style every row out to
    // column 16384, so a document tree cost hundreds of megabytes per start. Each worksheet's defects are kept and
    // judged after the document's end, StockSet before Stock, so a malformed document still fails as XML first and the
    // verdict does not depend on the order of the worksheets in the file.
    private static void Validate(Stream stream, IReadOnlyList<LotteryItem> prizes)
    {
        var bundles = prizes.Where(p => p.DisplayId >= 2000000).ToArray();
        var items = prizes.SelectMany(p => p.Components.Length > 0 ? p.Components : p.Lucci > 0 ? [] : new[] { p }).ToArray();
        var stock = new StockSheet(items);
        var sets = new StockSetSheet(bundles);
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024,
        }))
        {
            Sheet? sheet = null;
            var inTable = false;
            var cells = new List<string>();
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                // Only the first Table of a worksheet directly under the root holds that worksheet's rows.
                if (reader.Depth == 1)
                {
                    sheet = reader.LocalName != "Worksheet" || reader.NamespaceURI != Ss ? null
                        : reader.GetAttribute("Name", Ss) switch
                        {
                            "Stock" => stock.Enter(),
                            "StockSet" when bundles.Length > 0 => sets.Enter(),
                            _ => null,
                        };
                    inTable = false;
                }
                else if (reader.Depth == 2)
                {
                    inTable = sheet is not null && reader.LocalName == "Table" && reader.NamespaceURI == Ss && sheet.EnterTable();
                }
                else if (reader.Depth == 3 && inTable && reader.LocalName == "Row" && reader.NamespaceURI == Ss)
                {
                    if (!sheet!.WantsRow())
                    {
                        // Read to the row's end rather than Skip(): the loop's own Read() would then pass over the next row.
                        SkipElement(reader);
                        continue;
                    }
                    var defect = ReadCells(reader, cells);
                    if (defect is not null) sheet.Defect = defect;
                    else sheet.Row(cells);
                }
            }
        }
        if (bundles.Length > 0) sets.Judge("StockSet");
        stock.Judge("Stock");
        foreach (var prize in items)
        {
            var expected = prize.Stackable ? prize.Quantity : prize.Days == 0 ? -1 : prize.Days;
            if (!stock.Selected.TryGetValue(prize.DisplayId, out var term) || term.Item != prize.ItemId
                || term.Duration != expected || term.Type != (expected == -1 ? 9 : 1))
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

    /// <summary>One worksheet's reading state: how often it occurs, its first table's row count and its first defect.</summary>
    private abstract class Sheet
    {
        private int _occurrences;
        private bool _tableSeen;
        private int _rows;
        private string[]? _header;

        public Exception? Defect { get; set; }

        public Sheet? Enter() => ++_occurrences == 1 ? this : null;

        public bool EnterTable()
        {
            if (_tableSeen) return false;
            _tableSeen = true;
            return true;
        }

        // Rows after a defect or past the bound are still counted, because the bound is judged before the defect.
        public bool WantsRow() => ++_rows <= MaxRows && Defect is null;

        public void Row(List<string> cells)
        {
            if (_header is null)
            {
                _header = cells.ToArray();
                Defect = Header(_header);
            }
            else
            {
                Defect = Data(cells);
            }
        }

        protected abstract ConfigException? Header(string[] header);

        protected abstract ConfigException? Data(List<string> cells);

        public void Judge(string name)
        {
            if (_occurrences > 1) throw new InvalidOperationException($"Reward-card workbook has more than one {name} worksheet.");
            if (_occurrences == 0) throw new ConfigException($"Reward-card workbook has no {name} worksheet.");
            if (!_tableSeen) throw new ConfigException($"Reward-card {name} worksheet has no table.");
            if (_rows is < 1 or > MaxRows) throw new ConfigException($"Reward-card {name} worksheet exceeds its row bound.");
            if (Defect is not null) throw Defect;
            Finish();
        }

        protected virtual void Finish() { }
    }

    private sealed class StockSheet(IReadOnlyList<LotteryItem> prizes) : Sheet
    {
        private readonly HashSet<int> _requested = prizes.Select(prize => prize.DisplayId).ToHashSet();
        private int _id, _item, _duration, _type;

        public Dictionary<int, (int Item, int Duration, int Type)> Selected { get; } = [];

        protected override ConfigException? Header(string[] header)
        {
            _id = Array.IndexOf(header, "stockId");
            _item = Array.IndexOf(header, "itemId");
            _duration = Array.IndexOf(header, "duration");
            _type = Array.IndexOf(header, "type");
            return new[] { _id, _item, _duration, _type }.Any(index => index < 0)
                ? new ConfigException("Reward-card Stock worksheet lacks native identity or duration columns.") : null;
        }

        protected override ConfigException? Data(List<string> cells)
        {
            if (cells.Count <= _id || !int.TryParse(cells[_id], CultureInfo.InvariantCulture, out var id)
                || !_requested.Contains(id)) return null;
            if (cells.Count <= Math.Max(_item, Math.Max(_duration, _type))
                || !int.TryParse(cells[_item], CultureInfo.InvariantCulture, out var item)
                || !int.TryParse(cells[_duration], CultureInfo.InvariantCulture, out var duration)
                || !int.TryParse(cells[_type], CultureInfo.InvariantCulture, out var type)
                || !Selected.TryAdd(id, (item, duration, type)))
                return new ConfigException($"Reward-card Stock {id} has malformed or duplicate native terms.");
            return null;
        }
    }

    private sealed class StockSetSheet(LotteryItem[] bundles) : Sheet
    {
        private readonly HashSet<int> _requested = bundles.Select(p => p.DisplayId).ToHashSet();
        private readonly HashSet<int> _found = [];
        private int _id, _lucci;
        private int[] _components = [];

        protected override ConfigException? Header(string[] header)
        {
            _id = Array.IndexOf(header, "stockSetId");
            _lucci = Array.FindIndex(header, h => h.Equals("lucci", StringComparison.OrdinalIgnoreCase));
            _components = Enumerable.Range(1, 10).Select(i => Array.IndexOf(header, $"stockId{i:00}")).ToArray();
            return _id < 0 || _lucci < 0 || _components.Any(i => i < 0)
                ? new ConfigException("Reward-card StockSet worksheet lacks native identity, currency or component columns.") : null;
        }

        protected override ConfigException? Data(List<string> cells)
        {
            if (cells.Count <= _id || !int.TryParse(cells[_id], CultureInfo.InvariantCulture, out var id)
                || !_requested.Contains(id)) return null;
            if (!_found.Add(id)) return new ConfigException($"Duplicate reward StockSet {id}.");
            int Number(int column) => column < cells.Count && int.TryParse(cells[column], CultureInfo.InvariantCulture, out var value) ? value : 0;
            var prize = bundles.Single(p => p.DisplayId == id);
            var native = _components.Select(Number).Where(value => value != 0).Order().ToArray();
            return Number(_lucci) != prize.Lucci || !native.SequenceEqual(prize.Components.Select(p => p.DisplayId).Order())
                ? new ConfigException($"Reward StockSet {id} does not match its configured currency and components.") : null;
        }

        protected override void Finish()
        {
            if (_found.Count != _requested.Count) throw new ConfigException("A reward StockSet is absent from the native workbook.");
        }
    }

    private static void SkipElement(XmlReader reader)
    {
        if (reader.IsEmptyElement) return;
        var depth = reader.Depth;
        while (reader.Read() && reader.Depth > depth) { }
    }

    // The reader is on a Row start element and ends on its end. Fills the row's cells by column and returns the first
    // defect instead of throwing it, so a later XML error in the document still takes precedence.
    private static Exception? ReadCells(XmlReader reader, List<string> values)
    {
        values.Clear();
        if (reader.IsEmptyElement) return null;
        Exception? defect = null;
        var rowDepth = reader.Depth;
        var previousIndex = 0;
        while (reader.Read() && reader.Depth > rowDepth)
        {
            if (defect is not null || reader.NodeType != XmlNodeType.Element || reader.Depth != rowDepth + 1
                || reader.LocalName != "Cell" || reader.NamespaceURI != Ss)
                continue;
            var indexText = reader.GetAttribute("Index", Ss);
            int index;
            try
            {
                index = indexText is null ? previousIndex + 1 : XmlConvert.ToInt32(indexText);
            }
            catch (Exception error) when (error is FormatException or OverflowException)
            {
                defect = error;
                continue;
            }
            var value = ReadCellValue(reader);
            if (index <= previousIndex || index > 16384)
            {
                defect = new ConfigException("Reward-card workbook has an invalid cell index.");
                continue;
            }
            previousIndex = index;
            // Native KR2015/CN2015 workbooks retain empty cells through the spreadsheet column limit.
            // Keep the bounded identity table while rejecting meaningful data outside its supported columns.
            if (index > 256)
            {
                if (!string.IsNullOrWhiteSpace(value)) defect = new ConfigException("Reward-card workbook has data beyond its supported columns.");
                continue;
            }
            while (values.Count < index - 1) values.Add(string.Empty);
            values.Add(value);
        }
        return defect;
    }

    // The reader is on a Cell start element and ends on its end. The value is all text of the first Data child, so
    // rich-text runs concatenate with the whitespace between them; a note's Data one level deeper is not the value.
    private static string ReadCellValue(XmlReader reader)
    {
        if (reader.IsEmptyElement) return string.Empty;
        var cellDepth = reader.Depth;
        var found = false;
        string? text = null;
        StringBuilder? builder = null;
        while (reader.Read() && reader.Depth > cellDepth)
        {
            if (found || reader.NodeType != XmlNodeType.Element || reader.Depth != cellDepth + 1
                || reader.LocalName != "Data" || reader.NamespaceURI != Ss)
                continue;
            found = true;
            if (reader.IsEmptyElement) continue;
            var dataDepth = reader.Depth;
            while (reader.Read() && reader.Depth > dataDepth)
            {
                if (reader.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace
                    or XmlNodeType.SignificantWhitespace)) continue;
                if (text is null) text = reader.Value;
                else (builder ??= new StringBuilder(text)).Append(reader.Value);
            }
        }
        return builder?.ToString() ?? text ?? string.Empty;
    }
}
