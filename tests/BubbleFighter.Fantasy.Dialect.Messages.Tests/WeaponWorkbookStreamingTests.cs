using System.Text;
using System.Xml;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Dialect.Messages;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// The streamed weapon workbook reader returns exactly what the document-tree reader it replaced returned, on the
/// shapes the client's workbooks use: sparse indexes, merged cells, rich text, whitespace runs and empty cells.
/// </summary>
public sealed class WeaponWorkbookStreamingTests
{
    private const string Ss = "urn:schemas-microsoft-com:office:spreadsheet";

    private static byte[] Workbook(string sheets) => Encoding.UTF8.GetBytes(
        $"<?xml version=\"1.0\"?><Workbook xmlns=\"{Ss}\" xmlns:ss=\"{Ss}\" xmlns:html=\"http://www.w3.org/TR/REC-html40\">{sheets}</Workbook>");

    private const string Tricky = """
        <Worksheet ss:Name="Ignored"><Table><Row><Cell><Data ss:Type="String">x</Data></Cell></Row></Table></Worksheet>
        <Worksheet ss:Name="Pistol">
          <Table>
            <Row><Cell><Data ss:Type="String">id</Data></Cell><Cell><Data ss:Type="Number"> 7 </Data></Cell></Row>
            <Row>
              <Cell ss:MergeAcross="1"><Data ss:Type="String">merged</Data></Cell>
              <Cell ss:Index="5"><ss:Data ss:Type="String"><html:B>ri</html:B> <html:Font>ch</html:Font></ss:Data></Cell>
              <Cell><Data ss:Type="String"><![CDATA[ cdata ]]></Data></Cell>
              <Cell/>
              <Cell><Comment><ss:Data>note</ss:Data></Comment><Data ss:Type="String">after note</Data></Cell>
            </Row>
            <Row/>
            <Row><Cell ss:Index="3"><Data ss:Type="String">  spaced  value  </Data></Cell><Cell><Data/></Cell></Row>
          </Table>
        </Worksheet>
        <Worksheet ss:Name="Rifle"/>
        """;

    [Fact]
    public void StreamedRowsMatchTheDocumentReader()
    {
        var bytes = Workbook(Tricky);
        string[] wanted = ["Pistol", "Rifle"];

        var expected = DocumentReader(bytes, wanted);
        var actual = WeaponCriticalCatalog.ReadWorkbook(bytes, "test.xml", wanted, 100, 64, bytes.Length);

        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (name, rows) in expected)
        {
            Assert.Equal(rows.Count, actual[name].Count);
            for (var i = 0; i < rows.Count; i++)
                Assert.Equal(rows[i].OrderBy(pair => pair.Key), actual[name][i].OrderBy(pair => pair.Key));
        }
        Assert.Equal("after note", actual["Pistol"][1][7]);
    }

    [Theory]
    [InlineData("<Worksheet ss:Name=\"Pistol\"><Table><Row><Cell ss:Index=\"2\"/><Cell ss:Index=\"2\"/></Row></Table></Worksheet>", "overlapping")]
    [InlineData("<Worksheet ss:Name=\"Pistol\"><Table/></Worksheet><Worksheet ss:Name=\"Pistol\"/>", "repeats worksheet")]
    [InlineData("<Worksheet ss:Name=\"Rifle\"/>", "lacks worksheet 'Pistol'")]
    public void MalformedWorkbooksFailAsBefore(string sheets, string message)
    {
        var bytes = Workbook(sheets);
        var error = Assert.Throws<ConfigException>(() => WeaponCriticalCatalog.ReadWorkbook(bytes, "test.xml", ["Pistol"], 100, 64, bytes.Length));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void RecordAndColumnBoundsStillApply()
    {
        var rows = Workbook("<Worksheet ss:Name=\"Pistol\"><Table><Row/><Row/><Row/></Table></Worksheet>");
        Assert.Contains("weapon_catalog_max_records",
            Assert.Throws<ConfigException>(() => WeaponCriticalCatalog.ReadWorkbook(rows, "t", ["Pistol"], 2, 64, rows.Length)).Message);
        var wide = Workbook("<Worksheet ss:Name=\"Pistol\"><Table><Row><Cell ss:Index=\"9\"/></Row></Table></Worksheet>");
        Assert.Contains("out-of-bound",
            Assert.Throws<ConfigException>(() => WeaponCriticalCatalog.ReadWorkbook(wide, "t", ["Pistol"], 2, 8, wide.Length)).Message);
    }

    // The document-tree reader this replaced, kept as the reference for equivalence.
    private static Dictionary<string, List<Dictionary<int, string>>> DocumentReader(byte[] bytes, string[] wanted)
    {
        XNamespace ss = Ss;
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        var result = new Dictionary<string, List<Dictionary<int, string>>>(StringComparer.Ordinal);
        foreach (var sheet in document.Descendants(ss + "Worksheet"))
        {
            var name = (string?)sheet.Attribute(ss + "Name");
            if (name is null || !wanted.Contains(name, StringComparer.Ordinal)) continue;
            var rows = new List<Dictionary<int, string>>();
            foreach (var row in sheet.Elements(ss + "Table").Elements(ss + "Row"))
            {
                var cells = new Dictionary<int, string>();
                var next = 0;
                foreach (var cell in row.Elements(ss + "Cell"))
                {
                    var index = cell.Attribute(ss + "Index") is { } position ? int.Parse(position.Value) - 1 : next;
                    var merged = cell.Attribute(ss + "MergeAcross") is { } span ? int.Parse(span.Value) : 0;
                    cells.Add(index, cell.Element(ss + "Data")?.Value.Trim() ?? "");
                    next = index + merged + 1;
                }
                rows.Add(cells);
            }
            result.Add(name, rows);
        }
        return result;
    }
}
