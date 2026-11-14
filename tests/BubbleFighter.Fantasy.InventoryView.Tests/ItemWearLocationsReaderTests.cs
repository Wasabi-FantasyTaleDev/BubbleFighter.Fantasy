using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Xunit;

namespace BubbleFighter.Fantasy.InventoryView.Tests;

/// <summary>
/// Pins the wear groups the ShopItem worksheet produces and every refusal, so the reader's parsing strategy can change
/// without changing a group, its order or an error.
/// </summary>
public sealed class ItemWearLocationsReaderTests
{
    private const string Ss = "urn:schemas-microsoft-com:office:spreadsheet";
    private const string Header = "itemId,mainCategory,baseLocation,slotLocation,partLocation,secPartLocation";

    /// <summary>Clothing joins base, part and secondary part; accessories use the slot, else the base; groups keep first-seen order.</summary>
    [Fact]
    public void GroupsFollowLocationsInFirstSeenOrder()
    {
        var groups = Load(Shop(Header,
            "10,Clothes,Head,,Hat Hat,",
            "11,Clothes,Body,,,Head",
            "12,Accessories,Neck,Ring,,",
            "13,Accessories,Neck,,,",
            "14,Other,bad,,,",
            "-1,Other,ignored,,,"));
        // Clothes:Head, Clothes:Hat, Clothes:Body, Accessories:Ring, Accessories:Neck.
        Assert.Equal([[10, 11], [10], [11], [12], [13]], groups.Select(group => group.ItemIds!.ToArray()).ToArray());
        Assert.Equal((10, 11, 10), (groups[0].FirstItemId, groups[0].LastItemId, groups[0].LegacyDefaultItemId));
    }

    /// <summary>Sparse cells pad to their columns, empty tails past column 256 are ignored, and rich text keeps its spaces.</summary>
    [Fact]
    public void SparseCellsRichTextAndEmptyTails()
    {
        var row = "<Row><Cell><Data>20</Data></Cell><Cell><Data>Clothes</Data></Cell>"
            + "<Cell><Data><B>Head</B> <I>Foot</I></Data></Cell><Cell ss:Index=\"5\"><Comment><ss:Data>Note</ss:Data></Comment><Data>Arm</Data></Cell>"
            + "<Cell ss:Index=\"257\"/><Cell ss:Index=\"16384\"><Data></Data></Cell></Row>";
        var groups = Load(Workbook(Sheet("ShopItem", HeaderRow(Header) + row)));
        Assert.Equal(3, groups.Length);
        Assert.All(groups, group => Assert.Equal([20], group.ItemIds));
    }

    /// <summary>Only the first Table of the single ShopItem worksheet is read; other worksheets are ignored.</summary>
    [Fact]
    public void OtherWorksheetsAreIgnored()
    {
        var groups = Load(Workbook(Sheet("Other", HeaderRow(Header) + DataRow("30,Clothes,Leg,,,"))
            + Sheet("ShopItem", HeaderRow(Header) + DataRow("31,Clothes,Arm,,,"))));
        Assert.Equal([31], Assert.Single(groups).ItemIds);
    }

    /// <summary>Every configuration refusal keeps its message.</summary>
    [Theory]
    [InlineData("<Worksheet ss:Name=\"Other\"><Table/></Worksheet>", "requires a ShopItem worksheet")]
    [InlineData("<Worksheet ss:Name=\"ShopItem\"></Worksheet>", "requires a ShopItem table")]
    [InlineData("<Worksheet ss:Name=\"ShopItem\"><Table/></Worksheet>", "bounded header and item rows")]
    [InlineData("<Worksheet ss:Name=\"ShopItem\"><Table>{header-without-secPart}</Table></Worksheet>", "missing secPartLocation")]
    [InlineData("<Worksheet ss:Name=\"ShopItem\"><Table>{header}{row:x,Clothes,Head,,,}</Table></Worksheet>", "identity must be positive")]
    [InlineData("<Worksheet ss:Name=\"ShopItem\"><Table>{header}{row:5,Other,Head,,,}</Table></Worksheet>", "no wearable positions")]
    [InlineData("<Worksheet ss:Name=\"ShopItem\"><Table>{header}<Row><Cell ss:Index=\"257\"><Data>x</Data></Cell></Row></Table></Worksheet>", "exceeds the column bound")]
    [InlineData("<Worksheet ss:Name=\"ShopItem\"><Table>{header}<Row><Cell ss:Index=\"16385\"/></Row></Table></Worksheet>", "cell index is invalid")]
    [InlineData("<Worksheet ss:Name=\"ShopItem\"><Table>{header}<Row><Cell ss:Index=\"3\"/><Cell ss:Index=\"2\"/></Row></Table></Worksheet>", "cell index is invalid")]
    public void RefusalsKeepTheirMessages(string sheets, string message)
    {
        sheets = sheets.Replace("{header}", HeaderRow(Header), StringComparison.Ordinal)
            .Replace("{header-without-secPart}", HeaderRow(Header.Replace(",secPartLocation", "", StringComparison.Ordinal)), StringComparison.Ordinal);
        var start = sheets.IndexOf("{row:", StringComparison.Ordinal);
        if (start >= 0)
        {
            var end = sheets.IndexOf('}', start);
            sheets = sheets[..start] + DataRow(sheets[(start + 5)..end]) + sheets[(end + 1)..];
        }
        var error = Assert.Throws<ConfigException>(() => Load(Workbook(sheets)));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    /// <summary>More than 59 locations exceed the equipment position bound.</summary>
    [Fact]
    public void TooManyLocationsAreRefused()
    {
        var rows = Enumerable.Range(1, 60).Select(index => $"{index},Clothes,P{index},,,").ToArray();
        var error = Assert.Throws<ConfigException>(() => Load(Shop(Header, rows)));
        Assert.Contains("equipment position bound", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A repeated ShopItem worksheet, a non-numeric index and malformed XML keep their exception types.</summary>
    [Fact]
    public void StructuralFailuresKeepTheirTypes()
    {
        var sheet = Sheet("ShopItem", HeaderRow(Header) + DataRow("1,Clothes,Head,,,"));
        Assert.Throws<InvalidOperationException>(() => Load(Workbook(sheet + sheet)));
        Assert.Throws<FormatException>(() => Load(Workbook(Sheet("ShopItem", HeaderRow(Header) + "<Row><Cell ss:Index=\"x\"/></Row>"))));
        Assert.Throws<System.Xml.XmlException>(() => Load(Workbook(Sheet("ShopItem", HeaderRow(Header) + "<Row><Cell></Row>"))));
        Assert.Throws<System.Xml.XmlException>(() => Load(Workbook(sheet).Replace("<Workbook", "<!DOCTYPE Workbook []><Workbook", StringComparison.Ordinal)));
    }

    /// <summary>The row bound is judged before a row's defect, so rows after a defect still count toward it.</summary>
    [Fact]
    public void RowsAfterADefectStillCountTowardTheBound()
    {
        var rows = HeaderRow(Header) + DataRow("x,Clothes,Head,,,") + string.Concat(Enumerable.Repeat("<Row/>", 65535));
        var error = Assert.Throws<ConfigException>(() => Load(Workbook(Sheet("ShopItem", rows))));
        Assert.Contains("bounded header and item rows", error.Message, StringComparison.Ordinal);
        var identity = Assert.Throws<ConfigException>(() => Load(Workbook(Sheet("ShopItem", rows[..^"<Row/>".Length]))));
        Assert.Contains("identity must be positive", identity.Message, StringComparison.Ordinal);
    }

    /// <summary>A defect after the ShopItem worksheet still fails the load: the whole document is read.</summary>
    [Fact]
    public void TrailingMalformedXmlFails()
    {
        var xml = Workbook(Sheet("ShopItem", HeaderRow(Header) + DataRow("1,Clothes,Head,,,")) + "<Worksheet ss:Name=\"Late\"><Table>");
        Assert.Throws<System.Xml.XmlException>(() => Load(xml));
    }

    private static InventoryWearGroup[] Load(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml), writable: false);
        return ItemWearLocations.Load(stream, "Clothes", "Accessories");
    }

    private static string Shop(string header, params string[] rows) =>
        Workbook(Sheet("ShopItem", HeaderRow(header) + string.Concat(rows.Select(DataRow))));

    private static string HeaderRow(string header) => DataRow(header);

    private static string DataRow(string values) =>
        "<Row>" + string.Concat(values.Split(',').Select(value => $"<Cell><Data>{value}</Data></Cell>")) + "</Row>";

    private static string Sheet(string name, string rows) => $"<Worksheet ss:Name=\"{name}\"><Table>{rows}</Table></Worksheet>";

    private static string Workbook(string sheets) =>
        $"<?xml version=\"1.0\"?><Workbook xmlns=\"{Ss}\" xmlns:ss=\"{Ss}\">{sheets}</Workbook>";
}
