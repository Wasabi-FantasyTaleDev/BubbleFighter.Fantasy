namespace BubbleFighter.Fantasy.Contracts.ClientData;

/// <summary>
/// A client data store's shared parse of its workbooks while the server starts. The item workbook of one build is read
/// by about nine rows; each parsed it again, which was most of a start's work. A store that implements this keeps
/// the worksheets it has parsed until <c>StartupConvergedEvent</c> and then keeps nothing, so the shared rows cost
/// memory only during the start. <see cref="SpreadsheetWorkbook.Load"/> uses it through
/// <see cref="WorkbookFile.Cache"/>; rows never call it directly.
/// </summary>
public interface IClientWorkbookCache
{
    /// <summary>
    /// Returns the requested worksheets of the workbook at <paramref name="path"/>. Worksheets already parsed are
    /// returned as kept; the rest are parsed by <paramref name="parse"/>, which receives only the missing names and
    /// fails exactly as an unshared load would. The returned rows are shared between callers and must not be changed.
    /// </summary>
    /// <param name="path">The workbook's client path.</param>
    /// <param name="sheetNames">The worksheet names the caller needs.</param>
    /// <param name="parse">Parses the named worksheets from the workbook.</param>
    /// <returns>The requested worksheets, worksheet name to rows of cell texts.</returns>
    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> Sheets(string path, IReadOnlyCollection<string> sheetNames,
        Func<IReadOnlyCollection<string>, IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>>> parse);
}
