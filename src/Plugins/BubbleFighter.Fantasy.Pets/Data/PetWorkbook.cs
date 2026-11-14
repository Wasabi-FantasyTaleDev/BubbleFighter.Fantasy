using System.Globalization;
using System.Xml;
using System.Text;

namespace BubbleFighter.Fantasy.Pets.Data;

/// <summary>Reads the selected pet worksheets, bounding sparse columns before they allocate cell lists.</summary>
internal static class PetWorkbook
{
    private const string Namespace = "urn:schemas-microsoft-com:office:spreadsheet";
    private static readonly string[] RequiredSheets = ["ShopItem", "SubCategory", "ItemAttr", "RandomTable"];

    internal static IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> Read(byte[] bytes, string source,
        int maxRows, int maxColumns, int maxCellCharacters, IReadOnlyList<string>? requiredSheets = null)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return Read(stream, bytes.Length, source, maxRows, maxColumns, maxCellCharacters, requiredSheets);
    }

    /// <summary>Reads the worksheets from a stream of <paramref name="length"/> bytes, which bounds the document.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> Read(Stream stream, long length,
        string source, int maxRows, int maxColumns, int maxCellCharacters, IReadOnlyList<string>? requiredSheets = null)
    {
        var selectedSheets = requiredSheets ?? RequiredSheets;
        var result = new Dictionary<string, IReadOnlyList<IReadOnlyList<string>>>(StringComparer.Ordinal);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = length,
        };
        using var reader = XmlReader.Create(stream, settings);
        List<IReadOnlyList<string>>? rows = null;
        var rowCount = 0;
        var worksheetDepth = -1;
        while (reader.Read())
        {
            if (reader.NamespaceURI != Namespace) continue;
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Worksheet")
            {
                var name = reader.GetAttribute("Name", Namespace);
                rows = null;
                worksheetDepth = reader.Depth;
                if (name is not null && selectedSheets.Contains(name, StringComparer.Ordinal))
                {
                    rows = [];
                    if (!result.TryAdd(name, rows))
                        throw new InvalidDataException($"Pet workbook '{source}' repeats worksheet '{name}'.");
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Worksheet")
            {
                rows = null;
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Row" && rows is not null
                     && reader.Depth == worksheetDepth + 2)
            {
                if (++rowCount > maxRows)
                    throw new InvalidDataException($"Pet workbook '{source}' exceeds max_catalog_rows.");
                rows.Add(ReadRow(reader, source, maxColumns, maxCellCharacters));
            }
        }
        foreach (var name in selectedSheets)
            if (!result.ContainsKey(name))
                throw new InvalidDataException($"Pet workbook '{source}' is missing worksheet '{name}'.");
        return result;
    }

    // The reader is on a Row start element. The row is read in place: native workbooks style thousands of empty cells
    // per row, so building an element tree per row allocated hundreds of megabytes per start for nothing.
    private static List<string> ReadRow(XmlReader reader, string source, int maxColumns, int maxCellCharacters)
    {
        var cells = new List<string>();
        if (reader.IsEmptyElement) return cells;
        var rowDepth = reader.Depth;
        var previousIndex = 0;
        while (reader.Read() && reader.Depth > rowDepth)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != rowDepth + 1
                || reader.LocalName != "Cell" || reader.NamespaceURI != Namespace)
                continue;
            var indexText = reader.GetAttribute("Index", Namespace);
            var (values, text) = ReadCell(reader);
            if (previousIndex == int.MaxValue)
                throw new InvalidDataException($"Pet workbook '{source}' has a cell index outside i32.");
            var index = previousIndex + 1;
            if (indexText is not null && (!int.TryParse(indexText, NumberStyles.None,
                    CultureInfo.InvariantCulture, out index) || index <= previousIndex))
                throw new InvalidDataException($"Pet workbook '{source}' has an invalid sparse cell index.");
            previousIndex = index;
            if (values > 1)
                throw new InvalidDataException($"Pet workbook '{source}' has more than one value in a cell.");
            if (text.Length > maxCellCharacters)
                throw new InvalidDataException($"Pet workbook '{source}' exceeds max_cell_characters.");
            // Native legacy workbooks style thousands of empty trailing cells; only later values require padding.
            if (text.Length == 0) continue;
            if (index > maxColumns)
                throw new InvalidDataException($"Pet workbook '{source}' exceeds max_catalog_columns.");
            while (cells.Count < index - 1) cells.Add(string.Empty);
            cells.Add(text);
        }
        return cells;
    }

    // The reader is on a Cell start element and ends on its end. Counts the Data children and returns the text of the
    // first one: all its text, so rich-text runs concatenate, while a note's Data one level deeper is not a value.
    private static (int Values, string Text) ReadCell(XmlReader reader)
    {
        if (reader.IsEmptyElement) return (0, string.Empty);
        var cellDepth = reader.Depth;
        var values = 0;
        string? text = null;
        StringBuilder? builder = null;
        while (reader.Read() && reader.Depth > cellDepth)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != cellDepth + 1
                || reader.LocalName != "Data" || reader.NamespaceURI != Namespace)
                continue;
            if (++values > 1 || reader.IsEmptyElement) continue;
            var dataDepth = reader.Depth;
            while (reader.Read() && reader.Depth > dataDepth)
            {
                if (reader.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace
                    or XmlNodeType.SignificantWhitespace)) continue;
                if (text is null) text = reader.Value;
                else (builder ??= new StringBuilder(text)).Append(reader.Value);
            }
        }
        return (values, builder?.ToString() ?? text ?? string.Empty);
    }
}
