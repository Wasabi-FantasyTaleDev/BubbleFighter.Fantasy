using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks joins and phase rules against the locally provisioned native client resources.</summary>
public sealed class FinalZoneNativeResourceTests
{
    private static readonly FinalZoneDataLimits Limits = new(4194304, 16384, 4096, 256, 100000, 64);

    /// <summary>The native tsunami map imports its full spot table and generates only catalogued, uniquely addressed items.</summary>
    [Fact]
    [Trait("Category", "LocalClientData")]
    public void NativeFinalZoneMapBuildsAnExplicitManifestAndOpeningInventory()
    {
        var store = new Store();
        var catalog = FinalZoneItemCatalog.Load(store, Limits);
        var map = FinalZoneMapItems.Load(store, "rel_Sudden08_Finalzone", catalog, Limits);
        Assert.Equal(1823, map.Spots.Count);
        var manifest = FinalZoneItemManifest.Create(map, new Random(12345), 65536, 16384);
        Assert.NotEmpty(manifest);
        Assert.Equal(Enumerable.Range(65536, manifest.Count), manifest.Select(item => item.ObjectId));
        Assert.All(manifest, item =>
        {
            Assert.True(catalog.Items.ContainsKey(item.ItemSpecId));
            Assert.InRange(item.Quantity, 1, Limits.MaximumQuantity);
        });
        var opening = catalog.OpeningInventory();
        Assert.Equal(new[] { 580, 590 }, opening.Select(item => item.ItemSpecId));
        Assert.All(opening, item => Assert.Equal(1, item.Quantity));
        var water = FinalZoneWaterRules.Load(store, "rel_Sudden08_Finalzone", Limits);
        Assert.True(water.Enabled);
        Assert.Equal(105000, water.AlarmMs);
        Assert.Equal(15000, water.ShortenMs);
        Assert.Equal(20000, water.MinimumAlarmMs);
        Assert.Equal(1200000, water.StopMs);
        var drones = FinalZoneDroneRules.FromFields(FinalZoneWaterRules.LoadFields(store, "rel_Sudden08_Finalzone", Limits));
        Assert.True(drones.Enabled);
        Assert.Equal(new FinalZoneAmountRange(10, 20), drones.InitialSeconds);
        Assert.Equal(new FinalZoneAmountRange(100, 100), drones.IntervalSeconds);
        var supply = FinalZoneSupplyCatalog.Load(store, "rel_Sudden08_Finalzone", catalog, Limits).Generate(12345);
        Assert.Equal(3, supply.Count);
        Assert.All(supply, entry => Assert.True(catalog.Items.ContainsKey(entry.ItemSpecId)));
    }

    /// <summary>The gas map disables tsunami and cannot silently instantiate its automatic terrain as ordinary pickup objects.</summary>
    [Fact]
    [Trait("Category", "LocalClientData")]
    public void NativeGasMapRetainsItsDistinctWaterAndObjectClasses()
    {
        var store = new Store();
        var catalog = FinalZoneItemCatalog.Load(store, Limits);
        var map = FinalZoneMapItems.Load(store, "rel_space04_64room", catalog, Limits);
        Assert.Equal(1900, map.Spots.Count);
        Assert.False(FinalZoneWaterRules.Load(store, "rel_space04_64room", Limits).Enabled);
        Assert.Throws<InvalidDataException>(() => FinalZoneItemManifest.Create(map, new Random(12345), 65536, 16384));
    }

    /// <summary>The native apartment maps can spawn ground items without a drone reward table.</summary>
    [Theory]
    [Trait("Category", "LocalClientData")]
    [InlineData("rel_Sudden07_Apartment")]
    [InlineData("rel_zombieSurvival01_Apartment")]
    public void NativeApartmentLoadsGroundItemsWithoutDroneRewards(string stage)
    {
        var store = new Store();
        var catalog = FinalZoneItemCatalog.Load(store, Limits);
        var map = FinalZoneMapItems.Load(store, stage, catalog, Limits);
        var fields = FinalZoneWaterRules.LoadFields(store, stage, Limits);
        var drones = FinalZoneDroneRules.FromFields(fields);
        Assert.False(drones.Enabled);
        Assert.False(store.Exists($"Maps/{stage}/GenDroneRewardTable.csv"));
        Assert.Null(FinalZoneSupplyCatalog.LoadForMap(store, stage, catalog, Limits, drones.Enabled));
        Assert.NotEmpty(FinalZoneItemManifest.Create(map, new Random(12345), 65536, 16384));
    }

    private sealed class Store : IClientDataStore
    {
        public bool Exists(string path)
        {
            try { _ = Read(path); return true; }
            catch (FileNotFoundException) { return false; }
        }
        public byte[] Read(string path) => LocalClientData.Bytes("kr-1256", path);
    }
}
