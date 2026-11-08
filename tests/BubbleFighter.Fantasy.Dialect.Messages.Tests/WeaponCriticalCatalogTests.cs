using System.Text;
using System.Xml.Linq;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Archives;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins weapon-slot identity, resource inheritance and the bounded opt-in seed contribution.</summary>
public sealed partial class WeaponCriticalCatalogTests
{
    /// <summary>The HC replacement adds its category master and affects only the primary table.</summary>
    [Theory]
    [InlineData(16)]
    [InlineData(101)]
    public void ModeReplacementAddsMasterOnceAndKeepsTheSidearmIndependent(int mode)
    {
        var result = Load().Contributions(new(mode, new[] { 300001, 203103, 201001, 207001 }));
        Assert.Equal(0.22, result.Primary, 6);
        Assert.Equal(0.02, result.Sidearm, 6);
    }

    /// <summary>Modes without a replacement retain their ordinary weapon contribution.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(19)]
    public void OrdinaryModesKeepTheirWeaponContribution(int mode) =>
        Assert.Equal((0.02, 0.02), Load().Contributions(new(mode, new[] { 203103, 201001 })));

    /// <summary>Accepted temporary spec selectors override worn equipment independently for both slots.</summary>
    [Fact]
    public void TemporarySpecificationsWinOverWornItems()
    {
        var result = Load().Contributions(new(101, new[] { 202001, 201001 }, 3103, 1001));
        Assert.Equal(0.22, result.Primary, 6);
        Assert.Equal(0.02, result.Sidearm, 6);
        Assert.Throws<InvalidOperationException>(() => Load().Contributions(new(101, new[] { 203103 }, 1001, 3103)));
    }

    /// <summary>Native nonpositive selectors preserve worn/default resolution and the same immutable issuance identity.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveTemporarySelectorsUseWornOrDefaultSpecifications(int sentinel)
    {
        var equipment = new MatchSeedEquipment(101, new[] { 203103, 201001 }, sentinel, sentinel);
        Assert.Null(equipment.PrimarySpecification);
        Assert.Null(equipment.SidearmSpecification);
        Assert.Equal(new(101, new[] { 203103, 201001 }), equipment);
        var contributions = Load().Contributions(equipment);
        Assert.Equal(0.22, contributions.Primary, 6);
        Assert.Equal(0.02, contributions.Sidearm, 6);
        Assert.Equal((0.02, 0.02), Load().Contributions(new(101, Array.Empty<int>(), sentinel, sentinel)));
    }

    /// <summary>Empty slots use the proven native defaults, while unknown worn weapons fail instead of falling back.</summary>
    [Fact]
    public void NativeDefaultsDoNotHideUnknownOrConflictingEquipment()
    {
        Assert.Equal((0.02, 0.02), Load().Contributions(new(16, new[] { 300001 })));
        Assert.Throws<InvalidOperationException>(() => Load().Contributions(null));
        Assert.Throws<InvalidOperationException>(() => Load().Contributions(new(16, new[] { 203999 })));
        Assert.Throws<InvalidOperationException>(() => Load().Contributions(new(16, new[] { 203103, 202001 })));
        Assert.Throws<InvalidOperationException>(() => Load().Contributions(new(16, new[] { 203103 }, 9999)));
    }

    /// <summary>Only one mode substitution occurs, even when its target names another replacement.</summary>
    [Fact]
    public void ModeReplacementIsNotRecursive()
    {
        var store = Fixture();
        var original = store.Files["Config/WeaponSpecDetail.xml"];
        Edit(store, DetailPath, document => Property(document, "Shotgun", "zombiePveModeId")
            .Elements(Spreadsheet + "Cell").Last().Element(Spreadsheet + "Data")!.Value = "3103");
        Assert.NotEqual(original, store.Files["Config/WeaponSpecDetail.xml"]);
        Assert.Equal(0.22, Load(store).Contributions(new(16, new[] { 203103, 201001 })).Primary, 6);
    }

    /// <summary>Missing references, duplicate identities and invalid chance values fail at catalog load.</summary>
    [Theory]
    [InlineData("20", "NaN")]
    [InlineData("20", "Infinity")]
    [InlineData("20", "-3")]
    [InlineData("20", "100")]
    [InlineData("3221", "9999")]
    public void InvalidResourceInputsFailBeforeIssuance(string original, string replacement)
    {
        var store = Fixture();
        var path = "Config/WeaponSpecDetail.xml";
        var before = store.Files[path];
        Edit(store, path, document => Property(document, "Shotgun", original == "3221" ? "zombiePveModeId" : "crtChancePercent")
            .Elements(Spreadsheet + "Cell").ElementAt(original == "3221" ? 2 : 3).Element(Spreadsheet + "Data")!.Value = replacement);
        Assert.NotEqual(before, store.Files[path]);
        Assert.Throws<ConfigException>(() => Load(store));
    }

    /// <summary>Byte, sparse-column and worn-set caps are enforced with no allocation from an arbitrary sparse index.</summary>
    [Fact]
    public void ConfiguredBoundsAreEnforced()
    {
        Assert.Throws<ConfigException>(() => Load(options: new() { WeaponWorkbookMaxBytes = 16 }));
        Assert.Throws<ConfigException>(() => Load(options: new() { WeaponWorkbookMaxColumns = 3 }));
        Assert.Throws<ConfigException>(() => Load(options: new() { WeaponCatalogMaxRecords = 1 }));
        Assert.Throws<InvalidOperationException>(() => Load(options: new() { WeaponMaxWornItems = 1 })
            .Contributions(new(16, new[] { 203103, 201001 })));
    }

    /// <summary>Locally calculated and unaudited seeded builds cannot enable this resolver.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-279")]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("id-1809")]
    [InlineData("kr-1256")]
    public void UnlistedBuildCannotOptIn(string build) => Assert.Throws<ConfigException>(() =>
        WeaponCriticalCatalog.Load(Fixture(), TestBuilds.Packets, build, new()));

    /// <summary>Every audited seed consumer resolves the same slot contract using its own supplied catalog.</summary>
    [Theory]
    [InlineData("cn-324")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("id-400")]
    public void AuditedSeedConsumersResolveBothMappedModes(string build)
    {
        var catalog = WeaponCriticalCatalog.Load(Fixture(), TestBuilds.Packets, build, new());
        foreach (var mode in new[] { 16, 101 })
        {
            Assert.True(catalog.AppliesTo(mode));
            var chance = catalog.Contributions(new(mode, new[] { 203103, 201001 }));
            Assert.Equal(0.22, chance.Primary, 6);
            Assert.Equal(0.02, chance.Sidearm, 6);
            Assert.Equal(chance, catalog.Contributions(new(mode, new[] { 202001, 201001 }, 3103, -1)));
        }
        Assert.True(catalog.AppliesTo(1));
    }

    /// <summary>Equipment snapshots own their storage and compare by values rather than list identity.</summary>
    [Fact]
    public void SnapshotCopiesAndCanonicalizesItsInput()
    {
        var items = new[] { 203103, 201001, 203103 };
        var snapshot = new MatchSeedEquipment(16, items, 3103, 1001);
        items[0] = 202001;
        var same = new MatchSeedEquipment(16, new[] { 201001, 203103 }, 3103, 1001);
        Assert.Equal(snapshot, same);
        Assert.Equal(snapshot.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(snapshot, new(101, new[] { 201001, 203103 }, 3103, 1001));
        Assert.NotEqual(snapshot, new(16, new[] { 201001, 203103 }, 2001, 1001));
    }

    /// <summary>Reads the provisioned own-client archive without extracting or changing any proprietary file.</summary>
    [Fact, Trait("Category", "LocalClientData")]
    public void ProvisionedCn324ArchiveResolvesHcMasterAndModeValues()
    {
        var path = Environment.GetEnvironmentVariable("BFF_CN324_CONFIG_ARCHIVE")
            ?? throw new InvalidOperationException("Set BFF_CN324_CONFIG_ARCHIVE to the provisioned CN2012 Config.bfz.");
        using var archive = Archive.Open(path, "Config/Config.bfz");
        var catalog = WeaponCriticalCatalog.Load(new ArchiveStore(archive), TestBuilds.Packets, "cn-324", new());
        Assert.Equal(0.22, catalog.Contributions(new(101, new[] { 203103, 201001 })).Primary, 6);
        Assert.Equal(0.014, catalog.Contributions(new(101, new[] { 202001, 201001 })).Primary, 6);
        Assert.Equal(new WeaponCriticalCatalog.EquipmentAttributes(0, 5, 5, 5000, 40),
            catalog.Attributes(new(1, new[] { 100213, 100214, 100215, 100216, 100217 })));
    }

    /// <summary>Each additional seeded build loads its own real master, detail and item workbooks without cross-build fallback.</summary>
    [Theory, Trait("Category", "LocalClientData")]
    [InlineData("cn-406", "BFF_CN406_CONFIG_ARCHIVE", 0.014)]
    [InlineData("cn-404", "BFF_CN404_CONFIG_ARCHIVE", 0.06)]
    [InlineData("cn-408", "BFF_CN408_CONFIG_ARCHIVE", 0.06)]
    [InlineData("id-400", "BFF_ID400_CONFIG_ARCHIVE", 0.014)]
    public void ProvisionedSeededBuildArchiveResolvesWeaponContributions(string build, string variable, double defaultPrimary)
    {
        var path = Environment.GetEnvironmentVariable(variable)
            ?? throw new InvalidOperationException($"Set {variable} to this build's provisioned Config.bfz.");
        using var archive = Archive.Open(path, "Config/Config.bfz");
        var catalog = WeaponCriticalCatalog.Load(new ArchiveStore(archive), TestBuilds.Packets, build,
            new() { WeaponWorkbookMaxBytes = 33554432 });
        var sidearm = build == "id-400" ? 201003 : 201001;
        foreach (var mode in new[] { 16, 101 })
        {
            Assert.Equal(0.22, catalog.Contributions(new(mode, new[] { 203103, sidearm })).Primary, 6);
            var defaults = catalog.Contributions(new(mode, Array.Empty<int>()));
            Assert.Equal(defaultPrimary, defaults.Primary, 6);
            Assert.Equal(0.02, defaults.Sidearm, 6);
        }
        Assert.Equal(0.02, catalog.Contributions(new(1, new[] { 203103, sidearm })).Primary, 6);
        var timed = catalog.Attributes(new(1, new[] { 100213, 100214, 100215, 100216, 100217 }));
        if (build != "id-400")
            Assert.Equal(new WeaponCriticalCatalog.EquipmentAttributes(0, 5, 5, 5000, 40), timed);
        else
        {
            Assert.Equal(0, timed.DurationMs);
            Assert.Equal(0, timed.TimedPercent);
            Assert.False(timed.TimedEligible);
        }
        if (build == "id-400")
        {
            var necklace = catalog.Contributions(new(1, new[] { 203103, sidearm, 100051 }));
            Assert.Equal(0.06, necklace.Primary, 6);
            Assert.Equal(0.06, necklace.Sidearm, 6);
            var assigned = catalog.Contributions(new(1, new[] { 203103, sidearm, 100051, 190044 }));
            Assert.Equal(0.03, assigned.Primary, 6);
            Assert.Equal(0.03, assigned.Sidearm, 6);
        }
    }

    internal static WeaponCriticalCatalog Load(FixtureStore? store = null, SceneSeedsOptions? options = null) =>
        WeaponCriticalCatalog.Load(store ?? Fixture(), TestBuilds.Packets, "cn-324", options ?? new());

    internal static FixtureStore Fixture()
    {
        XNamespace ns = "urn:schemas-microsoft-com:office:spreadsheet";
        XElement Row(params string[] cells) => new(ns + "Row", cells.Select(value => new XElement(ns + "Cell", new XElement(ns + "Data", value))));
        XElement Sheet(string name, params XElement[] rows) => new(ns + "Worksheet", new XAttribute(ns + "Name", name), new XElement(ns + "Table", rows));
        string Workbook(params XElement[] sheets) => new XElement(ns + "Workbook", sheets).ToString(SaveOptions.DisableFormatting);
        XElement Ordinary(string family, string id) => Sheet(family, Row("id", "description", id));
        return new(new()
        {
            ["Config/WeaponSpecMaster.xml"] = Workbook(Sheet("WeaponSpecMaster",
                Row("id", "description", "1000", "2000", "3000", "4000"),
                Row("weaponCategory", "description", "Pistol", "Rifle", "Shotgun", "SniperRifle"),
                Row("crtChancePercent", "description", "2", "2", "2", "2"))),
            ["Config/WeaponSpecDetail.xml"] = Workbook(Sheet("Pistol", Row("id", "description", "1001", "1003", "1004")),
                Sheet("Rifle", Row("id", "description", "2001", "2003", "2004")),
                Ordinary("SniperRifle", "4001"), Sheet("Shotgun",
                    Row("id", "description", "3103", "3221"), Row("crtChancePercent", "description", "", "20"),
                    Row("zombiePveModeId", "description", "3221", "0"), Row("hordeModeId", "description", "3221", "0"))),
            ["Config/ShopItem2.xml"] = Workbook(Sheet("ShopItem", Row("itemId", "slotLocation"),
                Row("201001", "sWeapon2"), Row("202001", "sWeapon1"), Row("203103", "sWeapon1"), Row("207001", "sWeapon3")),
                Sheet("ShopItemSpec", Row("shopItemId", "buffName", "value", "channel", "buffGroup"))),
        });
    }

    internal sealed class FixtureStore(Dictionary<string, string> files) : IClientDataStore
    {
        internal Dictionary<string, string> Files { get; } = files;
        /// <summary>Checks only the synthetic workbooks supplied by this fixture.</summary>
        public bool Exists(string path) => Files.ContainsKey(path);
        /// <summary>Returns independently encoded workbook bytes without retaining caller storage.</summary>
        public byte[] Read(string path) => Encoding.UTF8.GetBytes(Files.TryGetValue(path, out var value)
            ? value : throw new FileNotFoundException(path));
    }

    private sealed class ArchiveStore(IArchiveReader archive) : IClientDataStore
    {
        /// <summary>Checks a member in the provisioned read-only client archive.</summary>
        public bool Exists(string path) => archive.Contains(path);
        /// <summary>Verifies and reads the requested member without extracting it.</summary>
        public byte[] Read(string path) => archive.Read(path);
    }
}
