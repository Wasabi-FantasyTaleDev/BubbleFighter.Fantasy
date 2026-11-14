using System.Text;
using System.Xml;

namespace BubbleFighter.Fantasy.Contracts.ClientData;

/// <summary>
/// Reads the requested worksheets of the client's SpreadsheetML workbooks (<c>Config/ShopItem2.xml</c>,
/// <c>Config/ShopItemPet.xml</c> and <c>Config/FighterConfig.xml</c>). Only the named worksheets are kept, and
/// sparse <c>ss:Index</c> cells are expanded to their column positions, so a row's cell <c>i</c> is column
/// <c>i</c> of the sheet. The byte length is bounded before the parse, and the size error names the row
/// option that owns the cap, so an operator knows which YAML key to raise.
/// </summary>
public static class SpreadsheetWorkbook
{
    private const string Ss = "urn:schemas-microsoft-com:office:spreadsheet";

    // The last column a SpreadsheetML sheet can address (XFD); a sparse ss:Index past it is malformed, and padding
    // up to it is the most a row can allocate.
    private const int MaxColumn = 16384;

    /// <summary>
    /// Reads the requested worksheets from one workbook the client data store returns. A cell's value is the
    /// concatenated text of its <c>ss:Data</c> child (rich-text runs included); a cell note's deeper
    /// <c>ss:Data</c> inside a <c>Comment</c> is not read as the value. Throws
    /// <see cref="InvalidOperationException"/> when the path is blank, the file is empty, the file is larger
    /// than <paramref name="maxBytes"/>, or a requested worksheet is missing.
    /// </summary>
    /// <param name="file">The workbook, named by its client path, as the client data store returns it.</param>
    /// <param name="maxBytes">Inclusive size cap.</param>
    /// <param name="limitKey">The snake_case row option that owns <paramref name="maxBytes"/>, named in the
    /// size error.</param>
    /// <param name="sheetNames">Worksheet names to keep.</param>
    /// <returns>The kept sheets, worksheet name to rows of cell texts, in document order.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> Load(
        WorkbookFile file, long maxBytes, string limitKey, IReadOnlyCollection<string> sheetNames)
    {
        if (string.IsNullOrWhiteSpace(file.Name))
            throw new InvalidOperationException("Spreadsheet workbook source path is empty.");
        if (file.Length <= 0)
            throw new InvalidOperationException($"Spreadsheet workbook {file.Name} is empty.");
        if (file.Length > maxBytes)
            throw new InvalidOperationException(
                $"Spreadsheet workbook {file.Name} is {file.Length} bytes; {limitKey} is {maxBytes}.");
        // The caller's own size limit is checked above either way, so a shared parse never widens what it accepts.
        return file.Cache is { } cache
            ? cache.Sheets(file.Name, sheetNames, missing => Parse(file, missing))
            : Parse(file, sheetNames);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> Parse(
        WorkbookFile file, IReadOnlyCollection<string> sheetNames)
    {
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
                // A second copy of a requested sheet was silently dropped; which copy the client reads is not established.
                if (rows is not null && sheets.ContainsKey(current!))
                    throw new InvalidOperationException($"Spreadsheet workbook {file.Name} repeats worksheet '{current}'.");
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Row" && reader.NamespaceURI == Ss && rows is not null)
            {
                var cells = new List<string>();
                var nextIndex = 1;
                ReadRow(reader, cells, ref nextIndex, file.Name);
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
            throw new InvalidOperationException(
                $"Spreadsheet workbook {file.Name} is missing worksheet(s): {string.Join(", ", missing)}.");
        return sheets.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<IReadOnlyList<string>>)kv.Value, StringComparer.Ordinal);
    }

    private static void Flush(
        Dictionary<string, List<IReadOnlyList<string>>> sheets, string? name, List<IReadOnlyList<string>>? rows)
    {
        if (name is null || rows is null || sheets.ContainsKey(name))
            return;
        sheets[name] = rows;
    }

    private static void ReadRow(XmlReader reader, List<string> cells, ref int nextIndex, string name)
    {
        if (reader.IsEmptyElement)
            return;
        var depth = reader.Depth;
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Row" && reader.Depth == depth))
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Cell" || reader.NamespaceURI != Ss)
                continue;
            var indexAttr = reader.GetAttribute("Index", Ss);
            if (indexAttr is not null)
            {
                // A sparse index only moves forward and stays within the sheet; checked before any padding is added.
                if (!int.TryParse(indexAttr, out var index) || index < nextIndex || index > MaxColumn)
                    throw new InvalidOperationException(
                        $"Spreadsheet workbook {name} has an invalid cell index '{indexAttr}' at column {nextIndex}.");
                nextIndex = index;
            }
            if (nextIndex > MaxColumn)
                throw new InvalidOperationException($"Spreadsheet workbook {name} has a row past column {MaxColumn}.");
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

        // Nearly every cell is one text node, so its text is returned as read; a builder is made only for a second run.
        string? first = null;
        StringBuilder? value = null;
        var depth = reader.Depth;
        while (reader.Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType is XmlNodeType.Text
                or XmlNodeType.CDATA
                or XmlNodeType.Whitespace
                or XmlNodeType.SignificantWhitespace)
            {
                if (first is null) first = reader.Value;
                else (value ??= new StringBuilder(first)).Append(reader.Value);
            }
        }

        return value?.ToString() ?? first ?? string.Empty;
    }
}

/// <summary>A workbook to read, as the client data store returns it.</summary>
/// <param name="Name">The client path shown in messages.</param>
/// <param name="Length">The byte length.</param>
/// <param name="Open">Opens a fresh read-only stream over the bytes.</param>
public sealed record WorkbookFile(string Name, long Length, Func<Stream> Open)
{
    /// <summary>
    /// Gets the store's shared parse of this workbook, when the store keeps one, so the rows that read the same file
    /// while the server starts parse it once. Null reads the file on every load.
    /// </summary>
    public IClientWorkbookCache? Cache { get; init; }

    /// <summary>A workbook already read into memory, for example from the client's archives.</summary>
    /// <param name="name">The client path it was read from.</param>
    /// <param name="bytes">Its contents.</param>
    public static WorkbookFile FromBytes(string name, byte[] bytes) =>
        new(name, bytes.LongLength, () => new MemoryStream(bytes, writable: false));

    /// <summary>
    /// A workbook the client data store streams when it is parsed, so the file is never held whole. The size is
    /// asked now, which is also when a missing file is reported.
    /// </summary>
    /// <param name="store">The client data store.</param>
    /// <param name="path">The client path.</param>
    /// <returns>The workbook.</returns>
    /// <exception cref="FileNotFoundException">The client data has no such file.</exception>
    public static WorkbookFile FromStore(IClientDataStore store, string path)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new(path, store.Length(path), () => store.OpenRead(path)) { Cache = store as IClientWorkbookCache };
    }

    /// <summary>
    /// A workbook the client data store streams when it is parsed, refused before it is opened when it is larger
    /// than the caller accepts.
    /// </summary>
    /// <param name="store">The client data store.</param>
    /// <param name="path">The client path.</param>
    /// <param name="maximumBytes">The largest accepted uncompressed file size.</param>
    /// <returns>The workbook.</returns>
    /// <exception cref="FileNotFoundException">The client data has no such file.</exception>
    /// <exception cref="InvalidDataException">The file is larger than <paramref name="maximumBytes"/>.</exception>
    public static WorkbookFile FromStore(IClientDataStore store, string path, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        var file = FromStore(store, path);
        return file.Length <= maximumBytes ? file : throw new InvalidDataException($"Client file '{path}' exceeds its byte limit.");
    }
}
