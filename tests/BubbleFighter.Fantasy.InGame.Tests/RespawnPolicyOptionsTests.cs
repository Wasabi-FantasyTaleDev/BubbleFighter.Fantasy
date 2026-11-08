using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks the default-allow respawn policy and the explicit, bounded mode exclusions.</summary>
public sealed class RespawnPolicyOptionsTests
{
    /// <summary>No mode, including a future unknown identifier, needs an allow-list entry to respawn.</summary>
    [Fact]
    public void DefaultPolicyAllowsEveryMode()
    {
        var options = new InGameOptions();
        Assert.Empty(options.RespawnDisabledModeIds);
        Assert.All(Enumerable.Range(0, ushort.MaxValue + 1), mode => Assert.True(options.AllowsRespawn(mode)));
    }

    /// <summary>The snake-case plugin setting denies only the selected modes.</summary>
    [Fact]
    public void ConfigurationDeniesOnlyExplicitModes()
    {
        var options = Bind([19, 106]);
        Assert.Equal([19, 106], options.RespawnDisabledModeIds);
        Assert.False(options.AllowsRespawn(19));
        Assert.False(options.AllowsRespawn(106));
        Assert.True(options.AllowsRespawn(1));
        Assert.True(options.AllowsRespawn(65535));
    }

    /// <summary>Out-of-range, repeated and over-capacity exclusions fail configuration binding.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void InvalidModeIsRejected(int mode) => Assert.Throws<ConfigException>(() => Bind([mode]));

    /// <summary>A policy has a bounded collection of unique identifiers.</summary>
    [Fact]
    public void DuplicateAndExcessiveModesAreRejected()
    {
        Assert.Throws<ConfigException>(() => Bind([19, 19]));
        Assert.Throws<ConfigException>(() => Bind(Enumerable.Range(1, 65).ToArray()));
    }

    private static InGameOptions Bind(int[] modes) => new PluginConfig(new PluginRow
    {
        Id = "ingame", Plugin = "ingame", Source = "test",
        Config = new Dictionary<string, object?> { ["respawn_disabled_mode_ids"] = modes },
    }).Bind<InGameOptions>();
}
