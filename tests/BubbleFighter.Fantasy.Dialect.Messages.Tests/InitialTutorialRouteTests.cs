using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks that tutorial routes provide an assigned team representable by the native seat.</summary>
public sealed class InitialTutorialRouteTests
{
    /// <summary>The pirate route assigns the player to the team opposite the native pirate actors.</summary>
    [Fact]
    public void PiratePlayerOpposesNativePirateTeam()
    {
        var route = InitialTutorialRoute.Load(TestBuilds.Packets, "kr-1256", new ClientBuild("kr", 1256, 0), "pirate_prologue");
        Assert.Equal((byte)2, route.PlayerTeam);
        Assert.Equal(103, route.Mode);
    }

    /// <summary>Malformed or absent teams fail before tutorial bindings can admit a player.</summary>
    /// <param name="team">Replacement route value, or null to remove the required field.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("256")]
    [InlineData("blue")]
    public void MissingUnassignedOrUnrepresentableTeamFailsAtLoad(string? team)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bff-tutorial-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var teamField = team is null ? "" : $"        player_team: '{team}'\n";
            File.WriteAllText(Path.Combine(directory, "builds.yaml"), """
                version: 1
                builds:
                  kr-1256:
                    initial_tutorial_routes:
                      pirate_prologue:
                        builds: [kr-1256]
                        mode: 103
                        request_map: rel_Village07_Easy02
                        map: rel_prologue_01
                        difficulty: 1
                        time_limit_ms: 99999000
                """ + "\n" + teamField);
            var tables = PacketTables.Load(directory);
            var error = Assert.Throws<ConfigException>(() => InitialTutorialRoute.Load(tables, "kr-1256", new ClientBuild("kr", 1256, 0), "pirate_prologue"));
            Assert.Contains("player_team", error.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
