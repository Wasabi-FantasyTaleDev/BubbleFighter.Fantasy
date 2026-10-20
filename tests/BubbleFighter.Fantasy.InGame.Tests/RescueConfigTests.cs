using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks native rescue map definitions through the production configuration binder.</summary>
public sealed class RescueConfigTests
{
    /// <summary>Nested map entries bind without treating resource names as option property names.</summary>
    [Fact]
    public void MapDefinitionsBindAsAValidatedSequence()
    {
        var options = new PluginConfig(new PluginRow
        {
            Id = "ingame", Plugin = "ingame", Source = "test",
            Config = new Dictionary<string, object?>
            {
                ["rescue_mode_id"] = 16,
                ["rescue_start_times_ms"] = new object[] { 20000 },
                ["rescue_mission_ids"] = new object[] { 1 },
                ["rescue_citizen_names"] = new object[] { "ms_citizen" },
                ["rescue_map_enemy_counts"] = new object[]
                {
                    new Dictionary<string, object?> { ["map_key"] = "rel_zombie_01", ["enemy_counts"] = new object[] { 2 } }
                }
            }
        }).Bind<InGameOptions>();
        var map = Assert.Single(options.RescueMapEnemyCounts);
        Assert.Equal("rel_zombie_01", map.MapKey);
        Assert.Equal([2], map.EnemyCounts);
    }

    /// <summary>Maps with no native mission spawners retain an explicit disabled definition.</summary>
    [Fact]
    public void MapWithoutNativeRescuesAcceptsAnEmptySequence()
    {
        var options = new InGameOptions
        {
            RescueModeId = 16,
            RescueStartTimesMs = [20000],
            RescueMissionIds = [1],
            RescueCitizenNames = ["ms_citizen"],
            RescueMapEnemyCounts = [new() { MapKey = "rel_zombie04_1_Oneway", EnemyCounts = [] }]
        };
        var results = options.Validate();
        Assert.Empty(results);
    }
}
