using System.Xml.Linq;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.InGame.Data;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks native spreadsheet blank tails without weakening reward identity or allocation bounds.</summary>
public sealed class LotteryCatalogBoundsTests
{
    private static readonly XNamespace Spreadsheet = "urn:schemas-microsoft-com:office:spreadsheet";

    /// <summary>Empty native trailing cells do not prevent resolving a configured stock reward.</summary>
    [Fact]
    public void NativeBlankTailRetainsStockIdentityValidation()
    {
        var document = Workbook();
        var row = document.Descendants(Spreadsheet + "Row").Last();
        row.Add(new XElement(Spreadsheet + "Cell", new XAttribute(Spreadsheet + "Index", 256)));
        row.Add(new XElement(Spreadsheet + "Cell"));
        row.Add(new XElement(Spreadsheet + "Cell", new XAttribute(Spreadsheet + "Index", 16384)));
        LotteryCatalog.Validate(Bytes(document), [Prize()]);
        Assert.Throws<ConfigException>(() => LotteryCatalog.Validate(Bytes(document),
            [new LotteryItem { ItemId = 100208, DisplayId = 1000920, Days = 3 }]));
    }

    /// <summary>Nonempty unsupported columns, backwards indices and columns past the native limit fail validation.</summary>
    [Theory]
    [InlineData(257, "unexpected")]
    [InlineData(16385, "")]
    [InlineData(4, "")]
    public void InvalidTailIsRejected(int index, string value)
    {
        var document = Workbook();
        document.Descendants(Spreadsheet + "Row").Last().Add(new XElement(Spreadsheet + "Cell",
            new XAttribute(Spreadsheet + "Index", index), new XElement(Spreadsheet + "Data", value)));
        Assert.Throws<ConfigException>(() => LotteryCatalog.Validate(Bytes(document), [Prize()]));
    }

    /// <summary>Every source accepts the inclusive limit and refuses the same workbook one byte above it.</summary>
    [Fact]
    public void ConfiguredByteLimitAppliesToEveryWorkbookSource()
    {
        var bytes = Bytes(Workbook());
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, bytes);
            var store = new BoundedStore(bytes);
            LotteryCatalog.Validate(bytes, [Prize()], bytes.Length);
            LotteryCatalog.Validate(path, [Prize()], bytes.Length);
            LotteryCatalog.Validate(store, "Config/ShopItem2.xml", [Prize()], bytes.Length);
            Assert.Equal(bytes.Length, store.OpenedWithLimit);
            Assert.Throws<ConfigException>(() => LotteryCatalog.Validate(bytes, [Prize()], bytes.Length - 1));
            Assert.Throws<ConfigException>(() => LotteryCatalog.Validate(path, [Prize()], bytes.Length - 1));
            Assert.Throws<ConfigException>(() => LotteryCatalog.Validate(store, "Config/ShopItem2.xml", [Prize()], bytes.Length - 1));
            Assert.Equal(1, store.Opens);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Zero and negative parser limits cannot disable the XML or resource bounds.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidByteLimitIsRejected(int maximumBytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LotteryCatalog.Validate(Bytes(Workbook()), [Prize()], maximumBytes));

    private sealed class BoundedStore(byte[] bytes) : BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore
    {
        internal long OpenedWithLimit { get; private set; }
        internal int Opens { get; private set; }
        public bool Exists(string path) => true;
        public long Length(string path) => bytes.Length;
        public byte[] Read(string path) => throw new InvalidOperationException("The workbook must be streamed.");
        public Stream OpenRead(string path) => throw new InvalidOperationException("The stream must have a byte limit.");
        public Stream OpenRead(string path, long maximumBytes)
        {
            OpenedWithLimit = maximumBytes;
            Opens++;
            return new MemoryStream(bytes, writable: false);
        }
    }

    private static LotteryItem Prize() => new() { ItemId = 100207, DisplayId = 1000920, Days = 3 };

    private static XDocument Workbook() => new(new XElement(Spreadsheet + "Workbook",
        new XElement(Spreadsheet + "Worksheet", new XAttribute(Spreadsheet + "Name", "Stock"),
            new XElement(Spreadsheet + "Table", Row("stockId", "itemId", "duration", "type"),
                Row("1000920", "100207", "3", "1")))));

    private static XElement Row(params string[] values) => new(Spreadsheet + "Row",
        values.Select(value => new XElement(Spreadsheet + "Cell", new XElement(Spreadsheet + "Data", value))));

    private static byte[] Bytes(XDocument document) => System.Text.Encoding.UTF8.GetBytes(document.ToString());
}
