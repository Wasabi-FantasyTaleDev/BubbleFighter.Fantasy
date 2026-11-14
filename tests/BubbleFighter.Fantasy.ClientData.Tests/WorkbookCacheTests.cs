using System.Text;
using BubbleFighter.Fantasy.Archives;
using BubbleFighter.Fantasy.Contracts.ClientData;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.ClientData.Tests;

/// <summary>
/// Rows that read the same workbook while the server starts share one parse: each worksheet is read once, a caller's
/// own size limit and errors still apply, and once startup has converged nothing is kept.
/// </summary>
public sealed class WorkbookCacheTests : IDisposable
{
    private const string Ss = "urn:schemas-microsoft-com:office:spreadsheet";
    private const string Path0 = "Config/ShopItem2.xml";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bff-workbook-cache-" + Guid.NewGuid().ToString("N"));

    /// <summary>A second caller is served the sheets the first parsed, and only a sheet nobody asked for yet is parsed.</summary>
    [Fact]
    public void EachWorksheetIsParsedOnce()
    {
        using var store = Store();
        var parsed = new List<string>();
        Sheets(store, ["ShopItem", "SubCategory"], parsed);
        var second = Sheets(store, ["ShopItem", "ItemAttr"], parsed);
        Assert.Equal(["ShopItem", "SubCategory", "ItemAttr"], parsed);
        Assert.Equal(["1", "a"], second["ShopItem"][1]);
        Assert.Equal(["x"], second["ItemAttr"][0]);
        Assert.Same(Sheets(store, ["ShopItem"], parsed)["ShopItem"], second["ShopItem"]);
        Assert.Equal(1, store.KeptWorkbooks);
    }

    /// <summary>
    /// When the next build's store opens, the previous store stops keeping workbooks, so a start holds one build's
    /// parsed workbooks at a time rather than every build's.
    /// </summary>
    [Fact]
    public void OpeningTheNextStoreReleasesThePreviousOne()
    {
        using var first = Store();
        using var second = Store("data2");
        ClientDataStore.BecomeCurrent(first);
        Sheets(first, ["ShopItem"], []);
        Assert.Equal(1, first.KeptWorkbooks);
        ClientDataStore.BecomeCurrent(second);
        Assert.Equal(0, first.KeptWorkbooks);
        Sheets(second, ["ShopItem"], []);
        Assert.Equal(1, second.KeptWorkbooks);
    }

    /// <summary>The shared load returns the same rows and the same refusals as an unshared one.</summary>
    [Fact]
    public void SharedLoadsMatchUnsharedLoads()
    {
        using var store = Store();
        var shared = SpreadsheetWorkbook.Load(WorkbookFile.FromStore(store, Path0), 1 << 20, "max_bytes", ["ShopItem", "SubCategory"]);
        var alone = SpreadsheetWorkbook.Load(WorkbookFile.FromBytes(Path0, store.Read(Path0)), 1 << 20, "max_bytes", ["ShopItem", "SubCategory"]);
        Assert.Equal(alone.Keys.Order(), shared.Keys.Order());
        foreach (var name in alone.Keys)
            Assert.Equal(alone[name].Select(row => row.ToArray()), shared[name].Select(row => row.ToArray()));

        // A caller's own byte limit is checked before the shared parse, and a missing sheet still fails by name.
        var small = Assert.Throws<InvalidOperationException>(() =>
            SpreadsheetWorkbook.Load(WorkbookFile.FromStore(store, Path0), 10, "tiny_limit", ["ShopItem"]));
        Assert.Contains("tiny_limit is 10", small.Message, StringComparison.Ordinal);
        var missing = Assert.Throws<InvalidOperationException>(() =>
            SpreadsheetWorkbook.Load(WorkbookFile.FromStore(store, Path0), 1 << 20, "max_bytes", ["ShopItem", "Nope"]));
        Assert.Contains("missing worksheet(s): Nope", missing.Message, StringComparison.Ordinal);
    }

    /// <summary>A failed parse keeps nothing, so the next caller asking for the same sheets fails the same way.</summary>
    [Fact]
    public void AFailedParseIsNotKept()
    {
        using var store = Store();
        var calls = 0;
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> Fail(IReadOnlyCollection<string> names)
        {
            calls++;
            throw new InvalidDataException("bad");
        }
        Assert.Throws<InvalidDataException>(() => store.Sheets(Path0, ["ShopItem"], Fail));
        Assert.Throws<InvalidDataException>(() => store.Sheets(Path0, ["ShopItem"], Fail));
        Assert.Equal(2, calls);
    }

    /// <summary>After release the store keeps nothing and every load parses again.</summary>
    [Fact]
    public void ReleasedStoresKeepNothing()
    {
        using var store = Store();
        var parsed = new List<string>();
        Sheets(store, ["ShopItem"], parsed);
        store.ReleaseWorkbooks();
        Assert.Equal(0, store.KeptWorkbooks);
        Sheets(store, ["ShopItem"], parsed);
        Sheets(store, ["ShopItem"], parsed);
        Assert.Equal(["ShopItem", "ShopItem", "ShopItem"], parsed);
        Assert.Equal(0, store.KeptWorkbooks);
    }

    /// <summary>Workbooks are kept per client path, ignoring letter case as the store's lookups do.</summary>
    [Fact]
    public void PathsAreKeptIgnoringLetterCase()
    {
        using var store = Store();
        var parsed = new List<string>();
        Sheets(store, ["ShopItem"], parsed);
        var upper = store.Sheets("config/SHOPITEM2.xml", ["ShopItem"], names => throw new InvalidOperationException("parsed again"));
        Assert.Equal(["1", "a"], upper["ShopItem"][1]);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> Sheets(ClientDataStore store,
        string[] names, List<string> parsed) =>
        store.Sheets(Path0, names, missing =>
        {
            parsed.AddRange(missing);
            return SpreadsheetWorkbook.Load(WorkbookFile.FromBytes(Path0, store.Read(Path0)), 1 << 20, "max_bytes", missing);
        });

    private ClientDataStore Store(string name = "data")
    {
        var data = Path.Combine(_root, name);
        var file = Path.Combine(data, "Config", "Config.bfz");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var xml = $"<?xml version=\"1.0\"?><Workbook xmlns=\"{Ss}\" xmlns:ss=\"{Ss}\">"
            + Sheet("ShopItem", ["itemId", "name"], ["1", "a"])
            + Sheet("SubCategory", ["id", "name"])
            + Sheet("ItemAttr", ["x"])
            + "</Workbook>";
        using (var stream = File.Create(file))
            BfzArchive.Write(stream, "Config/Config.bfz", [(Path0, Encoding.UTF8.GetBytes(xml))]);
        return ClientDataStore.Open(data, Path.Combine(_root, "dataraw"), NullLogger.Instance);
    }

    private static string Sheet(string name, params string[][] rows) =>
        $"<Worksheet ss:Name=\"{name}\"><Table>"
        + string.Concat(rows.Select(row => "<Row>" + string.Concat(row.Select(cell => $"<Cell><Data ss:Type=\"String\">{cell}</Data></Cell>")) + "</Row>"))
        + "</Table></Worksheet>";

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
