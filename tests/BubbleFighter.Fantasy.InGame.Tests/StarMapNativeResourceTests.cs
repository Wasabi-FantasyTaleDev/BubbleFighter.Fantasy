using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Checks the star map fallback against locally provisioned native KR1256 star maps: the small and big star helpers
/// each map lists in its own <c>track.wb</c>, one small star of each map, and the big stars' first drop at 197 s.
/// </summary>
public sealed class StarMapNativeResourceTests
{
    [Theory]
    [Trait("Category", "LocalClientData")]
    [InlineData("rel_Desert01_Oneway", 40, 2, 2166.535, 99.377, 2924.572)]
    [InlineData("rel_Desert02_Castle", 25, 2, 3277.977, 121.959, 1687.840)]
    [InlineData("rel_Desert03_Temple", 22, 2, 1764.952, 110.122, 1541.043)]
    [InlineData("rel_Desert04_Pharaoh", 24, 2, 227.974, 175.690, 714.010)]
    [InlineData("rel_Forest01_Hill", 34, 1, 2037.000, 243.362, 1566.351)]
    [InlineData("rel_Forest02_Ravine", 25, 2, 1839.849, 110.731, 1341.217)]
    [InlineData("rel_Forest03_Waterfall", 24, 1, 2146.748, 248.431, 1493.875)]
    [InlineData("rel_Forest04_PeterPan", 24, 2, 1457.947, 165.636, 1802.824)]
    [InlineData("rel_Ice01_Cave", 25, 2, 4498.079, 183.476, 3472.641)]
    [InlineData("rel_Ice02_Icepit", 24, 2, 1702.069, 271.945, 2359.902)]
    [InlineData("rel_Ice04_Twist", 24, 2, 1741.995, 163.029, 1885.470)]
    [InlineData("rel_Ice05_Twobridge", 24, 2, 744.816, 488.006, 926.900)]
    [InlineData("rel_Stadium01_Pangpang", 40, 2, 1928.369, 77.973, 1524.362)]
    [InlineData("rel_Stadium02_Dome", 29, 1, 1918.336, 100.386, 2899.542)]
    [InlineData("rel_Stadium03_StardustArena", 40, 2, 1234.056, 148.069, 917.606)]
    [InlineData("rel_Stadium04_Extreme", 24, 1, 2035.503, 185.396, 1770.540)]
    [InlineData("rel_Village01_Byway", 35, 2, 1377.399, 119.254, 1614.975)]
    [InlineData("rel_Village02_waterPark", 32, 1, 1490.320, 128.755, 1474.762)]
    [InlineData("rel_Village03_CityHall", 40, 2, 1230.179, 126.700, 1390.094)]
    [InlineData("rel_Village04_Station", 40, 2, 1393.873, 156.499, 1477.991)]
    [InlineData("rel_Village05_Playground", 28, 1, 1707.360, 156.423, 1409.245)]
    [InlineData("rel_Village06_CirclePark", 30, 2, 1957.976, 356.543, 1586.384)]
    [InlineData("rel_Village07_Easy02", 18, 1, 1625.490, 172.876, 1843.818)]
    [InlineData("rel_pirate01_tear", 28, 1, 2175.145, 247.984, 2038.613)]
    [InlineData("rel_village08_daohouse", 28, 1, 1483.305, 313.475, 1618.408)]
    public void NativeStarMapReadsItsOwnStarHelpers(string stage, int small, int big, double x, double y, double z)
    {
        var points = StarMapPoints.Load(new Store(), stage, 8388608, 4096, 128);
        Assert.Equal(small, points.Count(point => point.Kind == 1));
        Assert.Equal(big, points.Count(point => point.Kind == 2));
        Assert.Contains(points, point => point.Kind == 1
            && Math.Abs(point.X - x) < 0.01 && Math.Abs(point.Y - y) < 0.01 && Math.Abs(point.Z - z) < 0.01);
        Assert.All(points.Where(point => point.Kind == 2), point => Assert.Equal(197000, point.FirstMs));
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
