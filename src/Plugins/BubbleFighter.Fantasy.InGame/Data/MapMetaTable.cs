using System.Xml;

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

    private const string Ss = "urn:schemas-microsoft-com:office:spreadsheet";

    private readonly Dictionary<string, Dictionary<string, string>> _maps;

    private MapMetaTable(Dictionary<string, Dictionary<string, string>> maps) => _maps = maps;

    /// <summary>Gets every map key in the table.</summary>
    internal IEnumerable<string> Maps => _maps.Keys;

    /// <summary>Parses the table. A sheet without a <c>[Field]</c> header row is an error.</summary>
    internal static MapMetaTable Parse(byte[] bytes)
    {
        var rows = Rows(bytes);
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

    // The first worksheet's rows, streamed rather than loaded as a document tree: two rows of every build read this
    // table while the server starts. As the tree load did, whitespace-only text is ignored, the sheet must exist, and a
    // cell's value is the trimmed text of its first direct ss:Data child, placed by ss:Index (1-based).
    internal static List<List<string>> Rows(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = true });
        var rows = new List<List<string>>();
        var sheetDepth = -1;
        while (reader.Read())
        {
            if (sheetDepth < 0)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Worksheet" && reader.NamespaceURI == Ss)
                {
                    if (reader.IsEmptyElement) return rows;
                    sheetDepth = reader.Depth;
                }
                continue;
            }
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == sheetDepth) return rows;
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Row" && reader.NamespaceURI == Ss)
                rows.Add(Cells(reader));
        }
        if (sheetDepth < 0) throw new InvalidOperationException("Sequence contains no elements");
        return rows;
    }

    private static List<string> Cells(XmlReader reader)
    {
        var cells = new List<string>();
        if (reader.IsEmptyElement) return cells;
        var rowDepth = reader.Depth;
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == rowDepth))
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Cell" || reader.NamespaceURI != Ss
                || reader.Depth != rowDepth + 1) continue;
            if (int.TryParse(reader.GetAttribute("Index", Ss), out var index))
            {
                while (cells.Count < index - 1)
                    cells.Add(string.Empty);
            }
            cells.Add(CellText(reader));
        }
        return cells;
    }

    private static string CellText(XmlReader reader)
    {
        if (reader.IsEmptyElement) return string.Empty;
        var cellDepth = reader.Depth;
        string? first = null;
        System.Text.StringBuilder? text = null;
        var dataDepth = -1;
        var read = false;
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == cellDepth))
        {
            if (dataDepth < 0)
            {
                if (!read && reader.NodeType == XmlNodeType.Element && reader.LocalName == "Data" && reader.NamespaceURI == Ss
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
        return (text?.ToString() ?? first ?? string.Empty).Trim();
    }
}
