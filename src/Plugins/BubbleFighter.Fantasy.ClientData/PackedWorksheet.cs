using BubbleFighter.Fantasy.Contracts.ClientData;
using MemoryPack;

namespace BubbleFighter.Fantasy.ClientData;

/// <summary>
/// One worksheet as <see cref="SpreadsheetWorkbook"/> parses it, in the form kept in the compiled client data. A
/// sheet repeats few distinct texts many times (empty cells, flags, category names), so each distinct text is kept
/// once and a cell is the index of its text; rows read back share those strings.
/// </summary>
/// <param name="Texts">The distinct cell texts, in order of first use.</param>
/// <param name="Rows">Per row, the index in <paramref name="Texts"/> of each cell.</param>
[MemoryPackable]
internal sealed partial record PackedWorksheet(string[] Texts, int[][] Rows)
{
    /// <summary>The catalog of one worksheet of one workbook.</summary>
    internal static ICompiledCatalog<IReadOnlyList<IReadOnlyList<string>>> Catalog(string path, string sheet) =>
        new PackedCatalog<IReadOnlyList<IReadOnlyList<string>>, PackedWorksheet>(
            "worksheet:" + path.ToLowerInvariant() + ":" + sheet, Pack, Unpack);

    private static PackedWorksheet Pack(IReadOnlyList<IReadOnlyList<string>> sheet)
    {
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var texts = new List<string>();
        var rows = new int[sheet.Count][];
        for (var row = 0; row < rows.Length; row++)
        {
            var cells = sheet[row];
            var packed = rows[row] = new int[cells.Count];
            for (var cell = 0; cell < packed.Length; cell++)
            {
                if (!indexes.TryGetValue(cells[cell], out var index))
                {
                    indexes.Add(cells[cell], index = texts.Count);
                    texts.Add(cells[cell]);
                }
                packed[cell] = index;
            }
        }
        return new(texts.ToArray(), rows);
    }

    private static IReadOnlyList<IReadOnlyList<string>> Unpack(PackedWorksheet packed)
    {
        if (packed.Texts is not { } texts || packed.Rows is not { } packedRows || Array.IndexOf(texts, null) >= 0)
            throw new InvalidDataException("A compiled worksheet lacks its texts or rows.");
        var rows = new IReadOnlyList<string>[packedRows.Length];
        for (var row = 0; row < rows.Length; row++)
        {
            var indexes = packedRows[row] ?? throw new InvalidDataException("A compiled worksheet lacks a row.");
            var cells = new string[indexes.Length];
            for (var cell = 0; cell < cells.Length; cell++)
                cells[cell] = (uint)indexes[cell] < (uint)texts.Length ? texts[indexes[cell]]
                    : throw new InvalidDataException("A compiled worksheet cell names a text the sheet does not have.");
            rows[row] = cells;
        }
        return rows;
    }
}
