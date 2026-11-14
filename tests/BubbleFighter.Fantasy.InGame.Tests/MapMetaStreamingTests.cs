using System.Text;
using System.Xml.Linq;
using BubbleFighter.Fantasy.InGame.Data;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>The streamed map meta reader returns the rows the document-tree reader it replaced returned.</summary>
public sealed class MapMetaStreamingTests
{
    private const string Ss = "urn:schemas-microsoft-com:office:spreadsheet";

    private static byte[] Workbook(string sheets) => Encoding.UTF8.GetBytes(
        $"<?xml version=\"1.0\"?><Workbook xmlns=\"{Ss}\" xmlns:ss=\"{Ss}\" xmlns:html=\"http://www.w3.org/TR/REC-html40\">{sheets}</Workbook>");

    [Fact]
    public void StreamedRowsMatchTheDocumentReader()
    {
        var bytes = Workbook("""
            <Worksheet ss:Name="MapMeta">
              <Table>
                <Row><Cell><Data ss:Type="String">[Field]</Data></Cell><Cell/><Cell><Data ss:Type="String"> map_a </Data></Cell>
                  <Cell ss:Index="5"><Data ss:Type="String">map_b</Data></Cell></Row>
                <Row/>
                <Row><Cell><Data ss:Type="String">isRelease</Data></Cell><Cell ss:Index="3"><ss:Data ss:Type="String"><html:B>1</html:B> <html:I>0</html:I></ss:Data></Cell>
                  <Cell><Comment><ss:Data>note</ss:Data></Comment><Data ss:Type="Number">1</Data></Cell><Cell><Data/></Cell></Row>
              </Table>
            </Worksheet>
            <Worksheet ss:Name="Other"><Table><Row><Cell><Data ss:Type="String">ignored</Data></Cell></Row></Table></Worksheet>
            """);

        var expected = DocumentRows(bytes);
        var actual = MapMetaTable.Rows(bytes);

        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.Equal(expected[i], actual[i]);
        Assert.Equal("map_b", MapMetaTable.Parse(bytes).Maps.Last());
    }

    [Fact]
    public void AWorkbookWithoutASheetStillFails() =>
        Assert.Throws<InvalidOperationException>(() => MapMetaTable.Rows(Workbook("")));

    // The document-tree reader this replaced, kept as the reference for equivalence.
    private static List<List<string>> DocumentRows(byte[] bytes)
    {
        XNamespace ss = Ss;
        var document = XDocument.Load(new MemoryStream(bytes));
        return document.Descendants(ss + "Worksheet").First().Descendants(ss + "Row").Select(row =>
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements(ss + "Cell"))
            {
                if (int.TryParse((string?)cell.Attribute(ss + "Index"), out var index))
                    while (cells.Count < index - 1) cells.Add(string.Empty);
                cells.Add(cell.Element(ss + "Data")?.Value.Trim() ?? string.Empty);
            }
            return cells;
        }).ToList();
    }
}
