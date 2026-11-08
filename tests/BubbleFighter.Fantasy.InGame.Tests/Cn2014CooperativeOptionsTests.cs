using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks that CN2014's cooperative resources compose with their required native senders.</summary>
public sealed class Cn2014CooperativeOptionsTests
{
    /// <summary>Validates the counted inventory, pickup dependency and resource-specific mission policy together.</summary>
    [Theory]
    [InlineData("cn-705", true)]
    [InlineData("cn-706", true)]
    public void CooperativePolicyHasItsRequiredSurfacesAndNativeResourcePairs(string build, bool ordinaryItems)
    {
        var composer = new ProfileComposer(RepositoryRoot("profiles"),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null);
        var rows = composer.Compose(build, []);
        var rooms = new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.rooms")).Bind<RoomsOptions>();
        Assert.True(rooms.CountedPveItems);
        Assert.True(rooms.RescueMissions);
        Assert.True(rooms.SupplyItems);
        Assert.True(rooms.PvePoints);

        var options = new PluginConfig(Assert.Single(rows, row => row.Id == "ingame")).Bind<InGameOptions>();
        Assert.Equal(16, options.CountedItemModeId);
        Assert.Equal(3, options.CountedItemSlotCount);
        Assert.Equal([229, 146, 136], options.CountedItemKinds);
        Assert.Equal([101, 17], options.CountedImmediateItemKinds);
        Assert.All(options.CountedItemKinds, kind => Assert.Contains(kind, options.RandomSupplyItemKinds));
        Assert.Equal(16, options.RescueModeId);
        Assert.Equal(185000, options.RescueStopTimeMs);
        Assert.Equal(6, options.RescueStartTimesMs.Length);
        Assert.True(options.RescueStartTimesMs[^1] + options.RescueDurationMs < options.RescueStopTimeMs);
        Assert.Equal([1, 4, 7, 10, 13, 16, 19, 22], options.RescueMissionIds);
        // These builds' MissionInfo_Clt rows put Ethi before Mos, unlike cn-406's current profile.
        Assert.Equal(["ms_citizen", "ms_citizen1", "ms_citizen2", "ms_citizen3",
            "ms_citizen6", "ms_citizen5", "ms_citizen7", "ms_citizen4"], options.RescueCitizenNames);
        Assert.Equal(7, options.RescueMapEnemyCounts.Length);
        Assert.Empty(Assert.Single(options.RescueMapEnemyCounts, map => map.MapKey == "rel_zombie04_1_Oneway").EnemyCounts);
        Assert.Equal([2, 3, 3, 4, 4, 4], Assert.Single(options.RescueMapEnemyCounts, map => map.MapKey == "rel_zombie03_Circle").EnemyCounts);
        Assert.Equal(16, options.PvePointModeId);
        Assert.Equal(16, options.PvePointUnitValues.Length);
        Assert.Equal(ordinaryItems ? new[] { 3 } : [], options.ItemModeIds);
        Assert.Equal([101, 102, 103], options.LotteryModes);
    }

    private static string RepositoryRoot(string leaf)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "packets", "builds.yaml")))
                return Path.Combine(directory.FullName, leaf);
        throw new InvalidOperationException("Repository packet tables were not found.");
    }
}
