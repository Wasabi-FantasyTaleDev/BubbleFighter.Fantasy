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
        LotteryCatalog.Validate(document, [Prize()]);
        Assert.Throws<ConfigException>(() => LotteryCatalog.Validate(document,
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
        Assert.Throws<ConfigException>(() => LotteryCatalog.Validate(document, [Prize()]));
    }

    private static LotteryItem Prize() => new() { ItemId = 100207, DisplayId = 1000920, Days = 3 };

    private static XDocument Workbook() => new(new XElement(Spreadsheet + "Workbook",
        new XElement(Spreadsheet + "Worksheet", new XAttribute(Spreadsheet + "Name", "Stock"),
            new XElement(Spreadsheet + "Table", Row("stockId", "itemId", "duration", "type"),
                Row("1000920", "100207", "3", "1")))));

    private static XElement Row(params string[] values) => new(Spreadsheet + "Row",
        values.Select(value => new XElement(Spreadsheet + "Cell", new XElement(Spreadsheet + "Data", value))));
}
