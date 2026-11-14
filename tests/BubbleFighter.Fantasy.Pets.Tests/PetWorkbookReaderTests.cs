using System.Text;
using BubbleFighter.Fantasy.Pets.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Pets.Tests;

/// <summary>
/// Pins what the pet worksheet reader returns and refuses for the SpreadsheetML shapes the native workbooks use, so
/// the reader's parsing strategy can change without changing a row, a cell or an error.
/// </summary>
public sealed class PetWorkbookReaderTests
{
    private const string Ss = "urn:schemas-microsoft-com:office:spreadsheet";

    /// <summary>Sparse indexes pad with empty cells, empty styled tails are dropped, and unselected sheets are skipped.</summary>
    [Fact]
    public void SparseCellsPadAndEmptyTailsDrop()
    {
        var rows = Read(Sheet("ConstValue",
            "<Row><Cell><Data>a</Data></Cell><Cell ss:Index=\"4\"><Data>d</Data></Cell><Cell/><Cell ss:Index=\"16384\"/></Row>",
            "<Row/>",
            "<Row><Cell ss:Index=\"2\"><Data>b</Data></Cell></Row>") + Sheet("Other", "<Row><Cell><Data>x</Data></Cell></Row>"));
        Assert.Equal(["a", "", "", "d"], rows[0]);
        Assert.Empty(rows[1]);
        Assert.Equal(["", "b"], rows[2]);
    }

    /// <summary>A value is the concatenated text of its direct Data child; a note's nested Data is not a second value.</summary>
    [Fact]
    public void RichTextConcatenatesAndNotesAreNotValues()
    {
        var rows = Read(Sheet("ConstValue",
            "<Row><Cell><Data><B>ab</B> <Font>c</Font></Data></Cell>"
            + "<Cell><Comment><ss:Data><B>note</B></ss:Data></Comment><Data>v</Data></Cell>"
            + "<Cell><Data>  </Data></Cell><Cell><Data> x </Data></Cell><Cell><Data>&amp;<![CDATA[<y>]]></Data></Cell></Row>"));
        Assert.Equal(["abc", "v", "", " x ", "&<y>"], rows[0]);
    }

    /// <summary>
    /// A row is any spreadsheet Row two levels below its worksheet, whatever its parent; deeper rows are not rows of
    /// that worksheet.
    /// </summary>
    [Fact]
    public void RowsAreTakenTwoLevelsBelowTheWorksheet()
    {
        var xml = Workbook($"<Worksheet ss:Name=\"ConstValue\"><Table><Row><Cell><Data>a</Data></Cell></Row></Table>"
            + "<WorksheetOptions><Row><Cell><Data>z</Data></Cell></Row><Panes><Row><Cell><Data>deep</Data></Cell></Row></Panes></WorksheetOptions></Worksheet>");
        var rows = Read(xml, maxRows: 2);
        Assert.Equal([["a"], ["z"]], rows.Select(row => row.ToArray()).ToArray());
    }

    /// <summary>Every refusal keeps its message.</summary>
    [Theory]
    [InlineData("<Row><Cell><Data>a</Data><Data>b</Data></Cell></Row>", "more than one value in a cell")]
    [InlineData("<Row><Cell ss:Index=\"2\"/><Cell ss:Index=\"2\"/></Row>", "invalid sparse cell index")]
    [InlineData("<Row><Cell ss:Index=\"x\"/></Row>", "invalid sparse cell index")]
    [InlineData("<Row><Cell ss:Index=\"-1\"/></Row>", "invalid sparse cell index")]
    [InlineData("<Row><Cell ss:Index=\"2147483647\"/><Cell/></Row>", "cell index outside i32")]
    [InlineData("<Row><Cell ss:Index=\"9\"><Data>a</Data></Cell></Row>", "exceeds max_catalog_columns")]
    [InlineData("<Row><Cell><Data>abcdefghijk</Data></Cell></Row>", "exceeds max_cell_characters")]
    [InlineData("<Row/><Row/><Row/><Row/>", "exceeds max_catalog_rows")]
    public void RefusalsKeepTheirMessages(string rows, string message)
    {
        var error = Assert.Throws<InvalidDataException>(() => Read(Sheet("ConstValue", rows), maxRows: 3, maxColumns: 8, maxCellCharacters: 10));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    /// <summary>Rows of all selected sheets share one row budget, while rows of unselected sheets cost nothing.</summary>
    [Fact]
    public void RowBudgetCoversSelectedSheetsOnly()
    {
        var xml = Workbook(SheetBody("A", "<Row/><Row/>") + SheetBody("B", "<Row/><Row/>") + SheetBody("C", "<Row/><Row/><Row/>"));
        Assert.Equal(2, PetWorkbook.Read(Bytes(xml), "test", 4, 8, 10, ["A", "B"])["B"].Count);
        Assert.Throws<InvalidDataException>(() => PetWorkbook.Read(Bytes(xml), "test", 3, 8, 10, ["A", "B"]));
    }

    /// <summary>A repeated or missing selected worksheet is refused.</summary>
    [Fact]
    public void RepeatedAndMissingSheetsAreRefused()
    {
        var repeated = Assert.Throws<InvalidDataException>(() => Read(Sheet("ConstValue", "<Row/>") + Sheet("ConstValue", "<Row/>")));
        Assert.Contains("repeats worksheet 'ConstValue'", repeated.Message, StringComparison.Ordinal);
        var missing = Assert.Throws<InvalidDataException>(() => Read(Sheet("Other", "<Row/>")));
        Assert.Contains("missing worksheet 'ConstValue'", missing.Message, StringComparison.Ordinal);
    }

    /// <summary>Malformed XML and a document type declaration fail as XML errors.</summary>
    [Theory]
    [InlineData("<Row><Cell><Data>a</Cell></Row>")]
    [InlineData("<!DOCTYPE x []>")]
    public void MalformedXmlFails(string fragment)
    {
        var xml = fragment.StartsWith("<!DOCTYPE", StringComparison.Ordinal)
            ? Workbook(SheetBody("ConstValue", "<Row/>")).Replace("<Workbook", fragment + "<Workbook", StringComparison.Ordinal)
            : Workbook(SheetBody("ConstValue", fragment));
        Assert.Throws<System.Xml.XmlException>(() => Read(xml));
    }

    private static IReadOnlyList<IReadOnlyList<string>> Read(string sheetsOrWorkbook, int maxRows = 100, int maxColumns = 16384,
        int maxCellCharacters = 1000)
    {
        var xml = sheetsOrWorkbook.StartsWith("<?xml", StringComparison.Ordinal) ? sheetsOrWorkbook : Workbook(sheetsOrWorkbook);
        return PetWorkbook.Read(Bytes(xml), "test", maxRows, maxColumns, maxCellCharacters, ["ConstValue"])["ConstValue"];
    }

    private static byte[] Bytes(string xml) => Encoding.UTF8.GetBytes(xml);

    private static string Sheet(string name, params string[] rows) => SheetBody(name, string.Concat(rows));

    private static string SheetBody(string name, string rows) => $"<Worksheet ss:Name=\"{name}\"><Table>{rows}</Table></Worksheet>";

    private static string Workbook(string sheets) =>
        $"<?xml version=\"1.0\"?><Workbook xmlns=\"{Ss}\" xmlns:ss=\"{Ss}\">{sheets}</Workbook>";
}
