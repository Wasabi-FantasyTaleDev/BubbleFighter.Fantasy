namespace BubbleFighter.Fantasy.Tests;

/// <summary>
/// Synthetic shop workbooks in the client's table format, written once per test process to a temporary
/// directory. The ids, names and prices are invented for the tests; real client tables are read from a
/// local client-data tree by the tests marked <c>LocalClientData</c>.
/// </summary>
internal static class SyntheticClientTables
{
    private static readonly Lazy<string> Directory = new(() => TestTempDirectories.ForProcess("bff-synthetic-tables"));

    /// <summary>Gets a shop workbook with item, cash, set and off-sale rows.</summary>
    public static string ShopItem2 => Write("ShopItem2.xml", ShopItem2Xml);

    /// <summary>
    /// Gets the shop workbook without the ShopItem purchase-term columns (durationType, maxDuration, buyAgain,
    /// function), for legacy tests that state their own terms in those columns.
    /// </summary>
    public static string ShopItem2WithoutTerms => Write("ShopItem2-without-terms.xml", WithoutTerms(ShopItem2Xml));

    /// <summary>Gets the synthetic shop workbook content, for a client-data tree the clientdata row can serve.</summary>
    public static string ShopItem2Content => ShopItem2Xml;

    /// <summary>Gets the synthetic pet workbook content, for a client-data tree the clientdata row can serve.</summary>
    public static string ShopItemPetContent => ShopItemPetXml;

    /// <summary>Gets a pet shop workbook with lucci and cash sets, a placeholder row and an off-sale row.</summary>
    public static string ShopItemPet => Write("ShopItemPet.xml", ShopItemPetXml);

    // The term cells are the last four header cells and the ss:Index="9" run of each item row.
    private static string WithoutTerms(string xml)
    {
        var document = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ss = "urn:schemas-microsoft-com:office:spreadsheet";
        var sheet = document.Descendants(ss + "Worksheet").Single(w => (string?)w.Attribute(ss + "Name") == "ShopItem");
        var rows = sheet.Descendants(ss + "Row").ToArray();
        foreach (var cell in rows[0].Elements(ss + "Cell").TakeLast(4).ToArray())
            cell.Remove();
        foreach (var row in rows.Skip(1))
            foreach (var cell in row.Elements(ss + "Cell").Skip(2).ToArray())
                cell.Remove();
        return document.Declaration + Environment.NewLine + document.ToString();
    }

    private static string Write(string name, string content)
    {
        var path = Path.Combine(Directory.Value, name);
        lock (Directory)
        {
            if (!File.Exists(path))
                File.WriteAllText(path, content);
        }
        return path;
    }

    private const string ShopItem2Xml = """
        <?xml version="1.0"?>
        <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet"
         xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
         <Worksheet ss:Name="Stock">
          <Table>
           <Row>
            <Cell><Data ss:Type="String">stockId</Data></Cell>
            <Cell><Data ss:Type="String">itemId</Data></Cell>
            <Cell><Data ss:Type="String">isOnSale</Data></Cell>
            <Cell><Data ss:Type="String">type</Data></Cell>
            <Cell><Data ss:Type="String">duration</Data></Cell>
            <Cell><Data ss:Type="String">showPrice</Data></Cell>
            <Cell><Data ss:Type="String">sellPrice</Data></Cell>
            <Cell><Data ss:Type="String">sellPriceCash</Data></Cell>
            <Cell><Data ss:Type="String">secondCash</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">1000001</Data></Cell>
            <Cell><Data ss:Type="Number">100001</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">120</Data></Cell>
            <Cell><Data ss:Type="Number">120</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">3000001</Data></Cell>
            <Cell><Data ss:Type="Number">200001</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">4500</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">4500</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
           </Row>
          </Table>
         </Worksheet>
         <Worksheet ss:Name="StockSet">
          <Table>
           <Row>
            <Cell><Data ss:Type="String">stockSetId</Data></Cell>
            <Cell><Data ss:Type="String">stockSetName</Data></Cell>
            <Cell><Data ss:Type="String">isOnSale</Data></Cell>
            <Cell><Data ss:Type="String">showPrice</Data></Cell>
            <Cell><Data ss:Type="String">sellPrice</Data></Cell>
            <Cell><Data ss:Type="String">sellPriceCash</Data></Cell>
            <Cell><Data ss:Type="String">moneyType</Data></Cell>
            <Cell><Data ss:Type="String">refundable</Data></Cell>
            <Cell><Data ss:Type="String">mainCategory</Data></Cell>
            <Cell><Data ss:Type="String">subCategory</Data></Cell>
            <Cell><Data ss:Type="String">stockId01</Data></Cell>
            <Cell><Data ss:Type="String">displayOrder</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">2000001</Data></Cell>
            <Cell><Data ss:Type="String">Round sneakers</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">120</Data></Cell>
            <Cell><Data ss:Type="Number">120</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="String">lucci</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="String">Clothes</Data></Cell>
            <Cell><Data ss:Type="String">Shoes</Data></Cell>
            <Cell><Data ss:Type="Number">1000001</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">4000001</Data></Cell>
            <Cell><Data ss:Type="String">Cash spray</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">4500</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">4500</Data></Cell>
            <Cell><Data ss:Type="String">cash</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="String">Accessories</Data></Cell>
            <Cell><Data ss:Type="String">Spray</Data></Cell>
            <Cell><Data ss:Type="Number">3000001</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">2000999</Data></Cell>
            <Cell><Data ss:Type="String">Off sale</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="String">lucci</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="String">Clothes</Data></Cell>
            <Cell><Data ss:Type="String">Shoes</Data></Cell>
            <Cell><Data ss:Type="Number">1000001</Data></Cell>
           </Row>
          </Table>
         </Worksheet>
         <Worksheet ss:Name="ShopItem">
          <Table>
           <Row>
            <Cell><Data ss:Type="String">itemId</Data></Cell>
            <Cell><Data ss:Type="String">itemName</Data></Cell>
            <Cell><Data ss:Type="String">isOnSale</Data></Cell>
            <Cell><Data ss:Type="String">moneyType</Data></Cell>
            <Cell><Data ss:Type="String">refundable</Data></Cell>
            <Cell><Data ss:Type="String">mainCategory</Data></Cell>
            <Cell><Data ss:Type="String">subCategory</Data></Cell>
            <Cell><Data ss:Type="String">displayOrder</Data></Cell>
            <Cell><Data ss:Type="String">durationType</Data></Cell>
            <Cell><Data ss:Type="String">maxDuration</Data></Cell>
            <Cell><Data ss:Type="String">buyAgain</Data></Cell>
            <Cell><Data ss:Type="String">function</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">100001</Data></Cell>
            <Cell><Data ss:Type="String">Round sneakers</Data></Cell>
            <Cell ss:Index="9"><Data ss:Type="String">day</Data></Cell>
            <Cell><Data ss:Type="Number">999</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">200001</Data></Cell>
            <Cell><Data ss:Type="String">Cash spray</Data></Cell>
            <Cell ss:Index="9"><Data ss:Type="String">day</Data></Cell>
            <Cell><Data ss:Type="Number">999</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
           </Row>
          </Table>
         </Worksheet>
         <Worksheet ss:Name="MainCategory">
          <Table>
           <Row>
            <Cell><Data ss:Type="String">id</Data></Cell>
            <Cell><Data ss:Type="String">name</Data></Cell>
            <Cell><Data ss:Type="String">displayName</Data></Cell>
            <Cell><Data ss:Type="String">displayOrder</Data></Cell>
            <Cell><Data ss:Type="String">shopView</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="String">Clothes</Data></Cell>
            <Cell><Data ss:Type="String">Clothes</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">2</Data></Cell>
            <Cell><Data ss:Type="String">Accessories</Data></Cell>
            <Cell><Data ss:Type="String">Accessories</Data></Cell>
            <Cell><Data ss:Type="Number">2</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">1000</Data></Cell>
            <Cell><Data ss:Type="String">Main</Data></Cell>
            <Cell><Data ss:Type="String">Main</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
           </Row>
          </Table>
         </Worksheet>
         <Worksheet ss:Name="SubCategory">
          <Table>
           <Row>
            <Cell><Data ss:Type="String">id</Data></Cell>
            <Cell><Data ss:Type="String">name</Data></Cell>
            <Cell><Data ss:Type="String">displayName</Data></Cell>
            <Cell><Data ss:Type="String">displayOrder</Data></Cell>
            <Cell><Data ss:Type="String">shopView</Data></Cell>
            <Cell><Data ss:Type="String">pageType</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">10</Data></Cell>
            <Cell><Data ss:Type="String">Shoes</Data></Cell>
            <Cell><Data ss:Type="String">Shoes</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">11</Data></Cell>
            <Cell><Data ss:Type="String">Spray</Data></Cell>
            <Cell><Data ss:Type="String">Spray</Data></Cell>
            <Cell><Data ss:Type="Number">2</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">2</Data></Cell>
           </Row>
          </Table>
         </Worksheet>
        </Workbook>
        """;

    private const string ShopItemPetXml = """
        <?xml version="1.0"?>
        <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet"
         xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
         <Worksheet ss:Name="Stock">
          <Table>
           <Row>
            <Cell><Data ss:Type="String">stockId</Data></Cell>
            <Cell><Data ss:Type="String">itemId</Data></Cell>
            <Cell><Data ss:Type="String">isOnSale</Data></Cell>
            <Cell><Data ss:Type="String">type</Data></Cell>
            <Cell><Data ss:Type="String">duration</Data></Cell>
            <Cell><Data ss:Type="String">showPrice</Data></Cell>
            <Cell><Data ss:Type="String">sellPrice</Data></Cell>
            <Cell><Data ss:Type="String">상품 이름</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">3000001</Data></Cell>
            <Cell><Data ss:Type="Number">200001</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">120</Data></Cell>
            <Cell><Data ss:Type="Number">120</Data></Cell>
            <Cell><Data ss:Type="String">펫 스니커즈</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="String">pseudo row</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">3000002</Data></Cell>
            <Cell><Data ss:Type="Number">200002</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">9</Data></Cell>
            <Cell><Data ss:Type="Number">7</Data></Cell>
            <Cell><Data ss:Type="Number">10</Data></Cell>
            <Cell><Data ss:Type="Number">20</Data></Cell>
            <Cell><Data ss:Type="String">펫 모자</Data></Cell>
           </Row>
          </Table>
         </Worksheet>
         <Worksheet ss:Name="StockSet">
          <Table>
           <Row>
            <Cell><Data ss:Type="String">stockSetId</Data></Cell>
            <Cell><Data ss:Type="String">stockSetName</Data></Cell>
            <Cell><Data ss:Type="String">isOnSale</Data></Cell>
            <Cell><Data ss:Type="String">showPrice</Data></Cell>
            <Cell><Data ss:Type="String">sellPrice</Data></Cell>
            <Cell><Data ss:Type="String">moneyType</Data></Cell>
            <Cell><Data ss:Type="String">refundable</Data></Cell>
            <Cell><Data ss:Type="String">mainCategory</Data></Cell>
            <Cell><Data ss:Type="String">subCategory</Data></Cell>
            <Cell><Data ss:Type="String">lucci</Data></Cell>
            <Cell><Data ss:Type="String">stockId01</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">4000001</Data></Cell>
            <Cell><Data ss:Type="String">펫 세트</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">120</Data></Cell>
            <Cell><Data ss:Type="Number">120</Data></Cell>
            <Cell><Data ss:Type="String">lucci</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="String">Clothes</Data></Cell>
            <Cell><Data ss:Type="String">Shoes</Data></Cell>
            <Cell><Data ss:Type="Number">500</Data></Cell>
            <Cell><Data ss:Type="Number">3000001</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">4000002</Data></Cell>
            <Cell><Data ss:Type="String">Cash pet set</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">4500</Data></Cell>
            <Cell><Data ss:Type="Number">4500</Data></Cell>
            <Cell><Data ss:Type="String">cash</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="String">Accessories</Data></Cell>
            <Cell><Data ss:Type="String">Spray</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">3000002</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">4000999</Data></Cell>
            <Cell><Data ss:Type="String">Off sale</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="String">lucci</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="String">Clothes</Data></Cell>
            <Cell><Data ss:Type="String">Shoes</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">3000001</Data></Cell>
           </Row>
          </Table>
         </Worksheet>
         <Worksheet ss:Name="ShopItem">
          <Table>
           <Row>
            <Cell><Data ss:Type="String">itemId</Data></Cell>
            <Cell><Data ss:Type="String">itemName</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">200001</Data></Cell>
            <Cell><Data ss:Type="String">펫 스니커즈</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">200002</Data></Cell>
            <Cell><Data ss:Type="String">펫 모자</Data></Cell>
           </Row>
          </Table>
         </Worksheet>
         <Worksheet ss:Name="MainCategory">
          <Table>
           <Row>
            <Cell><Data ss:Type="String">id</Data></Cell>
            <Cell><Data ss:Type="String">name</Data></Cell>
            <Cell><Data ss:Type="String">nameKr</Data></Cell>
            <Cell><Data ss:Type="String">displayOrder</Data></Cell>
            <Cell><Data ss:Type="String">shopView</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="String">note text, not a number</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="String">Clothes</Data></Cell>
            <Cell><Data ss:Type="String">의 상</Data></Cell>
            <Cell><Data ss:Type="Number">10</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">2</Data></Cell>
            <Cell><Data ss:Type="String">Accessories</Data></Cell>
            <Cell><Data ss:Type="String">Ornament</Data></Cell>
            <Cell><Data ss:Type="Number">20</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
           </Row>
          </Table>
         </Worksheet>
         <Worksheet ss:Name="SubCategory">
          <Table>
           <Row>
            <Cell><Data ss:Type="String">id</Data></Cell>
            <Cell><Data ss:Type="String">name</Data></Cell>
            <Cell><Data ss:Type="String">nameKr</Data></Cell>
            <Cell><Data ss:Type="String">displayOrder</Data></Cell>
            <Cell><Data ss:Type="String">shopView</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
            <Cell><Data ss:Type="Number">0</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">10</Data></Cell>
            <Cell><Data ss:Type="String">Shoes</Data></Cell>
            <Cell><Data ss:Type="String">신발</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
           </Row>
           <Row>
            <Cell><Data ss:Type="Number">11</Data></Cell>
            <Cell><Data ss:Type="String">Spray</Data></Cell>
            <Cell><Data ss:Type="String">스프레이</Data></Cell>
            <Cell><Data ss:Type="Number">2</Data></Cell>
            <Cell><Data ss:Type="Number">1</Data></Cell>
           </Row>
          </Table>
         </Worksheet>
        </Workbook>
        """;
}
