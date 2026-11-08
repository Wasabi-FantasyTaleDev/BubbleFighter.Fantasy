using System.Numerics;
using System.Text;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.InGame.Data;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks native resource joins, quantity grammar and import bounds using synthetic map data.</summary>
public sealed class FinalZoneCatalogTests
{
    private static readonly FinalZoneDataLimits Limits = new(65536, 100, 20, 8, 100, 4);
    private const string Prefix = "Maps/fixture/";

    /// <summary>Optional native packs cannot bypass the unimplemented consumption acknowledgement and opening grant.</summary>
    [Fact]
    public void StartingPackRequiresAnEquipmentSnapshotAndRefusesSelectedPacks()
    {
        var store = Fixture();
        store.Files[FinalZoneItemCatalog.Path] += "\nSurvivalPacks:\n  190001: { TestRifle: 1 }\n";
        var catalog = FinalZoneItemCatalog.Load(store, Limits);
        Assert.Throws<InvalidDataException>(() => catalog.ValidateOpeningEquipment(null));
        Assert.Throws<InvalidDataException>(() => catalog.ValidateOpeningEquipment([190001]));
        catalog.ValidateOpeningEquipment([100001]);
        catalog.ValidateOpeningEquipment([]);
    }

    /// <summary>One chosen spot creates the main object and its supplies with distinct server handles and native amount ranges.</summary>
    [Fact]
    public void ExplicitManifestPreservesItemQuantitiesAndBoundsTheWholeRound()
    {
        var store = Fixture();
        var items = FinalZoneItemCatalog.Load(store, Limits);
        var map = FinalZoneMapItems.Load(store, "fixture", items, Limits);
        var manifest = FinalZoneItemManifest.Create(map, new MinimumRandom(), 65536, 10);
        Assert.Equal(new[] { 65536, 65537, 65538 }, manifest.Select(item => item.ObjectId));
        Assert.Equal(new[] { 9001, 9002, 9002 }, manifest.Select(item => item.ItemSpecId));
        Assert.Equal(new[] { 1, 2, 1 }, manifest.Select(item => item.Quantity));
        Assert.Equal((1f, -2f, 3f), (manifest[1].X, manifest[1].Y, manifest[1].Z));
        Assert.Throws<InvalidDataException>(() => FinalZoneItemManifest.Create(map, new MinimumRandom(), 65536, 2));
        Assert.Throws<OverflowException>(() => FinalZoneItemManifest.Create(map, new MinimumRandom(), int.MaxValue, 2));
    }

    /// <summary>Unused permyriad leaves a spot empty, while unsupported object classes cannot become ordinary items.</summary>
    [Fact]
    public void UnusedWeightAndDifferentNativeObjectClassesAreNotInventedLoot()
    {
        var item = new FinalZoneItemSpec("Fixture", 1, "GunAmmo", "FixtureAmmo", -1, true, true, true);
        var rule = new FinalZoneSpawnRule(0, [new(item, new(1, 1))], [], new(0, 0));
        var map = new FinalZoneMapItems([new("Fixture", 1, Vector3.Zero, true, [rule])]);
        Assert.Empty(FinalZoneItemManifest.Create(map, new MinimumRandom(), 65536, 10));
        rule = rule with { Main = [new(item with { Group = "AutoItem" }, new(1, 1))] };
        map = new([new("Fixture", 1, Vector3.Zero, true, [rule])]);
        Assert.Throws<InvalidDataException>(() => FinalZoneItemManifest.Create(map, new MinimumRandom(), 65536, 10));
    }

    /// <summary>Quoted tabular values resolve by column name and retain per-region choices and quantities.</summary>
    [Fact]
    public void MapJoinsSymbolicItemsRegionsDefaultAndNativeSpotFlag()
    {
        var store = Fixture();
        var items = FinalZoneItemCatalog.Load(store, Limits);
        var map = FinalZoneMapItems.Load(store, "fixture", items, Limits);
        Assert.Equal(2, items.Items.Count);
        Assert.Equal("PrimaryWeapon", items.Resolve("TestRifle").Group);
        Assert.True(items.Resolve("TestAmmo").IsSplittable);
        Assert.Equal(2, map.Spots.Count);
        var region = map.Spots[0];
        Assert.Equal(new Vector3(1, -2, 3), region.Position);
        Assert.True(region.Valid);
        var rule = Assert.Single(region.Rules);
        Assert.Equal(7500, rule.Permyriad);
        Assert.Equal(9001, Assert.Single(rule.Main).Item.Id);
        Assert.Equal(new FinalZoneAmountRange(2, 5), Assert.Single(rule.Bonus).Quantity);
        Assert.Equal(new FinalZoneAmountRange(1, 2), rule.BonusCount);
        Assert.False(map.Spots[1].Valid);
        Assert.Equal(9002, Assert.Single(Assert.Single(map.Spots[1].Rules).Main).Item.Id);
    }

    /// <summary>Unknown items, duplicate identities and excess weights fail before any round objects are created.</summary>
    [Theory]
    [InlineData("GenSpawnTable.csv", "TestRifle", "UnknownItem")]
    [InlineData("GenSpawnTable.csv", "7500", "10001")]
    [InlineData("GenSpawnTable.csv", "[2~5]", "[5~2]")]
    [InlineData("GenSpotList.csv", "1, -2, 3", "NaN, 2, 3")]
    [InlineData("GenSpotList.csv", "Unmapped\t2", "Region\t1")]
    [InlineData("GenClusterTable.csv", "Rare", "Missing")]
    public void MalformedMapResourceFailsImport(string file, string original, string replacement)
    {
        var store = Fixture();
        store.Files[Prefix + file] = store.Files[Prefix + file].Replace(original, replacement, StringComparison.Ordinal);
        var items = FinalZoneItemCatalog.Load(store, Limits);
        Assert.Throws<InvalidDataException>(() => FinalZoneMapItems.Load(store, "fixture", items, Limits));
    }

    /// <summary>Item IDs are unique, and each configured resource bound is enforced at the relevant import.</summary>
    [Fact]
    public void DuplicateItemIdsTraversalAndResourceBoundsFail()
    {
        var duplicate = Fixture();
        duplicate.Files[FinalZoneItemCatalog.Path] = duplicate.Files[FinalZoneItemCatalog.Path].Replace("9002", "9001", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => FinalZoneItemCatalog.Load(duplicate, Limits));
        var store = Fixture();
        Assert.Throws<InvalidDataException>(() => FinalZoneItemCatalog.Load(store, Limits with { MaximumFileBytes = 16 }));
        Assert.Throws<InvalidDataException>(() => FinalZoneItemCatalog.Load(store, Limits with { MaximumItemSpecs = 1 }));
        var items = FinalZoneItemCatalog.Load(store, Limits);
        Assert.Throws<InvalidDataException>(() => FinalZoneMapItems.Load(store, "../fixture", items, Limits));
        Assert.Throws<InvalidDataException>(() => FinalZoneMapItems.Load(store, "fixture", items, Limits with { MaximumRows = 1 }));
        Assert.Throws<InvalidDataException>(() => FinalZoneMapItems.Load(store, "fixture", items, Limits with { MaximumQuantity = 4 }));
        Assert.Throws<InvalidDataException>(() => FinalZoneMapItems.Load(store, "fixture", items, Limits with { MaximumBonusItems = 1 }));
    }

    private static Store Fixture() => new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [FinalZoneItemCatalog.Path] = """
            GenItemMeta:
              PrimaryWeapon:
                catTestRifle: { isVariant: false, isReloadNecessary: true, isSplittable: false }
              GunAmmo:
                catTestAmmo: { isVariant: true, isReloadNecessary: true, isSplittable: true }
            GenItems:
              TestRifle: { genItemId: 9001, genItemCategory: catTestRifle, weaponId: 9901 }
              TestAmmo: { genItemId: 9002, genItemCategory: catTestAmmo, weaponId: -1 }
            """,
        [Prefix + "GenClusterTable.csv"] = "ClusterName\tSpawnGroupName\nRegion\tRare\n",
        [Prefix + "GenSpawnTable.csv"] = "SpawnGroupName\tPermyriad\t\tMainCandidates\tBonusCandidates\tBonusSpawnCount\n"
            + "Rare\t7500\t\tTestRifle\tTestAmmo[2~5]\t1~2\n<Default>\t10000\t\tTestAmmo[1]\t\t0\n",
        [Prefix + "GenSpotList.csv"] = "ClusterName\tSpotSn\tSpotPoint\tSpotValid\nRegion\t1\t\"1, -2, 3\"\tTRUE\n"
            + "Unmapped\t2\t\"4, 5, 6\"\tFALSE\n",
    });

    private sealed class Store(Dictionary<string, string> files) : IClientDataStore
    {
        internal Dictionary<string, string> Files { get; } = files;
        public bool Exists(string path) => Files.ContainsKey(path);
        public byte[] Read(string path) => Files.TryGetValue(path, out var value)
            ? Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(value)).ToArray()
            : throw new FileNotFoundException(path);
    }

    private sealed class MinimumRandom : Random
    {
        public override int Next(int maxValue) => 0;
        public override long NextInt64(long minValue, long maxValue) => minValue;
    }
}
