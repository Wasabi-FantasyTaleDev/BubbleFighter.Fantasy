using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Checks that the shipped defence opt-in composes a usable native relay and preserves default dormancy.</summary>
public sealed class DefenceProfileTests
{
    private static readonly string Profiles = Path.Combine(Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName, "profiles");

    /// <summary>A default CN2014 profile retains the rows but opens neither the relay nor its defence authority.</summary>
    [Theory]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    public void Cn2014DefenceAndTransportRemainDormantByDefault(string build)
    {
        var rows = Composer().Compose(build, []);
        foreach (var id in NativeRows)
            Assert.True(Assert.Single(rows, row => row.Id == id).Disabled);
        Assert.Equal("false", Assert.Single(rows, row => row.Id == "ingame").Config!["native_match_relay"]);
        Assert.Equal("false", Assert.Single(rows, row => row.Id == "dialect.rooms").Config!["select_match_relay"]);
        Assert.False(TestBuilds.Packets.CapabilityOf(build, "native_scene_seed_tables"));
        Assert.True(TestBuilds.Packets.CapabilityOf(build, "native_defence_seed_tables"));
    }

    /// <summary>Modern profiles include the defence patch, so the seed authority is active beside their native transport.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ModernDefenceIsOnByDefault(string build)
    {
        var rows = Composer().Compose(build, []);
        Assert.False(Assert.Single(rows, row => row.Id == "dialect.scene-seeds").Disabled);
        Assert.False(Assert.Single(rows, row => row.Id == "relay-listener").Disabled);
        Assert.False(TestBuilds.Packets.CapabilityOf(build, "native_scene_seed_tables"));
        Assert.True(TestBuilds.Packets.CapabilityOf(build, "native_defence_seed_tables"));
    }

    /// <summary>The opt-in enables every required native transport row without requiring a UDP listener.</summary>
    [Theory, MemberData(nameof(SeedTableTests.DefenceBuilds), MemberType = typeof(SeedTableTests))]
    public void DefenceOptInEnablesItsNativeTransport(string build)
    {
        var rows = Composer().Compose(build, [Path.Combine(Profiles, "bundles", "defence-seeds.patch.yaml")]);
        foreach (var id in NativeRows)
            Assert.False(Assert.Single(rows, row => row.Id == id).Disabled);
        var authority = Assert.Single(rows, row => row.Id == "dialect.scene-seeds");
        var config = authority.Config!;
        Assert.Equal(build + "/relay", config["protocol"]);
        Assert.Equal("true", config["defence_seeds"]);
        Assert.Equal("true", Assert.Single(rows, row => row.Id == "ingame").Config!["native_match_relay"]);
        Assert.Equal("true", Assert.Single(rows, row => row.Id == "ingame").Config!["match_transport_fallback"]);
        Assert.Equal("true", Assert.Single(rows, row => row.Id == "dialect.rooms").Config!["select_match_relay"]);
        Assert.DoesNotContain(rows, row => row.Plugin == "net.udp");
    }

    private static ProfileComposer Composer() => new(Profiles, key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN"
        ? "isolated-test-placeholder" : null);

    private static readonly string[] NativeRows =
    [
        "relay-listener", "dialect.match", "dialect.match-offers", "dialect.match-replies", "match-connection", "dialect.scene-seeds",
    ];
}
