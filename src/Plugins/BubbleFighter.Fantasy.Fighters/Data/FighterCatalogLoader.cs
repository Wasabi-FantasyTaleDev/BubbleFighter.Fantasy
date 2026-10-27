using BubbleFighter.Fantasy.Contracts.ClientData;

namespace BubbleFighter.Fantasy.Fighters.Data;

/// <summary>
/// Builds a <see cref="FighterCatalog"/> from <c>Config/FighterConfig.xml</c>
/// worksheet <c>FighterConfig</c> (<c>FighterConfig:HorzTable</c>). Missing file,
/// missing sheet, missing <c>[Field]</c> / <c>name</c> / <c>chrTypeIdx</c> /
/// <c>isSelectable</c> rows, or zero Field columns fail loud.
/// </summary>
public static class FighterCatalogLoader
{
    /// <summary>Worksheet that holds the HorzTable used by login type resolution.</summary>
    public const string SheetName = "FighterConfig";

    /// <summary>The row option that owns the size cap, named in the workbook reader's size error.</summary>
    private const string LimitKey = "max_fighter_config_bytes";

    /// <summary>Loads and validates the HorzTable from the bytes the client data store returns.</summary>
    /// <param name="content">The whole <c>FighterConfig.xml</c> file.</param>
    /// <param name="source">The client path the bytes came from, for diagnostics.</param>
    /// <param name="maxBytes">Inclusive size cap.</param>
    public static FighterCatalog Load(ReadOnlyMemory<byte> content, string source, long maxBytes)
    {
        var sheets = SpreadsheetWorkbook.Load(
            WorkbookFile.FromBytes(source, content.ToArray()), maxBytes, LimitKey, [SheetName]);
        var table = sheets[SheetName];
        if (table.Count == 0)
            throw new InvalidOperationException($"Fighter config {source} sheet {SheetName} has no rows.");

        var fieldRow = RequireKeyedRow(table, "[Field]", source);
        var nameRow = RequireKeyedRow(table, "name", source);
        var chrRow = RequireKeyedRow(table, "chrTypeIdx", source);
        var selectableRow = RequireKeyedRow(table, "isSelectable", source);

        // Column 0 is the row key; column 1 is [Desc]. Field ids start at column 2.
        var columns = new List<(int Index, int FieldId)>();
        for (var i = 2; i < fieldRow.Count; i++)
        {
            var text = fieldRow[i].Trim();
            if (text.Length == 0)
                continue;
            if (!int.TryParse(text, out var fieldId))
                throw new InvalidOperationException($"Fighter config {source} [Field] column {i} is not an i32: '{text}'.");
            columns.Add((i, fieldId));
        }

        if (columns.Count == 0)
            throw new InvalidOperationException($"Fighter config {source} [Field] row has no Field columns.");
        if (columns.Count > FighterCatalog.MaxFieldColumns)
            throw new InvalidOperationException(
                $"Fighter config {source} has {columns.Count} Field columns; max is {FighterCatalog.MaxFieldColumns}.");

        var rows = new List<FighterItemRow>(columns.Count);
        var seen = new HashSet<int>();
        foreach (var (index, fieldId) in columns)
        {
            if (!seen.Add(fieldId))
                throw new InvalidOperationException($"Fighter config {source} repeats Field {fieldId}.");
            var name = Cell(nameRow, index);
            if (name.Length == 0)
                throw new InvalidOperationException($"Fighter config {source} Field {fieldId} has an empty name.");
            var chrTypeIdx = IntCell(chrRow, index, source, "chrTypeIdx");
            var selectable = BoolCell(selectableRow, index);
            var itemId = FighterCatalog.IsPlayableField(fieldId) ? FighterCatalog.ItemIdForField(fieldId) : 0;
            if (FighterCatalog.IsPlayableField(fieldId) && chrTypeIdx != fieldId)
            {
                throw new InvalidOperationException(
                    $"Fighter config {source} Field {fieldId} chrTypeIdx is {chrTypeIdx}; 1..31 columns must match.");
            }

            rows.Add(new FighterItemRow(fieldId, name, chrTypeIdx, selectable, itemId));
        }

        var playable = rows.Count(r => r.ItemId != 0);
        if (playable == 0)
            throw new InvalidOperationException($"Fighter config {source} has no Field columns in 1..31.");

        return new FighterCatalog(source, rows);
    }

    private static IReadOnlyList<string> RequireKeyedRow(
        IReadOnlyList<IReadOnlyList<string>> table, string key, string source)
    {
        foreach (var row in table)
        {
            if (row.Count > 0 && string.Equals(row[0].Trim(), key, StringComparison.Ordinal))
                return row;
        }

        throw new InvalidOperationException($"Fighter config {source} sheet {SheetName} is missing row '{key}'.");
    }

    private static string Cell(IReadOnlyList<string> row, int index) =>
        index < row.Count ? row[index].Trim() : string.Empty;

    private static int IntCell(IReadOnlyList<string> row, int index, string source, string key)
    {
        var text = Cell(row, index);
        if (string.IsNullOrEmpty(text))
            return 0;
        if (!int.TryParse(text, out var value))
            throw new InvalidOperationException($"Fighter config {source} {key} is not an i32: '{text}'.");
        return value;
    }

    private static bool BoolCell(IReadOnlyList<string> row, int index)
    {
        var text = Cell(row, index);
        if (text.Length == 0)
            return false;
        if (bool.TryParse(text, out var flag))
            return flag;
        return text is not "0";
    }
}
