using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks that modern cooperative inventory and point-policy profile values resolve consistently.</summary>
public sealed class ModernCooperativePointsOptionsTests
{
    /// <summary>Only cooperative zombie mode uses the configured native unit indices and positive server awards.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernCooperativePointPolicyResolvesFromProfile(string build)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "packets", "builds.yaml")))
            root = root.Parent ?? throw new InvalidOperationException("Repository packet tables were not found.");
        var composer = new ProfileComposer(Path.Combine(root.FullName, "profiles"),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null);
        var rows = composer.Compose(build, []);
        var rooms = new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.rooms")).Bind<RoomsOptions>();
        Assert.True(rooms.ModernRooms);
        Assert.False(rooms.ScriptZombies);
        Assert.DoesNotContain(8, rooms.ResultScoreboardModes);
        Assert.True(rooms.PvePoints);
        Assert.True(rooms.SupplyItems);
        Assert.True(rooms.CountedPveItems);
        var options = new PluginConfig(Assert.Single(rows, row => row.Id == "ingame")).Bind<InGameOptions>();
        Assert.False(options.ScriptZombies);
        Assert.Empty(options.ZombiePvpModeIds);
        Assert.Equal(180_000, options.ScriptZombieRoundTimeLimitMs);
        // Native mode-16 hotkeys read 1..3 while ordinary item mode uses an independent two-slot bank.
        Assert.Equal(16, options.CountedItemModeId);
        Assert.Equal(3, options.CountedItemSlotCount);
        Assert.Equal([229, 146, 136], options.CountedItemKinds);
        Assert.All(options.CountedItemKinds, kind => Assert.Contains(kind, options.RandomSupplyItemKinds));
        Assert.Equal(2, options.NativeItemSlotCount);
        Assert.Equal([3], options.ItemModeIds);
        Assert.DoesNotContain(options.CountedItemModeId, options.ItemModeIds);
        Assert.Contains(17, options.CountedImmediateItemKinds);
        Assert.DoesNotContain(17, options.RandomSupplyItemKinds);
        Assert.All(options.CountedImmediateItemKinds, kind => Assert.DoesNotContain(kind, options.CountedItemKinds));
        Assert.All(options.ImmediateSupplyItemKinds, kind => Assert.DoesNotContain(kind, options.RandomSupplyItemKinds));
        Assert.All(options.ItemModeKinds, kind => Assert.Contains(kind, options.RandomSupplyItemKinds));
        Assert.Equal(16, options.PvePointModeId);
        Assert.Equal(16, options.PvePointUnitValues.Length);
        Assert.All(options.PvePointUnitValues, value => Assert.InRange(value, 1, 1000000));
    }
}
