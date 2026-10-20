using System.Xml;

namespace BubbleFighter.Fantasy.Fighters.Data;

/// <summary>
/// Bounded SpreadsheetML reader for Korean client Excel-XML tables
/// (<c>Config/FighterConfig.xml</c>). Only named worksheets are kept. Sparse
/// <c>ss:Index</c> cells are expanded. The content length is checked before parse.
/// </summary>
public static class SpreadsheetWorkbook
{
    private const string Ss = "urn:schemas-microsoft-com:office:spreadsheet";

    /// <summary>
    /// Reads the requested worksheets from the workbook bytes the client data store returns. Throws
    /// <see cref="InvalidOperationException"/> when the content is empty, larger than
    /// <paramref name="maxBytes"/>, or missing a requested sheet.
    /// </summary>
    /// <param name="content">The whole workbook file, read by client path from the build's client data.</param>
    /// <param name="source">The client path the bytes came from, for diagnostics.</param>
    /// <param name="maxBytes">Inclusive size cap.</param>
    /// <param name="sheetNames">Worksheet names to keep.</param>
    public static IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> Load(
        ReadOnlyMemory<byte> content, string source, long maxBytes, IReadOnlyCollection<string> sheetNames)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("Fighter config source is empty.");
        if (content.Length == 0)
            throw new InvalidOperationException($"Fighter config {source} is empty.");
        if (content.Length > maxBytes)
            throw new InvalidOperationException(
                $"Fighter config {source} is {content.Length} bytes; max_fighter_config_bytes is {maxBytes}.");

        var wanted = new HashSet<string>(sheetNames, StringComparer.Ordinal);
        var sheets = new Dictionary<string, List<IReadOnlyList<string>>>(StringComparer.Ordinal);
        string? current = null;
        List<IReadOnlyList<string>>? rows = null;

        var settings = new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            DtdProcessing = DtdProcessing.Prohibit,
        };
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        using var reader = XmlReader.Create(stream, settings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Worksheet")
            {
                Flush(sheets, current, rows);
                current = reader.GetAttribute("Name", Ss);
                rows = current is not null && wanted.Contains(current) ? [] : null;
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Row" && rows is not null)
            {
                var cells = new List<string>();
                var nextIndex = 1;
                ReadRow(reader, cells, ref nextIndex);
                rows.Add(cells);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Worksheet")
            {
                Flush(sheets, current, rows);
                current = null;
                rows = null;
            }
        }

        Flush(sheets, current, rows);
        var missing = wanted.Where(n => !sheets.ContainsKey(n)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Fighter config is missing worksheet(s): {string.Join(", ", missing)}.");
        return sheets.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<IReadOnlyList<string>>)kv.Value, StringComparer.Ordinal);
    }

    private static void Flush(
        Dictionary<string, List<IReadOnlyList<string>>> sheets, string? name, List<IReadOnlyList<string>>? rows)
    {
        if (name is null || rows is null || sheets.ContainsKey(name))
            return;
        sheets[name] = rows;
    }

    private static void ReadRow(XmlReader reader, List<string> cells, ref int nextIndex)
    {
        if (reader.IsEmptyElement)
            return;
        var depth = reader.Depth;
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Row" && reader.Depth == depth))
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Cell")
                continue;
            var indexAttr = reader.GetAttribute("Index", Ss);
            if (int.TryParse(indexAttr, out var index) && index > 0)
                nextIndex = index;
            while (cells.Count < nextIndex - 1)
                cells.Add(string.Empty);
            cells.Add(ReadCellText(reader));
            nextIndex++;
        }
    }

    private static string ReadCellText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
            return string.Empty;
        var depth = reader.Depth;
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Cell" && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Data")
                return reader.ReadElementContentAsString();
        }

        return string.Empty;
    }
}
