using System.Text;
using System.Xml;

namespace BubbleFighter.Fantasy.Shop.Data;

/// <summary>
/// Bounded SpreadsheetML reader for the Korean client Excel-XML tables
/// (<c>Config/ShopItem2.xml</c>). Only named worksheets are kept. Sparse
/// <c>ss:Index</c> cells are expanded. The file length is checked before parse.
/// </summary>
public static class SpreadsheetWorkbook
{
    private const string Ss = "urn:schemas-microsoft-com:office:spreadsheet";

    /// <summary>
    /// Reads the requested worksheets. Throws <see cref="InvalidOperationException"/>
    /// when the file is empty, larger than <paramref name="maxBytes"/>, or missing a
    /// requested sheet.
    /// </summary>
    /// <param name="file">The workbook read from the client's data.</param>
    /// <param name="maxBytes">Inclusive size cap.</param>
    /// <param name="sheetNames">Worksheet names to keep.</param>
    public static IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> Load(
        WorkbookFile file, long maxBytes, IReadOnlyCollection<string> sheetNames)
    {
        var full = file.Name;
        var length = file.Length;
        if (length <= 0)
            throw new InvalidOperationException($"Shop catalog file is empty: {full}");
        if (length > maxBytes)
            throw new InvalidOperationException(
                $"Shop catalog {full} is {length} bytes; max_shop_item_bytes is {maxBytes}.");

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
        using var stream = file.Open();
        using var reader = XmlReader.Create(stream, settings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Worksheet")
            {
                Flush(sheets, current, rows);
                current = reader.GetAttribute("Name", Ss);
                rows = current is not null && wanted.Contains(current) ? [] : null;
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Row" && reader.NamespaceURI == Ss && rows is not null)
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
            throw new InvalidOperationException($"Shop catalog is missing worksheet(s): {string.Join(", ", missing)}.");
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
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Cell" || reader.NamespaceURI != Ss)
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
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Cell" && reader.NamespaceURI == Ss && reader.Depth == depth))
        {
            // The cell value is a Data element that is a direct child of the Cell in
            // the spreadsheet namespace. Cell notes (<Comment> holding rich-text
            // <ss:Data> with <B>/<Font> children) reuse the Data name one level
            // deeper and must not be read as the value.
            if (reader.NodeType == XmlNodeType.Element
                && reader.LocalName == "Data"
                && reader.NamespaceURI == Ss
                && reader.Depth == depth + 1)
            {
                return ReadDataValue(reader);
            }
        }

        return string.Empty;
    }

    private static string ReadDataValue(XmlReader reader)
    {
        // The reader is on a Data start element. Most values are plain text, but the
        // real workbook also carries hand-authored rich-text cells whose value is the
        // concatenation of the child runs; read the text content of all of them.
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return string.Empty;
        }

        var value = new StringBuilder();
        var depth = reader.Depth;
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType is XmlNodeType.Text
                or XmlNodeType.CDATA
                or XmlNodeType.Whitespace
                or XmlNodeType.SignificantWhitespace)
            {
                value.Append(reader.Value);
            }
        }

        return value.ToString();
    }
}

/// <summary>A workbook to read, as the client data store returns it.</summary>
/// <param name="Name">The client path shown in messages.</param>
/// <param name="Length">The byte length.</param>
/// <param name="Open">Opens a fresh read-only stream over the bytes.</param>
public sealed record WorkbookFile(string Name, long Length, Func<Stream> Open)
{
    /// <summary>A workbook already read into memory, for example from the client's archives.</summary>
    /// <param name="name">The client path it was read from.</param>
    /// <param name="bytes">Its contents.</param>
    public static WorkbookFile FromBytes(string name, byte[] bytes) =>
        new(name, bytes.LongLength, () => new MemoryStream(bytes, writable: false));
}
