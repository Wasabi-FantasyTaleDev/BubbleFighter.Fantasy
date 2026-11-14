using System.Globalization;
using System.Text;
using System.Xml;

namespace BubbleFighter.Fantasy.InGame.Data;

internal sealed class CustomGameRulesWorkbookTable
{
    private readonly string _name;
    private readonly Dictionary<string, int> _columns = new(StringComparer.Ordinal);

    internal CustomGameRulesWorkbookTable(string name, IReadOnlyList<IReadOnlyList<string>> rows, int headerRow, params string[] required)
    {
        _name = name;
        if (rows.Count <= headerRow) throw new InvalidDataException($"Custom room catalog {name} has no header.");
        for (var index = 0; index < rows[headerRow].Count; index++)
        {
            var column = rows[headerRow][index].Trim();
            if (column.Length > 0 && !_columns.TryAdd(column, index))
                throw new InvalidDataException($"Custom room catalog {name} repeats column '{column}'.");
        }
        foreach (var column in required)
            if (!_columns.ContainsKey(column)) throw new InvalidDataException($"Custom room catalog {name} is missing '{column}'.");
        Rows = rows.Skip(headerRow + 1);
    }

    internal IEnumerable<IReadOnlyList<string>> Rows { get; }
    internal bool HasColumn(string column) => _columns.ContainsKey(column);
    internal string Cell(IReadOnlyList<string> row, string column) =>
        _columns[column] < row.Count ? row[_columns[column]].Trim() : string.Empty;
    internal string Required(IReadOnlyList<string> row, string column) => Cell(row, column) is { Length: > 0 } text ? text
        : throw new InvalidDataException($"Custom room catalog {_name}.{column} is empty.");
    internal int Integer(IReadOnlyList<string> row, string column, bool emptyIsZero = false)
    {
        var text = Cell(row, column);
        if (emptyIsZero && text.Length == 0) return 0;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value
            : throw new InvalidDataException($"Custom room catalog {_name}.{column} is not an i32.");
    }
    internal decimal Decimal(IReadOnlyList<string> row, string column) =>
        decimal.TryParse(Required(row, column), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value
            : throw new InvalidDataException($"Custom room catalog {_name}.{column} is not a finite decimal.");
    internal bool TryKey(IReadOnlyList<string> row, string column, out int id)
    {
        id = 0;
        var text = Cell(row, column);
        if (text.Length == 0 || text.StartsWith('#')) return false;
        id = Integer(row, column);
        if (id < 0) throw new InvalidDataException($"Custom room catalog {_name}.{column} is negative.");
        return id != 0;
    }
}

internal static class CustomGameRulesWorkbook
{
    private const string Namespace = "urn:schemas-microsoft-com:office:spreadsheet";

    internal static Dictionary<string, IReadOnlyList<IReadOnlyList<string>>> Read(byte[] bytes, string path,
        int maxRows, int maxColumns, int maxCellCharacters, IReadOnlyList<string> requiredSheets)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            IgnoreComments = true, IgnoreWhitespace = true, MaxCharactersInDocument = bytes.Length };
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, settings);
        var sheets = new Dictionary<string, IReadOnlyList<IReadOnlyList<string>>>(StringComparer.Ordinal);
        List<IReadOnlyList<string>>? rows = null;
        var worksheetDepth = -1;
        var rowCount = 0;
        var rowIndex = 0;
        while (reader.Read())
        {
            if (reader.NamespaceURI != Namespace) continue;
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Worksheet")
            {
                var name = reader.GetAttribute("Name", Namespace);
                worksheetDepth = reader.Depth;
                rowIndex = 0;
                rows = null;
                if (name is not null && requiredSheets.Contains(name, StringComparer.Ordinal))
                {
                    rows = [];
                    if (!sheets.TryAdd(name, rows)) throw new InvalidDataException($"Custom room workbook '{path}' repeats '{name}'.");
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Worksheet") rows = null;
            else if (rows is not null && reader.NodeType == XmlNodeType.Element && reader.LocalName == "Row"
                     && reader.Depth == worksheetDepth + 2)
            {
                rowIndex = Index(reader, rowIndex);
                var retainedRows = rowIndex - rows.Count;
                if (retainedRows > maxRows - rowCount)
                    throw new InvalidDataException($"Custom room workbook '{path}' exceeds custom_room_rules_max_rows.");
                rowCount += retainedRows;
                using var row = reader.ReadSubtree();
                while (rows.Count < rowIndex - 1) rows.Add(Array.Empty<string>());
                rows.Add(ReadRow(row, maxColumns, maxCellCharacters));
            }
        }
        foreach (var name in requiredSheets)
            if (!sheets.ContainsKey(name)) throw new InvalidDataException($"Custom room workbook '{path}' is missing '{name}'.");
        return sheets;
    }

    private static IReadOnlyList<string> ReadRow(XmlReader row, int maxColumns, int maxCellCharacters)
    {
        var cells = new List<string>();
        var index = 0;
        while (row.Read())
        {
            if (row.NodeType != XmlNodeType.Element || row.LocalName != "Cell" || row.NamespaceURI != Namespace || row.Depth != 1) continue;
            index = Index(row, index);
            using var cell = row.ReadSubtree();
            var text = ReadCell(cell, maxCellCharacters);
            // Styled empty trailing cells do not allocate sparse padding; later nonempty values remain bounded.
            if (text.Length == 0) continue;
            if (index > maxColumns) throw new InvalidDataException("Custom room workbook exceeds custom_room_rules_max_columns.");
            while (cells.Count < index - 1) cells.Add(string.Empty);
            cells.Add(text);
        }
        return cells;
    }

    private static int Index(XmlReader reader, int previous)
    {
        var text = reader.GetAttribute("Index", Namespace);
        if (previous == int.MaxValue) throw new InvalidDataException("Custom room workbook index overflows i32.");
        var index = previous + 1;
        if (text is not null && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out index) || index <= previous))
            throw new InvalidDataException("Custom room workbook has an invalid sparse index.");
        return index;
    }

    private static string ReadCell(XmlReader cell, int maxCellCharacters)
    {
        string? result = null;
        while (cell.Read())
        {
            if (cell.NodeType != XmlNodeType.Element || cell.LocalName != "Data" || cell.NamespaceURI != Namespace || cell.Depth != 1) continue;
            if (result is not null) throw new InvalidDataException("Custom room workbook repeats a cell value.");
            using var data = cell.ReadSubtree();
            var text = new StringBuilder();
            var buffer = new char[Math.Min(maxCellCharacters, 1024)];
            while (data.Read())
            {
                if (data.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)) continue;
                int length;
                while ((length = data.ReadValueChunk(buffer, 0, buffer.Length)) != 0)
                {
                    if (length > maxCellCharacters - text.Length) throw new InvalidDataException("Custom room workbook exceeds custom_room_rules_max_cell_characters.");
                    text.Append(buffer, 0, length);
                }
            }
            result = text.ToString();
        }
        return result ?? string.Empty;
    }
}
