using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks explicit special-mode policies through the production snake_case option binder.</summary>
public sealed class SpecialModeConfigTests
{
    /// <summary>The native BnB capitalization keeps the established lowercase configuration keys.</summary>
    [Fact]
    public void BnBNamingBindsFromSnakeCaseKeys()
    {
        var options = Bind(new()
        {
            ["bnb"] = true, ["native_match_relay"] = true, ["native_life_every_mode"] = true,
            ["game_protocol"] = "kr-1256/game", ["relay_protocol"] = "kr-1256/relay",
            ["bnb_maximum_cells"] = 128, ["bnb_maximum_position_cell_distance"] = 0,
            ["bnb_stages"] = new object[] { new Dictionary<string, object?>
                { ["stage_directory"] = "map", ["tile_layer_minimum_x"] = 0, ["tile_layer_maximum_z"] = 10 } },
        });
        Assert.True(options.BnB);
        Assert.Equal(128, options.BnBMaximumCells);
        Assert.Equal(0, options.BnBMaximumPositionCellDistance);
        Assert.Equal(0f, Assert.Single(options.BnBStages).TileLayerMinimumX);
    }

    /// <summary>A missing native scene origin never becomes zero merely because a CLR scalar has that default.</summary>
    [Fact]
    public void BnBStageRequiresBothExplicitBounds()
    {
        Assert.Throws<ConfigException>(() => Bind(new()
        {
            ["bnb_stages"] = new object[] { new Dictionary<string, object?>
                { ["stage_directory"] = "map", ["tile_layer_minimum_x"] = 0 } },
        }));
    }

    /// <summary>Coin presentation and round assignments must be explicit even when variant zero is the only supported policy.</summary>
    [Theory]
    [InlineData("coin_variant")]
    [InlineData("coin_rounds")]
    public void RacingStageRejectsAnOmittedCoinPolicy(string omitted)
    {
        var stage = new Dictionary<string, object?>
        {
            ["stage_directory"] = "map", ["first_cannon_identity"] = 100,
            ["first_coin_identity"] = 200, ["first_stone_disposer_identity"] = 300,
            ["coin_variant"] = 0, ["coin_rounds"] = new object[] { 1 },
        };
        stage.Remove(omitted);
        Assert.Throws<ConfigException>(() => Bind(new() { ["extreme_racing_stages"] = new object[] { stage } }));
    }

    private static InGameOptions Bind(Dictionary<string, object?> values) => new PluginConfig(new PluginRow
    {
        Id = "ingame", Plugin = "ingame", Source = "test", Config = values,
    }).Bind<InGameOptions>();
}
