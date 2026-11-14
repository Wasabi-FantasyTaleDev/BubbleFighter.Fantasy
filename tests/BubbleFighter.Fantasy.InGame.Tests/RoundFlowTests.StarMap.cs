using System.Numerics;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class RoundFlowTests
{
    /// <summary>A star helper's name gives its kind, first appearance and period, as the host's map report reads them.</summary>
    [Fact]
    public void StarHelperNamesBecomeStarPoints()
    {
        static WorldBlockHelper Helper(string name) => new(name, new Vector3(1, 2, 3), Vector3.Zero, 0, "");
        Assert.Equal(new RoundStarPoint(1, 2, 3, 1, 1, 98500), StarMapPoints.Parse(Helper("#sp_star_1_98500_3")));
        Assert.Equal(new RoundStarPoint(1, 2, 3, 2, 197000, 999999), StarMapPoints.Parse(Helper("#sp_bigStar_197000_999999_1")));
        Assert.Equal(new RoundStarPoint(1, 2, 3, 1, 12, 0), StarMapPoints.Parse(Helper("#sp_star_12x")));
        Assert.Null(StarMapPoints.Parse(Helper("#ic_star_1_2_3")));
        Assert.Null(StarMapPoints.Parse(Helper("#sp_supply_1_2_3")));
    }

    /// <summary>
    /// A star round whose host reported no star points reads them from the map's track.wb once, and a host report
    /// arriving after that changes nothing.
    /// </summary>
    [Fact]
    public async Task StarRoundWithoutHostPointsReadsTheMapFile()
    {
        var rig = StarRig();
        var data = new FallbackStore(WorldBlockFixture.Create(
            ("#sp_star_0_60000_0", new(10, 2, 10)), ("#sp_bigStar_0_999999_0", new(20, 2, 20)), ("#ic_box_00", new(30, 2, 30))));
        rig.Round.ConfigureStarMapFallback(data);
        try
        {
            await rig.StartAsync();
            var room = rig.Registry.Find(rig.RoomId)!;
            lock (room.Gate) room.RoundStageKey = "stage/sub";
            await rig.Round.HandleLoadReportedAsync(rig.Host, new(), default);
            await rig.Round.HandleLoadReportedAsync(rig.Guest, new(), default);
            await rig.WaitStartedAsync(rig.Host);
            for (var i = 0; i < 200 && rig.StarCreated.To(rig.Host).Length < 2; i++) await Task.Delay(10);
            Assert.Equal("Maps/stage/sub/track.wb", Assert.Single(data.Reads));
            Assert.Equal(new[] { (1, 10f, 3), (2, 20f, 3) },
                rig.StarCreated.To(rig.Host).Select(star => (star.Kind, star.Point.X, star.Tag)));
            await rig.Round.HandleStarPointsReportedAsync(rig.Host, new RoundStarPointsReported([new(5, 5, 5, 1, 0, 100)]), default);
            await Task.Delay(400);
            Assert.Equal(2, rig.StarCreated.To(rig.Host).Length);
        }
        finally { await rig.Registry.StopAllAsync(); }
    }
}
