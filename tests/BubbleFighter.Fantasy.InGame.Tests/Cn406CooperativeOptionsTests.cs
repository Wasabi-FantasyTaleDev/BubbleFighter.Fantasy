using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.InGame;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Validates the composed cn-406 cooperative configuration against the plugin option rules.</summary>
public sealed class Cn406CooperativeOptionsTests
{
    [Fact]
    public void ComposedCooperativeConfigurationBindsAndValidates()
    {
        var composer = new ProfileComposer(RepositoryRoot("profiles"),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null);
        var rows = composer.Compose("cn-406", []);

        // The room row's native flags and the ingame mode ids must both survive the option rules: an
        // invalid cooperative value must fail here, before a profile ever reaches a listener.
        var rooms = new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.rooms")).Bind<RoomsOptions>();
        // The audited wire consumers are mounted (cn-406 sub_700EE0 cases 924-929) and mode 16 activates them.
        Assert.True(rooms.PvePoints);
        Assert.True(rooms.RescueMissions);
        Assert.True(rooms.CountedPveItems);
        Assert.True(rooms.ZombieConversion);
        Assert.True(rooms.TutorialMoveIn);

        var options = new PluginConfig(Assert.Single(rows, row => row.Id == "ingame")).Bind<InGameOptions>();
        Assert.Equal(16, options.ZombieConversionModeId);
        Assert.Equal(600000, options.ZombieRoundTimeLimitMs);
        Assert.Equal(16, options.CountedItemModeId);
        Assert.Equal([229, 146, 136], options.CountedItemKinds);
        Assert.Equal([101, 17], options.CountedImmediateItemKinds);
        Assert.Equal(3, options.CountedItemSlotCount);
        // cn-406 sub_42A5B0 sends the own AiFighterConfig unit index; awards are server policy.
        Assert.Equal(16, options.PvePointModeId);
        Assert.Equal(16, options.PvePointUnitValues.Length);
        Assert.Equal(16, options.RescueModeId);
        Assert.Equal([20000, 47500, 75000, 102500, 130000, 157500], options.RescueStartTimesMs);
        Assert.Equal(20000, options.RescueDurationMs);
        Assert.Equal(7500, options.RescueCooldownMs);
        Assert.Equal(185000, options.RescueStopTimeMs);
        Assert.Equal([1, 4, 7, 10, 13, 16, 19, 22], options.RescueMissionIds);
        // The INI CHR_PROPERTY comments pair 13 with Mos (ms_citizen5), 16 with Ethi (ms_citizen6),
        // 19 with Marid (ms_citizen7) and 22 with Uni (ms_citizen4).
        Assert.Equal(1, options.RescueMissionIds[0]);
        Assert.Equal("ms_citizen", options.RescueCitizenNames[0]);
        Assert.Equal("ms_citizen5", options.RescueCitizenNames[4]);
        Assert.Equal("ms_citizen6", options.RescueCitizenNames[5]);
        Assert.Equal("ms_citizen7", options.RescueCitizenNames[6]);
        Assert.Equal("ms_citizen4", options.RescueCitizenNames[7]);
        var maps = options.RescueMapEnemyCounts.Select(map => (map.MapKey, map.EnemyCounts)).ToArray();
        Assert.Equal(7, maps.Length);
        Assert.Equal([], Assert.Single(maps, map => map.MapKey == "rel_zombie04_1_Oneway").EnemyCounts);
        Assert.Equal([2, 3, 3, 3, 4, 4], Assert.Single(maps, map => map.MapKey == "rel_zombie_01").EnemyCounts);
        Assert.Equal([2, 2, 3, 3, 4, 4], Assert.Single(maps, map => map.MapKey == "rel_zombie06_Frontal").EnemyCounts);
        // Boss rooms have no start button before kr-812; pirate rooms keep theirs on CN2013.
        Assert.Equal([16], options.AutoStartModeIds);
    }

    [Fact]
    public void ComposedCooperativeRoomRowRejectsOutOfRangeValues()
    {
        var composer = new ProfileComposer(RepositoryRoot("profiles"),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null);
        var rows = composer.Compose("cn-406", []);
        var ingame = Assert.Single(rows, row => row.Id == "ingame");
        var mutated = ingame with
        {
            Config = new Dictionary<string, object?>(ingame.Config!) { ["rescue_mode_id"] = 16, ["rescue_duration_ms"] = 0 },
        };
        var bound = new PluginConfig(mutated);
        Assert.Throws<ConfigException>(() => bound.Bind<InGameOptions>());
    }

    private static string RepositoryRoot(string leaf)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "packets", "builds.yaml")))
                return Path.Combine(directory.FullName, leaf);
        throw new InvalidOperationException("Repository packet tables were not found.");
    }
}
