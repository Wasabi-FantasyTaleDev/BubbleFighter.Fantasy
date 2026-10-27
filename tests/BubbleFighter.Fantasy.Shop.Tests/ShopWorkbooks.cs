using System.Security.Cryptography;
using BubbleFighter.Fantasy.Contracts.ClientData;
using System.Text;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Tests;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Serves synthetic workbooks the way the real clientdata row does: the XML is written into a loose-file client-data
/// tree by its client path, then read back from that tree. The loaders only ever see the bytes the store returns.
/// </summary>
internal static class ShopWorkbooks
{
    /// <summary>The synthetic shop workbook (<c>Config/ShopItem2.xml</c>).</summary>
    public static WorkbookFile ShopItem2 =>
        FromXml("shop-item2", SyntheticClientTables.ShopItem2Content, "Config/ShopItem2.xml");

    /// <summary>The synthetic pet overlay workbook (<c>Config/ShopItemPet.xml</c>).</summary>
    public static WorkbookFile ShopItemPet =>
        FromXml("shop-itempet", SyntheticClientTables.ShopItemPetContent, "Config/ShopItemPet.xml");

    /// <summary>A workbook with no bytes, the shape an empty client file has.</summary>
    /// <param name="clientPath">The client path shown in messages.</param>
    /// <returns>The empty workbook.</returns>
    public static WorkbookFile Empty(string clientPath = "Config/ShopItem2.xml") =>
        WorkbookFile.FromBytes(clientPath, []);

    /// <summary>Writes the XML into a client-data tree and returns the bytes read back from its client path.</summary>
    /// <param name="name">A stable name for the tree.</param>
    /// <param name="xml">The workbook content.</param>
    /// <param name="clientPath">The client path the workbook has in the client data.</param>
    /// <returns>The workbook.</returns>
    public static WorkbookFile FromXml(string name, string xml, string clientPath = "Config/ShopItem2.xml")
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        var tree = TestClientData.Tree($"workbook-{name}-{Digest(bytes)}", (clientPath, bytes));
        return WorkbookFile.FromBytes(clientPath, File.ReadAllBytes(Path.Combine(tree.RawDirectory, clientPath)));
    }

    /// <summary>Reads a workbook a test wrote to a temporary file, as the bytes the client-data store would return.</summary>
    /// <param name="path">The temporary file; its path stands in for the client path in messages.</param>
    /// <returns>The workbook.</returns>
    public static WorkbookFile At(string path) => WorkbookFile.FromBytes(path, File.ReadAllBytes(path));

    private static string Digest(byte[] content) => Convert.ToHexString(SHA256.HashData(content))[..12];
}
