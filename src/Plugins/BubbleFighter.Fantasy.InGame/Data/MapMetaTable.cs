using System.Xml.Linq;

namespace BubbleFighter.Fantasy.InGame.Data;

/// <summary>
/// The client's <c>ServerShared/MapMeta.xml</c>: a SpreadsheetML sheet laid out sideways, one
/// field per row (<c>[Field]</c> header row, then rows such as <c>Directory</c>,
/// <c>isRelease</c> and the per-mode <c>is...ModeMap</c> flags) and one map per column.
/// </summary>
internal sealed class MapMetaTable
{
    /// <summary>Client path of the table.</summary>
    internal const string Path = "ServerShared/MapMeta.xml";

    private static readonly XNamespace Ss = "urn:schemas-microsoft-com:office:spreadsheet";

    private readonly Dictionary<string, Dictionary<string, string>> _maps;

    private MapMetaTable(Dictionary<string, Dictionary<string, string>> maps) => _maps = maps;

    /// <summary>Gets every map key in the table.</summary>
    internal IEnumerable<string> Maps => _maps.Keys;

    /// <summary>Parses the table. A sheet without a <c>[Field]</c> header row is an error.</summary>
    internal static MapMetaTable Parse(byte[] bytes)
    {
        var document = XDocument.Load(new MemoryStream(bytes));
        var rows = document.Descendants(Ss + "Worksheet").First().Descendants(Ss + "Row").Select(Cells).ToList();
        var header = rows.FirstOrDefault(row => row.Count > 0 && row[0] == "[Field]")
            ?? throw new InvalidDataException($"{Path} has no [Field] header row.");
        var maps = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        for (var column = 2; column < header.Count; column++)
        {
            if (header[column].Length > 0)
                maps[header[column]] = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        foreach (var row in rows)
        {
            if (row.Count == 0 || row[0].Length == 0 || ReferenceEquals(row, header))
                continue;
            for (var column = 2; column < header.Count && column < row.Count; column++)
            {
                if (header[column].Length > 0)
                    maps[header[column]][row[0]] = row[column];
            }
        }
        return new MapMetaTable(maps);
    }

    /// <summary>Whether the table has a column for <paramref name="map"/>.</summary>
    internal bool Contains(string map) => _maps.ContainsKey(map);

    /// <summary>The value of <paramref name="field"/> for <paramref name="map"/>, or empty.</summary>
    internal string Get(string map, string field) =>
        _maps.TryGetValue(map, out var fields) && fields.TryGetValue(field, out var value) ? value : string.Empty;

    /// <summary>Whether <paramref name="field"/> is <c>1</c> for <paramref name="map"/>.</summary>
    internal bool IsSet(string map, string field) => Get(map, field) == "1";

    // A SpreadsheetML row: cells may skip columns with ss:Index (1-based).
    private static List<string> Cells(XElement row)
    {
        var cells = new List<string>();
        foreach (var cell in row.Elements(Ss + "Cell"))
        {
            if (int.TryParse((string?)cell.Attribute(Ss + "Index"), out var index))
            {
                while (cells.Count < index - 1)
                    cells.Add(string.Empty);
            }
            cells.Add(cell.Element(Ss + "Data")?.Value.Trim() ?? string.Empty);
        }
        return cells;
    }
}
