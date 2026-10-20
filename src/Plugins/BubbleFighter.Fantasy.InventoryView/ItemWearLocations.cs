using System.Xml;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.InventoryView;

/// <summary>Reads wearable body parts and accessory slots from the client's ShopItem workbook.</summary>
internal static class ItemWearLocations
{
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
        var document = XDocument.Load(reader);
        XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
        var sheet = document.Root?.Elements(ns + "Worksheet").SingleOrDefault(row => (string?)row.Attribute(ns + "Name") == "ShopItem")
            ?? throw new ConfigException("Item locations workbook requires a ShopItem worksheet.");
        var rows = sheet.Element(ns + "Table")?.Elements(ns + "Row").ToArray()
            ?? throw new ConfigException("Item locations workbook requires a ShopItem table.");
        if (rows.Length == 0 || rows.Length > 65536) throw new ConfigException("Item locations table must contain a bounded header and item rows.");
        var header = Cells(rows[0]);
        foreach (var column in new[] { "itemId", "mainCategory", "baseLocation", "slotLocation", "partLocation", "secPartLocation" })
            if (!header.Contains(column)) throw new ConfigException($"Item locations table is missing {column}.");
        var locations = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var row in rows.Skip(1))
        {
            var cells = Cells(row);
            string Cell(string name) => Array.IndexOf(header, name) is var index && index < cells.Length ? cells[index] : "";
            var category = Cell("mainCategory");
            if (category != clothingCategory && category != accessoryCategory) continue;
            if (!int.TryParse(Cell("itemId"), out var id) || id <= 0) throw new ConfigException("Wearable item identity must be positive.");
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
        }
        if (locations.Count == 0) throw new ConfigException("Item locations workbook contains no wearable positions; check clothing_category and accessory_category.");
        if (locations.Count > 59) throw new ConfigException("Wearable locations exceed the equipment position bound.");
        return locations.Values.Select(ids => new InventoryWearGroup(ids.Min(), ids.Max(), ids.Min()) { ItemIds = ids.Order().ToArray() }).ToArray();

        string[] Cells(XElement row)
        {
            var values = new List<string>();
            var previousIndex = 0;
            foreach (var cell in row.Elements(ns + "Cell"))
            {
                var index = (int?)cell.Attribute(ns + "Index") ?? previousIndex + 1;
                if (index <= previousIndex || index > 16384) throw new ConfigException("Item locations cell index is invalid.");
                previousIndex = index;
                var value = cell.Element(ns + "Data")?.Value ?? "";
                // kr-812 and cn-717 ShopItem worksheets retain empty formatting
                // through Excel column 16384; their populated fields end before 256.
                if (index > 256)
                {
                    if (value.Length != 0) throw new ConfigException("Item locations data exceeds the column bound.");
                    continue;
                }
                while (values.Count < index - 1) values.Add("");
                values.Add(value);
            }
            return values.ToArray();
        }
    }
}
