using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.MatchConnection.Tests;

/// <summary>Checks that optional native peer setup includes the relay leases it needs at runtime.</summary>
public sealed class UdpProfileCompositionTests
{
    private static readonly string[] RelayRows =
        ["relay-listener", "dialect.match", "dialect.match-offers", "dialect.match-replies", "match-connection"];

    [Theory]
    [InlineData("cn-279")]
    [InlineData("cn-324")]
    [InlineData("cn-406")]
    [InlineData("id-400")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    public void OptionalPeerPatchEnablesAnAdvertisedRelayAndOwnBuildDatagramScope(string build)
    {
        var root = RepositoryRoot();
        var rows = Composer(root).Compose(build, [Path.Combine(root, "profiles", "bundles", "udp-peers.patch.yaml")])
            .ToDictionary(row => row.Id);
        foreach (var id in RelayRows)
            Assert.False(rows[id].Disabled);
        Assert.True(Convert.ToBoolean(rows["ingame"].Config!["native_match_relay"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(Convert.ToBoolean(rows["ingame"].Config!["match_transport_fallback"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(Convert.ToBoolean(rows["dialect.rooms"].Config!["select_match_relay"], System.Globalization.CultureInfo.InvariantCulture));
        var listen = (string)rows["relay-listener"].Config!["listen"]!;
        Assert.Equal(int.Parse(listen[(listen.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(rows["ingame"].Config!["relay_advertise_port"], System.Globalization.CultureInfo.InvariantCulture));
        foreach (var id in new[] { "udp-listener", "dialect.udp-peers", "match-udp-peers" })
        {
            Assert.False(rows[id].Disabled);
            Assert.Equal(rows["ingame"].ClientBuild, rows[id].ClientBuild);
        }
        Assert.Equal(build + "/udp", rows["udp-listener"].Config!["protocol"]);
        Assert.Equal(build + "/game", rows["match-udp-peers"].Config!["protocol"]);
        Assert.Equal(build + "/udp", rows["match-udp-peers"].Config!["udp_protocol"]);
    }

    [Theory]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    public void DefenceOnlyBuildsKeepAuthorityDormantWhenPeerSetupIsEnabled(string build)
    {
        var root = RepositoryRoot();
        var defaults = Composer(root).Compose(build, []).ToDictionary(row => row.Id);
        foreach (var id in RelayRows)
            Assert.True(defaults[id].Disabled);
        Assert.False(Convert.ToBoolean(defaults["ingame"].Config!["native_match_relay"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(Convert.ToBoolean(defaults["dialect.rooms"].Config!["select_match_relay"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.DoesNotContain("udp-listener", defaults.Keys);
        Assert.True(defaults["dialect.scene-seeds"].Disabled);
        Assert.Equal("false", defaults["dialect.scene-seeds"].Config!["defence_seeds"]);

        var enabled = Composer(root).Compose(build, [Path.Combine(root, "profiles", "bundles", "udp-peers.patch.yaml")]);
        var authority = Assert.Single(enabled, row => row.Id == "dialect.scene-seeds");
        Assert.True(authority.Disabled);
        Assert.Equal("false", authority.Config!["defence_seeds"]);
    }

    [Theory]
    [InlineData("kr-812", "udp-relay.patch.yaml", true)]
    [InlineData("kr-1256", "udp-relay.patch.yaml", false)]
    [InlineData("cn-324", "udp-peers.patch.yaml", true)]
    [InlineData("cn-279", "udp-peers.patch.yaml", false)]
    public void DeploymentSwitchReachesEveryUdpConsumer(string build, string patch, bool enabled)
    {
        var root = RepositoryRoot();
        var composer = new ProfileComposer(Path.Combine(root, "profiles"), key => key switch
        {
            "BFF_UDP_PORT" => "7633", "BFF_UDP_ENABLED" => enabled ? "true" : "false", _ => null,
        }, unsetReadsEmpty: true);
        var rows = composer.Compose(build, [Path.Combine(root, "profiles", "bundles", patch)]).ToDictionary(row => row.Id);
        foreach (var (row, field) in patch == "udp-relay.patch.yaml"
            ? new[] { ("udp-listener", "enabled"), ("match-udp-relay-setup", "enabled"), ("dialect.rooms", "udp_enabled"),
                ("dialect.match-replies", "udp_enabled"), ("ingame", "udp_enabled") }
            : new[] { ("udp-listener", "enabled"), ("match-udp-peers", "enabled"), ("ingame", "udp_enabled") })
            Assert.Equal(enabled, Convert.ToBoolean(rows[row].Config![field], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static ProfileComposer Composer(string root) => new(Path.Combine(root, "profiles"),
        key => key == "BFF_UDP_PORT" ? "7633" : null, unsetReadsEmpty: true);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln")))
                return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
