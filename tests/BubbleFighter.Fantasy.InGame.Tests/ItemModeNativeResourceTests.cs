using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks structural world-block traversal against locally provisioned native CN2011 map resources.</summary>
public sealed class ItemModeNativeResourceTests
{
    [Theory]
    [Trait("Category", "LocalClientData")]
    [InlineData("rel_Desert04_Pharaoh", 125, 24)]
    [InlineData("rel_Forest01_Hill", 114, 34)]
    [InlineData("rel_Village01_Byway", 130, 24)]
    public void NativeItemMapSelectsNeutralSupplyPositionsFromItsOwnHelpers(string stage, int helperCount, int starCount)
    {
        var store = new Store();
        var helpers = WorldBlockFile.LoadHelpers(store, $"Maps/{stage}/track.wb", 8388608, 4096);
        Assert.Equal(helperCount, helpers.Count);
        var stars = helpers.Where(helper => helper.Name.StartsWith("#sp_star_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(starCount, stars.Length);
        var positions = ItemModeMapPoints.Load(store, stage, 8388608, 4096, 4);
        Assert.Equal(4, positions.Count);
        Assert.Equal(4, positions.Distinct().Count());
        Assert.All(positions, point => Assert.Contains(stars, helper => helper.Position == point));
    }

    private sealed class Store : IClientDataStore
    {
        public bool Exists(string path)
        {
            try { _ = Read(path); return true; }
            catch (FileNotFoundException) { return false; }
        }
        public byte[] Read(string path) => LocalClientData.Bytes("cn-279", path);
    }
}
