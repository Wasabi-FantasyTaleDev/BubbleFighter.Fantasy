using System.Text;
using BubbleFighter.Fantasy.Contracts.ClientData;
using Xunit;

namespace BubbleFighter.Fantasy.ClientData.Tests;

/// <summary>
/// The shared workbook reader refuses what it cannot place: a sparse index past the last SpreadsheetML column (before
/// any padding is allocated), an index that moves backward over filled columns, and a requested sheet given twice.
/// </summary>
public sealed class SpreadsheetWorkbookBoundsTests
{
    private const string Ss = "urn:schemas-microsoft-com:office:spreadsheet";

    private static WorkbookFile Workbook(string sheets) => WorkbookFile.FromBytes("Config/Test.xml", Encoding.UTF8.GetBytes(
        $"<?xml version=\"1.0\"?><Workbook xmlns=\"{Ss}\" xmlns:ss=\"{Ss}\">{sheets}</Workbook>"));

    private static IReadOnlyList<IReadOnlyList<string>> Rows(string sheets) =>
        SpreadsheetWorkbook.Load(Workbook(sheets), 1 << 20, "max_test_bytes", ["Sheet"])["Sheet"];

    [Fact]
    public void ForwardSparseIndexesStillPadTheirColumns()
    {
        var rows = Rows("<Worksheet ss:Name=\"Sheet\"><Table><Row><Cell><Data>a</Data></Cell><Cell ss:Index=\"4\"><Data>d</Data></Cell>"
            + "<Cell ss:Index=\"16384\"><Data>last</Data></Cell></Row></Table></Worksheet>");

        var row = Assert.Single(rows);
        Assert.Equal(["a", "", "", "d"], row.Take(4));
        Assert.Equal(16384, row.Count);
        Assert.Equal("last", row[^1]);
    }

    [Theory]
    [InlineData("<Cell ss:Index=\"16385\"><Data>x</Data></Cell>")]
    [InlineData("<Cell ss:Index=\"2147483647\"><Data>x</Data></Cell>")]
    [InlineData("<Cell><Data>a</Data></Cell><Cell><Data>b</Data></Cell><Cell ss:Index=\"1\"><Data>x</Data></Cell>")]
    [InlineData("<Cell ss:Index=\"0\"><Data>x</Data></Cell>")]
    [InlineData("<Cell ss:Index=\"two\"><Data>x</Data></Cell>")]
    public void UnplaceableCellIndexesAreRefused(string cells)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Rows($"<Worksheet ss:Name=\"Sheet\"><Table><Row>{cells}</Row></Table></Worksheet>"));
        Assert.Contains("cell index", error.Message);
    }

    [Fact]
    public void ARequestedSheetGivenTwiceIsRefused()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Rows(
            "<Worksheet ss:Name=\"Sheet\"><Table/></Worksheet><Worksheet ss:Name=\"Sheet\"><Table/></Worksheet>"));
        Assert.Contains("repeats worksheet 'Sheet'", error.Message);
    }

    [Fact]
    public void AnUnrequestedSheetMayRepeat()
    {
        var rows = Rows("<Worksheet ss:Name=\"Other\"/><Worksheet ss:Name=\"Other\"/><Worksheet ss:Name=\"Sheet\"><Table><Row/></Table></Worksheet>");
        Assert.Single(rows);
    }
}
