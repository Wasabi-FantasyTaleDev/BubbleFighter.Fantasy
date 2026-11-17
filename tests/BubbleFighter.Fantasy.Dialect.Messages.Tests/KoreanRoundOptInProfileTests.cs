using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the kr-1256 round features that its shipped profile turns on and its descendants do not inherit.</summary>
public sealed class KoreanRoundOptInProfileTests
{
    private static readonly string Profiles = Path.Combine(Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName, "profiles");

    /// <summary>
    /// kr-1256 drops weapon objects (Zombie Escape boxes) and advances the held item selection after use, because its
    /// client only empties the used slot and waits for gspSwitchItemSlot.
    /// </summary>
    [Fact]
    public void KoreanProfileDropsWeaponsAndAdvancesTheItemSelection()
    {
        var rows = Composer().Compose("kr-1256", []);
        Assert.Equal("true", Assert.Single(rows, row => row.Id == "dialect.rooms").Config!["weapon_objects"]);
        Assert.Equal("true", Assert.Single(rows, row => row.Id == "ingame").Config!["auto_advance_item_slot_after_use"]);
        Assert.True(TestBuilds.Packets.CapabilityOf("kr-1256", "weapon_objects"));
    }

    /// <summary>The China and Taiwan 2019 builds keep weapon objects off until their own notices are audited.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void DescendantsDoNotDropWeaponObjects(string build)
    {
        var rooms = Assert.Single(Composer().Compose(build, []), row => row.Id == "dialect.rooms");
        Assert.NotEqual("true", rooms.Config!.GetValueOrDefault("weapon_objects")?.ToString());
        Assert.False(TestBuilds.Packets.CapabilityOf(build, "weapon_objects"));
    }

    private static ProfileComposer Composer() => new(Profiles, key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN"
        ? "isolated-test-placeholder" : null);
}
