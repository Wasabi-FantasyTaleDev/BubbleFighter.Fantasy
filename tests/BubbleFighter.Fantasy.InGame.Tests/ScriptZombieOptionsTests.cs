using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks explicit script activation, cumulative optional patches and incompatible life policies.</summary>
public sealed class ScriptZombieOptionsTests
{
    /// <summary>Every profile requires its optional patch to activate either side of the native exchange.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void DefaultProfileLeavesTheScriptExchangeDisabled(string build)
    {
        var (rooms, game) = Compose(build, []);
        Assert.False(rooms.ScriptZombies);
        Assert.False(game.ScriptZombies);
        Assert.False(rooms.ZombieEscape);
        Assert.False(game.ZombieEscape);
        Assert.False(rooms.GhostZombies);
        Assert.False(game.GhostZombies);
        Assert.DoesNotContain(8, rooms.ResultScoreboardModes);
    }

    /// <summary>The optional script patch composes with the other audited mode patches and retains every enabled scoreboard.</summary>
    [Theory]
    [MemberData(nameof(PatchCombinations))]
    public void ScriptPatchComposesWithOtherAuditedModes(string build, string[] patches)
    {
        var (rooms, game) = Compose(build, patches);
        Assert.True(rooms.ScriptZombies);
        Assert.True(game.ScriptZombies);
        Assert.Contains(8, rooms.ResultScoreboardModes);
        Assert.True(game.NativeLifeEveryMode || game.NativeLifeModeIds.Contains(8));
        Assert.True(game.AllowsRespawn(8));
        Assert.Equal(180_000, game.ScriptZombieRoundTimeLimitMs);
        Assert.Empty(game.ZombiePvpModeIds);
        if (rooms.BombMission) Assert.Contains(19, rooms.ResultScoreboardModes);
        if (rooms.CaptainMode) Assert.Contains(20, rooms.ResultScoreboardModes);
        if (rooms.ZombieEscape)
        {
            Assert.True(game.ZombieEscape);
            Assert.Contains(32, rooms.ResultScoreboardModes);
        }
        if (rooms.GhostZombies)
        {
            Assert.True(game.GhostZombies);
            Assert.Contains(28, rooms.ResultScoreboardModes);
        }
        if (rooms.ExtremeRacing) Assert.Contains(24, rooms.ResultScoreboardModes);
        if (rooms.FinalZone)
            foreach (var mode in new[] { 23, 25, 30, 45 })
                Assert.Contains(mode, rooms.ResultScoreboardModes);
    }

    /// <summary>Both pair orders and every insertion into the supported cumulative patch sequence keep required boards.</summary>
    public static IEnumerable<object[]> PatchCombinations()
    {
        foreach (var build in new[] { "kr-1256", "cn-774", "tw-454" })
        {
            string[] other = build == "kr-1256" ? ["bomb", "captain", "extreme-racing", "final-zone", "zombie-escape"]
                : ["bomb", "captain", "zombie-escape"];
            if (build != "tw-454") other = [.. other, "ghost-zombies"];
            yield return [build, new[] { "script-zombies" }];
            foreach (var patch in other)
            {
                yield return [build, new[] { patch, "script-zombies" }];
                yield return [build, new[] { "script-zombies", patch }];
            }
            for (var index = 0; index <= other.Length; index++)
            {
                yield return [build, other.Take(index).Append("script-zombies").Concat(other.Skip(index)).ToArray()];
                var reversed = other.Reverse().ToArray();
                yield return [build, reversed.Take(index).Append("script-zombies").Concat(reversed.Skip(index)).ToArray()];
            }
        }
    }

    /// <summary>Ordinary script zombies require a unique mode policy, native life and available respawn.</summary>
    [Theory]
    [InlineData("native-life")]
    [InlineData("respawn")]
    [InlineData("legacy-zombies")]
    [InlineData("conversion")]
    [InlineData("items")]
    [InlineData("counted-items")]
    [InlineData("stars")]
    [InlineData("team-stars")]
    public void ScriptOptionsRejectAnIncompatibleModePolicy(string fault)
    {
        var options = new InGameOptions { ScriptZombies = true, NativeLifeModeIds = [8] };
        switch (fault)
        {
            case "native-life": options.NativeLifeModeIds = []; break;
            case "respawn": options.RespawnDisabledModeIds = [8]; break;
            case "legacy-zombies": options.ZombiePvpModeIds = [8]; break;
            case "conversion": options.ZombieConversionModeId = 8; break;
            case "items": options.ItemModeIds = [8]; break;
            case "counted-items": options.CountedItemModeId = 8; break;
            case "stars": options.StarModeIds = [8]; break;
            case "team-stars": options.StarTeamModeIds = [8]; break;
        }
        Assert.Contains(options.Validate(), error => error.ErrorMessage?.StartsWith("script_zombies", StringComparison.Ordinal) == true);
    }

    private static (RoomsOptions Rooms, InGameOptions Game) Compose(string build, string[] patches)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "packets", "builds.yaml")))
            root = root.Parent ?? throw new InvalidOperationException("Repository packet tables were not found.");
        var profiles = Path.Combine(root.FullName, "profiles");
        var composer = new ProfileComposer(profiles,
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null);
        var rows = composer.Compose(build, patches.Select(patch =>
            Path.Combine(profiles, patch == "zombie-escape" ? "bundles" : build, patch + ".patch.yaml")).ToArray());
        return (new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.rooms")).Bind<RoomsOptions>(),
            new PluginConfig(Assert.Single(rows, row => row.Id == "ingame")).Bind<InGameOptions>());
    }
}
