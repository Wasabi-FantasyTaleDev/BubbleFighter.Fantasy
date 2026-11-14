using System.Text;
using BubbleFighter.Fantasy.Archives;
using BubbleFighter.Fantasy.Contracts.ClientData;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BubbleFighter.Fantasy.ClientData.Tests;

/// <summary>
/// The compiled client data file is a cache and never a source: a catalog comes from it only while the archives, the
/// loose files the catalog read, the row's inputs and the assemblies are the ones it was written from, and anything
/// else, including a missing, truncated or damaged file, builds the catalog from the client data again.
/// </summary>
public sealed class CompiledDataFileTests : IDisposable
{
    private const string Member = "Config/Names.txt";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bff-compiled-data-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private int _compiles;

    /// <summary>The first start builds the catalog and writes the file; the next start reads it without the client data.</summary>
    [Fact]
    public void ALaterStartReadsWhatTheFirstCompiled()
    {
        WriteArchive("alpha", "beta");
        using (var first = Store())
        {
            Assert.Equal(["alpha", "beta"], Load(first));
            Assert.False(File.Exists(CompiledPath));
            first.EndStartup();
            Assert.True(File.Exists(CompiledPath));
            // Within one start the catalog is served from what was just compiled, also after it was written.
            Assert.Equal(["alpha", "beta"], Load(first));
        }
        Assert.Equal(1, _compiles);
        using var second = Store();
        Assert.Equal(["alpha", "beta"], Load(second));
        Assert.Equal(1, _compiles);
        Assert.Equal(1, second.CompiledFile!.Loaded);
        Assert.Equal(0, second.CompiledFile.Compiled);
    }

    /// <summary>Deleting the file changes nothing but the work of the next start.</summary>
    [Fact]
    public void DeletingTheFileOnlyCompilesAgain()
    {
        WriteArchive("alpha");
        using (var first = Store()) Load(first);
        File.Delete(CompiledPath);
        using var second = Store();
        Assert.Equal(["alpha"], Load(second));
        Assert.Equal(2, _compiles);
    }

    /// <summary>A store without a compiled file, and a store that is not a compiled store at all, build every time.</summary>
    [Fact]
    public void WithoutAFileEveryLoadCompiles()
    {
        WriteArchive("alpha");
        using var store = ClientDataStore.Open(Path.Combine(_root, "data"), Path.Combine(_root, "dataraw"), new ListLogger(_log));
        Load(store);
        Load(store);
        Assert.Equal(2, _compiles);
        Assert.Null(store.CompiledFile);
        Assert.Equal(["x"], CompiledClientData.Load(new BareStore(), new LinesCatalog(), "", _ => new[] { "x" }));
    }

    /// <summary>Other inputs are another catalog; both are kept, and neither is returned for the other.</summary>
    [Fact]
    public void OtherInputsCompileAgainAndAreKeptBesideTheFirst()
    {
        WriteArchive("alpha");
        using (var first = Store())
        {
            Assert.Equal(["alpha"], Load(first, "upper=false"));
            Assert.Equal(["ALPHA"], Load(first, "upper=true", upper: true));
        }
        Assert.Equal(2, _compiles);
        using var second = Store();
        Assert.Equal(["ALPHA"], Load(second, "upper=true", upper: true));
        Assert.Equal(["alpha"], Load(second, "upper=false"));
        Assert.Equal(2, _compiles);
    }

    /// <summary>An archive that changed in size or time makes the whole file stale, and the log says which one.</summary>
    [Fact]
    public void AChangedArchiveDiscardsTheFile()
    {
        WriteArchive("alpha");
        using (var first = Store()) Load(first);
        WriteArchive("alpha", "gamma");
        using var second = Store();
        Assert.Equal(["alpha", "gamma"], Load(second));
        Assert.Equal(2, _compiles);
        Assert.Contains(_log, line => line.Contains("archive 'Config/Config.bfz' changed", StringComparison.Ordinal));
    }

    /// <summary>
    /// A loose file wins over the archives, so a loose file that appears, changes or disappears for a path the catalog
    /// read makes that catalog stale. A loose file the catalog never asked for does not.
    /// </summary>
    [Fact]
    public void ALooseFileTheCatalogReadMakesItStale()
    {
        WriteArchive("alpha");
        using (var first = Store()) Load(first);

        WriteLoose("Config/Other.txt", "unrelated");
        using (var unrelated = Store()) Assert.Equal(["alpha"], Load(unrelated));
        Assert.Equal(1, _compiles);

        WriteLoose(Member, "loose");
        using (var appeared = Store()) Assert.Equal(["loose"], Load(appeared));
        Assert.Equal(2, _compiles);
        using (var same = Store()) Assert.Equal(["loose"], Load(same));
        Assert.Equal(2, _compiles);

        WriteLoose(Member, "loose\nedited");
        using (var edited = Store()) Assert.Equal(["loose", "edited"], Load(edited));
        Assert.Equal(3, _compiles);

        File.Delete(Path.Combine(_root, "dataraw", Member));
        using (var removed = Store()) Assert.Equal(["alpha"], Load(removed));
        Assert.Equal(4, _compiles);
        Assert.Contains(_log, line => line.Contains("the loose file state of 'Config/Names.txt' changed", StringComparison.Ordinal));
    }

    /// <summary>A damaged section fails its checksum and is compiled again; the damaged bytes are never parsed.</summary>
    [Fact]
    public void ADamagedSectionIsCompiledAgain()
    {
        WriteArchive("alpha");
        using (var first = Store()) Load(first);
        var bytes = File.ReadAllBytes(CompiledPath);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(CompiledPath, bytes);
        var catalog = new LinesCatalog();
        using (var second = Store())
            Assert.Equal(["alpha"], Load(second, catalog: catalog));
        Assert.Equal(2, _compiles);
        // Read once to check the new section's round trip, never for the damaged one.
        Assert.Equal(1, catalog.Reads);
        Assert.Contains(_log, line => line.Contains("its checksum does not match", StringComparison.Ordinal));
        using var third = Store();
        Assert.Equal(["alpha"], Load(third));
        Assert.Equal(2, _compiles);
    }

    /// <summary>A truncated file, or a file that is something else, yields nothing and is rewritten.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(40)]
    [InlineData(-10)]
    public void ATruncatedFileIsRewritten(int keep)
    {
        WriteArchive("alpha");
        using (var first = Store()) Load(first);
        var bytes = File.ReadAllBytes(CompiledPath);
        File.WriteAllBytes(CompiledPath, bytes[..(keep >= 0 ? keep : bytes.Length + keep)]);
        using (var second = Store()) Assert.Equal(["alpha"], Load(second));
        Assert.Equal(2, _compiles);
        using var third = Store();
        Assert.Equal(["alpha"], Load(third));
        Assert.Equal(2, _compiles);
    }

    /// <summary>A catalog whose format loses data on the way is reported and never kept.</summary>
    [Fact]
    public void ACatalogThatDoesNotRoundTripIsNotKept()
    {
        WriteArchive("alpha", "beta");
        using (var first = Store())
            Assert.Equal(["alpha", "beta"], Load(first, catalog: new LinesCatalog { DropLastOnRead = true }));
        Assert.Contains(_log, line => line.Contains("is not kept", StringComparison.Ordinal));
        using var second = Store();
        Assert.Equal(["alpha", "beta"], Load(second));
        Assert.Equal(2, _compiles);
    }

    /// <summary>A catalog that leaves part of its section unread is not trusted.</summary>
    [Fact]
    public void ASectionThatIsNotReadToItsEndIsCompiledAgain()
    {
        WriteArchive("alpha", "beta");
        using (var first = Store()) Load(first);
        using var second = Store();
        Assert.Equal(["alpha", "beta"], Load(second, catalog: new LinesCatalog { StopEarly = true }));
        Assert.Equal(2, _compiles);
    }

    /// <summary>Once the start is over a newly compiled catalog is written when it is built, not at some later event.</summary>
    [Fact]
    public void AfterStartupACompiledCatalogIsWrittenAtOnce()
    {
        WriteArchive("alpha");
        using var store = Store();
        store.EndStartup();
        Assert.False(File.Exists(CompiledPath));
        Load(store);
        Assert.True(File.Exists(CompiledPath));
    }

    /// <summary>When the next build's store opens, the previous build's catalogs go to its file.</summary>
    [Fact]
    public void OpeningTheNextStoreWritesThePreviousOnesCatalogs()
    {
        WriteArchive("alpha");
        using var first = Store();
        ClientDataStore.BecomeCurrent(first);
        Load(first);
        Assert.False(File.Exists(CompiledPath));
        using var second = ClientDataStore.Open(Path.Combine(_root, "data"), Path.Combine(_root, "dataraw"), new ListLogger(_log));
        ClientDataStore.BecomeCurrent(second);
        Assert.True(File.Exists(CompiledPath));
    }

    /// <summary>A directory that cannot hold the file costs nothing but the cache: the catalog is still returned.</summary>
    [Fact]
    public void AnUnwritableLocationStillServesTheCatalog()
    {
        WriteArchive("alpha");
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "a file where the directory should be");
        using var store = ClientDataStore.Open(Path.Combine(_root, "data"), Path.Combine(_root, "dataraw"), new ListLogger(_log),
            Path.Combine(blocker, "kr.bffdata"));
        Assert.Equal(["alpha"], Load(store));
        store.EndStartup();
        Assert.Contains(_log, line => line.Contains("could not be written", StringComparison.Ordinal));
        Assert.Equal(["alpha"], Load(store));
    }

    /// <summary>The inputs text separates names, values and lists, so different inputs never read the same.</summary>
    [Fact]
    public void InputsAreUnambiguous()
    {
        Assert.NotEqual(new CompiledInputs().Add("a", "b;1:c=1:d").ToString(), new CompiledInputs().Add("a", "b").Add("c", "d").ToString());
        Assert.NotEqual(new CompiledInputs().Add("a", (string?)null).ToString(), new CompiledInputs().Add("a", "").ToString());
        Assert.NotEqual(new CompiledInputs().Add("a", ["x", "y"]).ToString(), new CompiledInputs().Add("a", ["x"]).Add("a[1]", "y").ToString());
        Assert.Equal(new CompiledInputs().Add("n", 5).Add("f", true).ToString(), new CompiledInputs().Add("n", "5").Add("f", "true").ToString());
        var file = Path.Combine(_root, "input.yaml");
        Directory.CreateDirectory(_root);
        var absent = new CompiledInputs().AddFile("file", file).ToString();
        File.WriteAllText(file, "one");
        var present = new CompiledInputs().AddFile("file", file).ToString();
        File.WriteAllText(file, "one and more");
        Assert.NotEqual(absent, present);
        Assert.NotEqual(present, new CompiledInputs().AddFile("file", file).ToString());
    }

    /// <summary>
    /// A workbook's worksheets are kept as parsed: the next start returns the same rows without opening the workbook,
    /// parses only a worksheet nobody asked for before, and parses again once the workbook changes.
    /// </summary>
    [Fact]
    public void WorksheetsAreReadBackAsTheyWereParsed()
    {
        const string book = "Config/Book.xml";
        WriteWorkbook(book, ("Items", [["id", "name", ""], ["1", "A & B", ""], [], ["2", "\u00e9\u4e2d \n", "1"]]), ("Empty", []), ("Other", [["x"]]));
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<string>>> parsed;
        using (var first = Store())
            parsed = SpreadsheetWorkbook.Load(WorkbookFile.FromStore(first, book), 1 << 20, "max_bytes", ["Items", "Empty"]);
        Assert.Equal(4, parsed["Items"].Count);

        using (var second = Store())
        {
            // The workbook is not opened: a store whose workbook cannot be parsed still returns the kept worksheets.
            var kept = second.Sheets(book, ["Items", "Empty"], _ => throw new InvalidOperationException("parsed again"));
            Assert.Equal(["Empty", "Items"], kept.Keys.Order());
            foreach (var name in kept.Keys)
                Assert.Equal(parsed[name].Select(row => row.ToArray()), kept[name].Select(row => row.ToArray()));
            Assert.Equal(2, second.CompiledFile!.Loaded);
            // Only the worksheet that was never parsed is asked of the workbook.
            var asked = new List<string>();
            var more = second.Sheets(book.ToUpperInvariant(), ["Items", "Other"], missing =>
            {
                asked.AddRange(missing);
                return SpreadsheetWorkbook.Load(WorkbookFile.FromStore(new BareStoreOver(second), book), 1 << 20, "max_bytes", missing);
            });
            Assert.Equal(["Other"], asked);
            Assert.Equal(["x"], more["Other"][0]);
        }

        WriteWorkbook(book, ("Items", [["id"], ["9"]]), ("Empty", []), ("Other", [["x"]]));
        using var third = Store();
        var again = SpreadsheetWorkbook.Load(WorkbookFile.FromStore(third, book), 1 << 20, "max_bytes", ["Items"]);
        Assert.Equal(["9"], again["Items"][1]);
    }

    /// <summary>The caller's own size limit is judged before the kept worksheets are looked at, on every start.</summary>
    [Fact]
    public void AKeptWorksheetDoesNotWidenTheCallersSizeLimit()
    {
        const string book = "Config/Book.xml";
        WriteWorkbook(book, ("Items", [["id"], ["1"]]));
        using (var first = Store())
            SpreadsheetWorkbook.Load(WorkbookFile.FromStore(first, book), 1 << 20, "max_bytes", ["Items"]);
        using var second = Store();
        var error = Assert.Throws<InvalidOperationException>(() =>
            SpreadsheetWorkbook.Load(WorkbookFile.FromStore(second, book), 16, "max_bytes", ["Items"]));
        Assert.Contains("max_bytes is 16", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A worksheet section that names a text it does not have, lacks a part, is cut short or states a length its
    /// bytes cannot hold is refused instead of being returned or allocated for.
    /// </summary>
    [Fact]
    public void ADamagedWorksheetSectionIsRefused()
    {
        var catalog = PackedWorksheet.Catalog("Config/Book.xml", "Items");
        byte[] Section(PackedWorksheet sheet) => MemoryPack.MemoryPackSerializer.Serialize(sheet);
        Assert.Throws<InvalidDataException>(() => catalog.Read(new MemoryStream(Section(new(["a"], [[0, 1]])))));
        Assert.Throws<InvalidDataException>(() => catalog.Read(new MemoryStream(Section(new(["a"], [[-1]])))));
        Assert.Throws<InvalidDataException>(() => catalog.Read(new MemoryStream(Section(new(null!, [[0]])))));
        Assert.Throws<InvalidDataException>(() => catalog.Read(new MemoryStream(Section(new(["a", null!], [[0]])))));
        Assert.Throws<InvalidDataException>(() => catalog.Read(new MemoryStream(Section(new(["a"], [null!])))));
        Assert.Throws<InvalidDataException>(() => catalog.Read(new MemoryStream(Section(null!))));

        var good = Section(new(["a", "b"], [[0, 1], [1]]));
        Assert.Equal([["a", "b"], ["b"]], catalog.Read(new MemoryStream(good)).Select(row => row.ToArray()));
        Assert.ThrowsAny<Exception>(() => catalog.Read(new MemoryStream(good[..^2])));
        // A text count of two billion in a section of a few bytes.
        var huge = good.ToArray();
        BitConverter.TryWriteBytes(huge.AsSpan(1), int.MaxValue);
        Assert.ThrowsAny<Exception>(() => catalog.Read(new MemoryStream(huge)));
    }

    private void WriteWorkbook(string path, params (string Name, string[][] Rows)[] sheets)
    {
        var text = new StringBuilder("<?xml version=\"1.0\"?><Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\" "
            + "xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\">");
        foreach (var (name, rows) in sheets)
        {
            text.Append("<Worksheet ss:Name=\"").Append(name).Append("\"><Table>");
            foreach (var row in rows)
            {
                text.Append("<Row>");
                foreach (var cell in row)
                    text.Append("<Cell><Data ss:Type=\"String\">").Append(System.Security.SecurityElement.Escape(cell)).Append("</Data></Cell>");
                text.Append("</Row>");
            }
            text.Append("</Table></Worksheet>");
        }
        WriteLoose(path, text.Append("</Workbook>").ToString());
    }

    private string CompiledPath => Path.Combine(_root, "state", "kr.bffdata");

    private ClientDataStore Store() =>
        ClientDataStore.Open(Path.Combine(_root, "data"), Path.Combine(_root, "dataraw"), new ListLogger(_log), CompiledPath);

    private string[] Load(ClientDataStore store, string inputs = "", bool upper = false, LinesCatalog? catalog = null) =>
        store.Load(catalog ?? new LinesCatalog(), inputs, data =>
        {
            _compiles++;
            var lines = Encoding.UTF8.GetString(data.Read(Member)).Split('\n');
            return upper ? lines.Select(line => line.ToUpperInvariant()).ToArray() : lines;
        });

    private void WriteArchive(params string[] lines)
    {
        var file = Path.Combine(_root, "data", "Config", "Config.bfz");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (var stream = File.Create(file))
            BfzArchive.Write(stream, "Config/Config.bfz", [(Member, Encoding.UTF8.GetBytes(string.Join('\n', lines)))]);
        // File times can be coarser than a test; a rewritten archive is always later than the one before.
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(Interlocked.Increment(ref _clock)));
    }

    private void WriteLoose(string path, string text)
    {
        var file = Path.Combine(_root, "dataraw", path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(Interlocked.Increment(ref _clock)));
    }

    private static int _clock;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class LinesCatalog : ICompiledCatalog<string[]>
    {
        public bool DropLastOnRead { get; init; }
        public bool StopEarly { get; init; }
        public int Reads { get; private set; }

        public string Name => "lines";

        public void Write(string[] value, Stream output)
        {
            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            writer.Write(value.Length);
            foreach (var line in value) writer.Write(line);
        }

        public string[] Read(Stream input)
        {
            Reads++;
            using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
            var lines = new string[reader.ReadInt32()];
            for (var index = 0; index < lines.Length - (StopEarly ? 1 : 0); index++) lines[index] = reader.ReadString();
            return DropLastOnRead ? lines[..^1] : lines;
        }
    }

    private sealed class BareStore : IClientDataStore
    {
        public bool Exists(string path) => false;

        public byte[] Read(string path) => throw new FileNotFoundException(path);
    }

    /// <summary>A store's files without its shared parse, so a load through it parses the workbook itself.</summary>
    private sealed class BareStoreOver(IClientDataStore inner) : IClientDataStore
    {
        public bool Exists(string path) => inner.Exists(path);

        public byte[] Read(string path) => inner.Read(path);

        public long Length(string path) => inner.Length(path);

        public Stream OpenRead(string path) => inner.OpenRead(path);
    }

    private sealed class ListLogger(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (lines) lines.Add(logLevel + ": " + formatter(state, exception));
        }
    }
}
