using System.Text;
using System.Xml;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.InventoryView;

/// <summary>Reads wearable body parts and accessory slots from the client's ShopItem workbook.</summary>
/// <remarks>
/// The workbook is read forward once without building a document: the native ShopItem worksheets style every row out to
/// column 16384, so a document tree of them cost hundreds of megabytes per start. Every defect is judged when the
/// document has been read to its end, in the order a whole-document reading judges them, so a malformed document
/// still fails as XML first and the refusals do not depend on where in the file a defect sits.
/// </remarks>
internal static class ItemWearLocations
{
    private const string Ss = "urn:schemas-microsoft-com:office:spreadsheet";
    private const int MaxRows = 65536;
    private const int MaxIndex = 16384;
    private const int MaxColumns = 256;
    private static readonly string[] RequiredColumns = ["itemId", "mainCategory", "baseLocation", "slotLocation", "partLocation", "secPartLocation"];

    internal static InventoryWearGroup[] Load(string path, string clothingCategory, string accessoryCategory)
    {
        if (string.IsNullOrEmpty(path)) return [];
        using var stream = File.OpenRead(path);
        return Load(stream, clothingCategory, accessoryCategory);
    }

    internal static InventoryWearGroup[] Load(Stream workbook, string clothingCategory, string accessoryCategory)
    {
        using var reader = XmlReader.Create(workbook, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 128 * 1024 * 1024,
        });
        var sheets = 0;
        var tableSeen = false;
        var inSheet = false;
        var inTable = false;
        var rowCount = 0;
        string[]? header = null;
        Exception? defect = null;
        var locations = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var cells = new List<string>();
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            // Only the first Table of the one ShopItem worksheet directly under the root holds rows.
            if (reader.Depth == 1)
            {
                inSheet = reader.LocalName == "Worksheet" && reader.NamespaceURI == Ss
                    && reader.GetAttribute("Name", Ss) == "ShopItem" && ++sheets == 1;
                inTable = false;
            }
            else if (reader.Depth == 2)
            {
                inTable = inSheet && !tableSeen && reader.LocalName == "Table" && reader.NamespaceURI == Ss;
                tableSeen |= inTable;
            }
            else if (reader.Depth == 3 && inTable && reader.LocalName == "Row" && reader.NamespaceURI == Ss)
            {
                ++rowCount;
                if (defect is not null || rowCount > MaxRows)
                {
                    // Still read to the row's end, not Skip(): the loop's own Read() would then pass over the next row.
                    SkipRow(reader);
                    continue;
                }
                defect = ReadCells(reader, cells);
                if (defect is not null) continue;
                if (header is null)
                {
                    header = cells.ToArray();
                    foreach (var column in RequiredColumns)
                        if (!header.Contains(column))
                        {
                            defect = new ConfigException($"Item locations table is missing {column}.");
                            break;
                        }
                }
                else
                {
                    defect = AddRow(header, cells, clothingCategory, accessoryCategory, locations);
                }
            }
        }
        if (sheets > 1) throw new InvalidOperationException("Item locations workbook has more than one ShopItem worksheet.");
        if (sheets == 0) throw new ConfigException("Item locations workbook requires a ShopItem worksheet.");
        if (!tableSeen) throw new ConfigException("Item locations workbook requires a ShopItem table.");
        if (rowCount is 0 or > MaxRows) throw new ConfigException("Item locations table must contain a bounded header and item rows.");
        if (defect is not null) throw defect;
        if (locations.Count == 0) throw new ConfigException("Item locations workbook contains no wearable positions; check clothing_category and accessory_category.");
        if (locations.Count > 59) throw new ConfigException("Wearable locations exceed the equipment position bound.");
        return locations.Values.Select(ids => new InventoryWearGroup(ids.Min(), ids.Max(), ids.Min()) { ItemIds = ids.Order().ToArray() }).ToArray();
    }

    private static ConfigException? AddRow(string[] header, List<string> cells, string clothingCategory, string accessoryCategory,
        Dictionary<string, HashSet<int>> locations)
    {
        string Cell(string name) => Array.IndexOf(header, name) is var index && index < cells.Count ? cells[index] : "";
        var category = Cell("mainCategory");
        if (category != clothingCategory && category != accessoryCategory) return null;
        if (!int.TryParse(Cell("itemId"), out var id) || id <= 0) return new ConfigException("Wearable item identity must be positive.");
        // cn-324 sub_821DE0 reads the workbook's part and secondary-part locations.
        // Accessory slotLocation is separate from the clothing mesh parts it attaches to.
        var names = category == clothingCategory
            ? (Cell("baseLocation") + " " + Cell("partLocation") + " " + Cell("secPartLocation")).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : (Cell("slotLocation").Length == 0 ? Cell("baseLocation") : Cell("slotLocation")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var key = category + ":" + name;
            if (!locations.TryGetValue(key, out var items)) locations.Add(key, items = []);
            items.Add(id);
        }
        return null;
    }

    private static void SkipRow(XmlReader reader)
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
            if (index <= previousIndex || index > MaxIndex)
            {
                defect = new ConfigException("Item locations cell index is invalid.");
                continue;
            }
            previousIndex = index;
            // kr-812 and cn-717 ShopItem worksheets retain empty formatting
            // through Excel column 16384; their populated fields end before 256.
            if (index > MaxColumns)
            {
                if (value.Length != 0) defect = new ConfigException("Item locations data exceeds the column bound.");
                continue;
            }
            while (values.Count < index - 1) values.Add("");
            values.Add(value);
        }
        return defect;
    }

    // The reader is on a Cell start element and ends on its end. The value is all text of the first Data child, so
    // rich-text runs concatenate with the whitespace between them; a note's Data one level deeper is not the value.
    private static string ReadCellValue(XmlReader reader)
    {
        if (reader.IsEmptyElement) return "";
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
        return builder?.ToString() ?? text ?? "";
    }
}
