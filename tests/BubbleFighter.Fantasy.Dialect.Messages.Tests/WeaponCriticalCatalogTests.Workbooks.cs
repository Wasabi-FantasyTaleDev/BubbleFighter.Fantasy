using System.Xml.Linq;
using BubbleFighter.Fantasy.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed partial class WeaponCriticalCatalogTests
{
    private static readonly XNamespace Spreadsheet = "urn:schemas-microsoft-com:office:spreadsheet";
    private const string DetailPath = "Config/WeaponSpecDetail.xml";

    /// <summary>Unavailable shop entries do not disable valid weapons, but cannot enter a round's seed inputs.</summary>
    [Fact]
    public void MissingShopSpecificationFailsOnlyWhenSelected()
    {
        var store = Fixture();
        Edit(store, "Config/ShopItem2.xml", document => document.Descendants(Spreadsheet + "Worksheet")
            .Single(sheet => (string?)sheet.Attribute(Spreadsheet + "Name") == "ShopItem").Element(Spreadsheet + "Table")!.Add(
            new XElement(Spreadsheet + "Row", new[] { "202320", "sWeapon1" }
                .Select(value => new XElement(Spreadsheet + "Cell", new XElement(Spreadsheet + "Data", value))))));
        var catalog = Load(store);
        Assert.Equal(0.22, catalog.Contributions(new(16, new[] { 203103, 201001 })).Primary, 6);
        Assert.Throws<InvalidOperationException>(() => catalog.Contributions(new(16, new[] { 202320, 201001 })));
    }

    /// <summary>Each empty seed slot resolves its own build's native fallback instead of inheriting an older client's item.</summary>
    [Theory]
    [InlineData("cn-324", 0.02, 0.02)]
    [InlineData("cn-406", 0.02, 0.02)]
    [InlineData("cn-404", 0.06, 0.05)]
    [InlineData("cn-408", 0.06, 0.05)]
    [InlineData("id-400", 0.03, 0.04)]
    public void BuildDefaultsResolveDistinctPrimaryAndSidearmSpecifications(string build, double primary, double sidearm)
    {
        var store = Fixture();
        Edit(store, DetailPath, document =>
        {
            foreach (var (family, amounts) in new[] { ("Rifle", new[] { "0", "1", "4" }), ("Pistol", new[] { "0", "2", "3" }) })
                Property(document, family, "id").Parent!.Add(new XElement(Spreadsheet + "Row",
                    new[] { "crtChancePercent", "description" }.Concat(amounts)
                        .Select(value => new XElement(Spreadsheet + "Cell", new XElement(Spreadsheet + "Data", value)))));
        });
        var catalog = WeaponCriticalCatalog.Load(store, TestBuilds.Packets, build, new());
        var actual = catalog.Contributions(new(1, Array.Empty<int>()));
        Assert.Equal(primary, actual.Primary, 6);
        Assert.Equal(sidearm, actual.Sidearm, 6);
    }

    /// <summary>Identical critical projections of repeated native specification columns do not add the master twice.</summary>
    [Fact]
    public void EquivalentDuplicateSpecificationRetainsOneContribution()
    {
        var store = Fixture();
        Edit(store, DetailPath, document =>
        {
            foreach (var row in Property(document, "Shotgun", "id").Parent!.Elements(Spreadsheet + "Row"))
                row.Add(new XElement(row.Elements(Spreadsheet + "Cell").Last()));
        });
        Assert.Equal(0.22, Load(store).Contributions(new(16, new[] { 203103, 201001 })).Primary, 6);
    }

    /// <summary>Every native replacement field is independent of whether the mode uses seeded critical decisions.</summary>
    [Theory]
    [InlineData("cn-324", 3, "itemModeId")]
    [InlineData("cn-324", 8, "zombieModeId")]
    [InlineData("cn-324", 12, "sniperModeId")]
    [InlineData("cn-324", 13, "singlePvpModeId")]
    [InlineData("cn-324", 14, "teamSurvivalId")]
    [InlineData("cn-406", 18, "teamSurvivalId")]
    [InlineData("cn-404", 18, "teamSurvivalId")]
    [InlineData("cn-408", 18, "teamSurvivalId")]
    [InlineData("id-400", 18, "teamSurvivalId")]
    public void NativeModeSelectsItsOwnReplacement(string build, int mode, string field)
    {
        var store = Fixture();
        Edit(store, DetailPath, document => Property(document, "Shotgun", "id").Parent!.Add(
            new XElement(Spreadsheet + "Row", new[] { field, "description", "3221", "0" }
                .Select(value => new XElement(Spreadsheet + "Cell", new XElement(Spreadsheet + "Data", value))))));
        var catalog = WeaponCriticalCatalog.Load(store, TestBuilds.Packets, build, new());
        Assert.Equal(0.22, catalog.Contributions(new(mode, new[] { 203103, 201001 })).Primary, 6);
        Assert.Equal(0.02, catalog.Contributions(new(1, new[] { 203103, 201001 })).Primary, 6);
    }

    /// <summary>A descendant-only replacement does not change the older client's ordinary weapon selection.</summary>
    [Fact]
    public void OlderBuildDoesNotAcquireTheTeamSurvivalModeAlias()
    {
        var store = Fixture();
        Edit(store, DetailPath, document => Property(document, "Shotgun", "id").Parent!.Add(
            new XElement(Spreadsheet + "Row", new[] { "teamSurvivalId", "description", "3221", "0" }
                .Select(value => new XElement(Spreadsheet + "Cell", new XElement(Spreadsheet + "Data", value))))));
        Assert.Equal(0.02, Load(store).Contributions(new(18, new[] { 203103, 201001 })).Primary, 6);
    }

    /// <summary>A negative detail increment reduces the category master instead of being rejected or clamped separately.</summary>
    [Fact]
    public void SignedDetailReducesTheMasterBeforeProbabilityValidation()
    {
        var store = Fixture();
        Edit(store, DetailPath, document => Property(document, "Shotgun", "crtChancePercent")
            .Elements(Spreadsheet + "Cell").Last().Element(Spreadsheet + "Data")!.Value = "-0.6");
        Assert.Equal(0.014, Load(store).Contributions(new(101, new[] { 203103, 201001 })).Primary, 6);
    }

    /// <summary>Sparse column positions and merged description cells preserve each specification's property alignment.</summary>
    [Fact]
    public void SparseAndMergedCellsKeepTheNativeColumnPosition()
    {
        var store = Fixture();
        Edit(store, DetailPath, document =>
        {
            foreach (var row in document.Descendants(Spreadsheet + "Row"))
            {
                var cells = row.Elements(Spreadsheet + "Cell").ToArray();
                cells[1].SetAttributeValue(Spreadsheet + "MergeAcross", 2);
                cells[2].SetAttributeValue(Spreadsheet + "Index", 5);
            }
        });
        Assert.Equal(0.22, Load(store).Contributions(new(16, new[] { 203103, 201001 })).Primary, 6);
    }

    /// <summary>A blank detail selector inherits its category's replacement id, while critical chance adds only once.</summary>
    [Fact]
    public void BlankModeSelectorInheritsTheMaster()
    {
        var store = Fixture();
        Edit(store, "Config/WeaponSpecMaster.xml", document => document.Descendants(Spreadsheet + "Table").Single().Add(
            new XElement(Spreadsheet + "Row", new[] { "zombiePveModeId", "description", "0", "0", "3221", "0" }
                .Select(value => new XElement(Spreadsheet + "Cell", new XElement(Spreadsheet + "Data", value))))));
        Edit(store, DetailPath, document => Property(document, "Shotgun", "zombiePveModeId")
            .Elements(Spreadsheet + "Cell").ElementAt(2).Element(Spreadsheet + "Data")!.Value = "");
        Assert.Equal(0.22, Load(store).Contributions(new(16, new[] { 203103, 201001 })).Primary, 6);
    }

    /// <summary>Malformed XML positions, duplicate identities and missing selected sheets fail with configuration errors.</summary>
    [Theory]
    [InlineData("overlap")]
    [InlineData("index-overflow")]
    [InlineData("merge-overflow")]
    [InlineData("duplicate-sheet")]
    [InlineData("duplicate-property")]
    [InlineData("duplicate-id")]
    [InlineData("missing-sheet")]
    public void MalformedWorkbookStructureFails(string fault)
    {
        var store = Fixture();
        Edit(store, DetailPath, document =>
        {
            var row = Property(document, "Shotgun", "id");
            var last = row.Elements(Spreadsheet + "Cell").Last();
            switch (fault)
            {
                case "overlap": last.SetAttributeValue(Spreadsheet + "Index", 1); break;
                case "index-overflow": last.SetAttributeValue(Spreadsheet + "Index", int.MaxValue); break;
                case "merge-overflow": last.SetAttributeValue(Spreadsheet + "MergeAcross", int.MaxValue); break;
                case "duplicate-sheet": document.Root!.Add(new XElement(row.Parent!.Parent!)); break;
                case "duplicate-property": row.Parent!.Add(new XElement(row)); break;
                case "duplicate-id": last.Element(Spreadsheet + "Data")!.Value = "3103"; break;
                case "missing-sheet": row.Parent!.Parent!.Remove(); break;
                default: throw new InvalidOperationException(fault);
            }
        });
        Assert.Throws<ConfigException>(() => Load(store));
    }

    /// <summary>DTD declarations are never resolved when loading a weapon workbook.</summary>
    [Fact]
    public void WorkbookDtdIsRejected()
    {
        var store = Fixture();
        store.Files[DetailPath] = "<!DOCTYPE Workbook [<!ENTITY custom '20'>]>" + store.Files[DetailPath];
        Assert.Throws<ConfigException>(() => Load(store));
    }

    private static XElement Property(XDocument document, string sheet, string property) => document
        .Descendants(Spreadsheet + "Worksheet").Single(value => (string?)value.Attribute(Spreadsheet + "Name") == sheet)
        .Descendants(Spreadsheet + "Row").Single(row => row.Elements(Spreadsheet + "Cell").First().Value == property);

    private static void Edit(FixtureStore store, string path, Action<XDocument> change)
    {
        var document = XDocument.Parse(store.Files[path]);
        change(document);
        store.Files[path] = document.ToString(SaveOptions.DisableFormatting);
    }
}
