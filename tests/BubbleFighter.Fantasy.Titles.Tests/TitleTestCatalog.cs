using System.Text;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Titles.Data;

namespace BubbleFighter.Fantasy.Titles.Tests;

internal sealed class TitleTestCatalog(bool modern = false) : IClientDataStore
{
    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:office:spreadsheet";

    internal TitleCatalogSchema Schema => modern ? TitleCatalogSchema.MedalFlag : TitleCatalogSchema.Legacy;
    internal List<string[]> Definitions { get; } = modern
        ? [["400001", "1200001", "0", "0", "0", "1"], ["400009", "1200009", "0", "0", "0", "1"]]
        : [["400001", "1200001", "0", "0", "0"], ["400009", "1200009", "0", "0", "0"]];
    internal List<string[]> Requirements { get; } =
        [["400001", "30007", "20"], ["400001", "30008", "5"], ["400009", "30007", "10"]];
    internal List<string[]> Bonuses { get; } = [];
    internal List<string[]> Conditions { get; } = [];

    /// <summary>Adds two type-3 challenge titles whose only condition is Favorite 1 and 2, as 400312 and 400313 are in the native sheet.</summary>
    internal void AddFavoriteTitles()
    {
        Definitions.Add(modern ? ["400006", "1200006", "3", "0", "0", "0"] : ["400006", "1200006", "3", "0", "0"]);
        Definitions.Add(modern ? ["400007", "1200007", "3", "0", "0", "0"] : ["400007", "1200007", "3", "0", "0"]);
        Conditions.Add(["400006", "Favorite", "1", "1"]);
        Conditions.Add(["400007", "Favorite", "2", "1"]);
    }

    internal TitleCatalog Load(int? bonusMedalQuantity = null, bool favorite = false) => TitleCatalog.Load(this, "Config/BF_title.xml", "Config/ShopItem2.xml",
        Schema, 1_000_000, 100, 32, 1000, bonusMedalQuantity, favorite);

    /// <inheritdoc />
    public bool Exists(string path) => path is "Config/BF_title.xml" or "Config/ShopItem2.xml";

    /// <inheritdoc />
    public byte[] Read(string path)
    {
        var sheets = path switch
        {
            "Config/BF_title.xml" => new[]
            {
                Sheet("title_dec", modern ? ["title_id", "Stock_id", "type", "conceal", "gettitle_date", "use_medal"]
                    : ["title_id", "Stock_id", "type", "conceal", "gettitle_date"], Definitions, true),
                Sheet("title", ["title_id", "medal_id", "count"], Requirements, true),
                Sheet("medal", ["mode", "medalid", "Medal_id"], [["3", "7", "30007"], ["3", "8", "30008"]], true),
                Sheet("bonus", ["title_id", "bonustype", "bonusnum"], Bonuses, true),
                Sheet("TitleConditionSet", ["TitleId", "ConditionId", "AchieveValue", "DisplayOrder"], Conditions, true),
            },
            "Config/ShopItem2.xml" => new[]
            {
                Sheet("Stock", ["stockId", "itemId", "type", "duration"],
                    [["1200001", "400901", "9", "-1"], ["1200009", "400909", "9", "-1"], ["1200006", "400906", "9", "-1"], ["1200007", "400907", "9", "-1"]], false),
                Sheet("ShopItem", ["itemId", "durationType", "maxDuration"],
                    [["400901", "day", "360"], ["400909", "day", "360"], ["400906", "day", "360"], ["400907", "day", "360"]], false),
            },
            _ => throw new FileNotFoundException("The synthetic title workbook is missing.", path),
        };
        return Encoding.UTF8.GetBytes(new XElement(Ns + "Workbook", sheets).ToString());
    }

    private static XElement Sheet(string name, string[] header, IEnumerable<string[]> rows, bool description) =>
        new(Ns + "Worksheet", new XAttribute(Ns + "Name", name), new XElement(Ns + "Table",
            description ? Row(["Native description"]) : null, Row(header), rows.Select(Row)));

    private static XElement Row(string[] cells) => new(Ns + "Row", cells.Select(value =>
        new XElement(Ns + "Cell", new XElement(Ns + "Data", new XAttribute(Ns + "Type", "String"), value))));
}
