using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Cluster;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Lobby;
using BubbleFighter.Fantasy.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks that the shipped migration bundles compose onto a real build and bind to their plugins' options.</summary>
public sealed class MigrationBundleCompositionTests
{
    private static readonly string[] Bundles =
    [
        "cluster-node.patch.yaml", "cluster-room-directory.patch.yaml", "cluster-account-store.patch.yaml",
        "cluster-account-sessions.patch.yaml", "cluster-account-write-fencing.patch.yaml",
        "cluster-migration.patch.yaml", "cluster-migration-build.patch.yaml",
    ];

    // Each build's audited channel set; the evidence for every row is listed in cluster-migration-build.patch.yaml.
    [Theory]
    [InlineData("kr-812", 60, new[] { 50, 60, 80 })]
    [InlineData("id-1809", 60, new[] { 50, 60, 80 })]
    [InlineData("cn-717", 60, new[] { 50, 60, 80 })]
    [InlineData("cn-706", 60, new[] { 50, 60, 80 })]
    [InlineData("cn-404", 60, new[] { 50, 60, 80 })]
    [InlineData("cn-408", 60, new[] { 50, 60, 80 })]
    [InlineData("cn-324", 60, new[] { 50, 60 })]
    [InlineData("id-400", 60, new[] { 50, 60 })]
    [InlineData("cn-406", 60, new[] { 50, 60 })]
    [InlineData("cn-279", 60, new[] { 50, 60 })]
    public void LegacyMigrationStackComposesAndEveryRowBindsItsOptions(string build, int channel, int[] channels)
    {
        var patch = ChannelPatch(channel, channels);
        Dictionary<string, PluginRow> rows;
        try { rows = Compose(build, patch); }
        finally { File.Delete(patch); }

        Bind<RoomBrowserOptions>(rows["cluster-room-browser"]);
        var source = Bind<MigrationSourceOptions>(rows["cluster-migration-source"]);
        var admission = Bind<MigrationAdmissionOptions>(rows["cluster-migration-admission"]);
        Bind<MigrationBrokerOptions>(rows["cluster-migration-broker"]);
        Bind<MigrationArrivalOptions>(rows["cluster-migration-arrivals"]);
        var destination = Bind<MigrationDestinationOptions>(rows["cluster-migration-destination"]);
        Bind<AccountSessionOptions>(rows["cluster-account-sessions"]);
        var lobby = Bind<LobbyOptions>(rows["lobby"]);
        var messages = Bind<MessagesOptions>(rows["dialect.messages"]);
        var ingame = Bind<InGameOptions>(rows["ingame"]);

        Assert.Equal(build, source.ClientBuild);
        Assert.Equal(channels, admission.Channels);
        Assert.Contains(source.Channel, admission.Channels);
        Assert.Equal("game-listener", Assert.Single(destination.Endpoints).ListenerId);
        Assert.Contains(rows.Keys, id => id == "game-listener");
        // The source needs the build's own online players and parties, which only its subtree can see.
        Assert.Equal("inventoryview", rows["cluster-migration-source"].ParentId);
        Assert.Equal("inventoryview", rows["cluster-migration-admission"].ParentId);
        Assert.Contains("BubbleFighter.Fantasy.Contracts.Players.IOnlinePlayers", rows["inventoryview"].IsolatedServices);
        Assert.Contains("BubbleFighter.Fantasy.Contracts.Party.IPartyDirectory", rows["inventoryview"].IsolatedServices);
        // The destination accepts handoffs only from a node-bound ownership store.
        Assert.Equal("a", rows["cluster-account-store"].Config!["node_id"]);
        Assert.True(lobby.NativeMigration && lobby.RequireAccountOwnership);
        Assert.True(messages.NativeMigration);
        Assert.True(ingame.RoomAdmissionReservations && ingame.HomeMigrationAdmission);
    }

    /// <summary>
    /// Every playable build gets its destination endpoint and bootstrap switches from the shipped bundles alone:
    /// the endpoint names the profile's own build and public game port, and the protocol row accepts retained
    /// moves with the build's own auxiliary prefix, so a node needs no hand-written protocol lists.
    /// </summary>
    [Theory]
    [InlineData("kr-812", 7110, new string[0])]
    [InlineData("id-400", 7120, new string[0])]
    [InlineData("id-1809", 7130, new string[0])]
    [InlineData("cn-717", 7150, new string[0])]
    [InlineData("cn-706", 7220, new string[0])]
    [InlineData("cn-404", 7240, new string[0])]
    [InlineData("cn-408", 7250, new string[0])]
    [InlineData("cn-705", 7260, new string[0])]
    [InlineData("cn-279", 7210, new[] { "gcpStartGPK" })]
    [InlineData("cn-324", 7140, new[] { "gcpStartGPK" })]
    [InlineData("cn-406", 7230, new[] { "gcpStartGPK" })]
    [InlineData("kr-1256", 7100, new string[0])]
    [InlineData("cn-774", 7190, new string[0])]
    [InlineData("tw-454", 7200, new string[0])]
    public void EveryPlayableBuildAdvertisesItsOwnEndpointAndAcceptsRetainedMoves(string build, int port, string[] prefix)
    {
        var patch = ChannelPatch(60, [60]);
        Dictionary<string, PluginRow> rows;
        try { rows = Compose(build, patch); }
        finally { File.Delete(patch); }

        var endpoint = Assert.Single(Bind<MigrationDestinationOptions>(rows["cluster-migration-destination"]).Endpoints);
        Assert.Equal(build, endpoint.ClientBuild);
        Assert.Equal(port, endpoint.Port);
        Assert.Equal("example.test", endpoint.Host);
        var protocol = Bind<ProtocolOptions>(rows["protocol"]);
        Assert.True(protocol.AcceptRetainedMoves);
        Assert.Equal(prefix, protocol.AuxiliaryPrefix);
        Assert.DoesNotContain("gcpReqMoveInto", protocol.PreHandshakeOpcodes);
    }

    /// <summary>Checks every multi-build listener, public port and isolated migration subtree without enabling standalone moves.</summary>
    [Fact]
    public void AllBuildsKeepMigrationAdmissionInTheirOwnPlayerSubtree()
    {
        var root = RepositoryRoot();
        var temporary = Path.Combine(Path.GetTempPath(), $"migration-all-{Guid.NewGuid():N}");
        var profiles = Path.Combine(temporary, "profiles");
        Directory.CreateDirectory(profiles);
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "profiles"), "*.yaml", SearchOption.AllDirectories))
            {
                if (file.EndsWith("local.patch.yaml", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith("deployment.patch.yaml", StringComparison.OrdinalIgnoreCase)) continue;
                var destination = Path.Combine(profiles, Path.GetRelativePath(Path.Combine(root, "profiles"), file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            var composer = Composer(profiles);
            var builds = composer.Compose("all", []).Where(row => row.Id.EndsWith(".lobby", StringComparison.Ordinal))
                .Select(row => row.Id[..^".lobby".Length]).ToArray();
            Assert.Equal(15, builds.Length);
            // Builds whose native move is not audited stay out of the node-wide migration rows; kr-186 has no
            // server list at all, and tw-410's move reply is not yet checked against its own channel table.
            string[] unaudited = ["kr-186", "tw-410"];
            Assert.All(unaudited, build => Assert.Contains(build, builds));
            builds = [.. builds.Except(unaudited)];
            Assert.DoesNotContain(composer.Compose("all", []), row => row.Plugin.StartsWith("cluster.migration-", StringComparison.Ordinal));
            var channel = ChannelPatch(60, [60]);
            try
            {
                foreach (var build in builds)
                    File.WriteAllText(Path.Combine(profiles, build, "local.patch.yaml"),
                        File.ReadAllText(Path.Combine(profiles, "bundles", "cluster-migration-build.patch.yaml"))
                        + Environment.NewLine + File.ReadAllText(channel));
            }
            finally { File.Delete(channel); }

            var rows = composer.Compose("all", [
                .. Bundles.Take(5).Select(name => Path.Combine(profiles, "bundles", name)),
                Path.Combine(profiles, "all", "migration.patch.yaml"),
            ]).ToDictionary(row => row.Id);
            var endpoints = Bind<MigrationDestinationOptions>(rows["cluster-migration-destination"]).Endpoints;
            Assert.Equal(builds.Order(), endpoints.Select(endpoint => endpoint.ClientBuild).Order());
            foreach (var endpoint in endpoints)
            {
                var prefix = endpoint.ClientBuild + ".";
                Assert.Equal(prefix + "game-listener", endpoint.ListenerId);
                Assert.Contains(endpoint.ListenerId, rows.Keys);
                Assert.Equal("example.test", endpoint.Host);
                var single = composer.Compose(endpoint.ClientBuild, [
                    .. Bundles.Take(6).Select(name => Path.Combine(profiles, "bundles", name)),
                ]).ToDictionary(row => row.Id);
                Assert.Equal(Assert.Single(Bind<MigrationDestinationOptions>(single["cluster-migration-destination"]).Endpoints).Port,
                    endpoint.Port);
                Assert.Equal("a", rows["cluster-account-store"].Config!["node_id"]);
                Assert.Equal(prefix + "inventoryview", rows[prefix + "cluster-migration-source"].ParentId);
                Assert.Equal(prefix + "inventoryview", rows[prefix + "cluster-migration-admission"].ParentId);
                Assert.Equal(prefix + "inventoryview", rows[prefix + "lobby"].ParentId);
                Assert.Contains("BubbleFighter.Fantasy.Contracts.Lobby.IRetainedPlayerAdmission", rows[prefix + "inventoryview"].IsolatedServices);
                var source = Bind<MigrationSourceOptions>(rows[prefix + "cluster-migration-source"]);
                Assert.Equal(endpoint.ClientBuild, source.ClientBuild);
                Assert.Equal(BubbleFighter.Fantasy.Packets.PacketTables.Load(Path.Combine(root, "packets"))
                    .CapabilityOf(endpoint.ClientBuild, "server_notice"), source.HomeUnavailableNoticeEnabled);
                Assert.Equal(60, source.Channel);
                Assert.Equal(new[] { 60 }, Bind<MigrationAdmissionOptions>(rows[prefix + "cluster-migration-admission"]).Channels);
                var lobby = Bind<LobbyOptions>(rows[prefix + "lobby"]);
                Assert.True(lobby.NativeMigration && lobby.RequireAccountOwnership);
                Assert.Equal("reject_new", lobby.DuplicateLogin);
                Assert.True(Bind<ProtocolOptions>(rows[prefix + "protocol"]).AcceptRetainedMoves);
                Assert.True(Bind<MessagesOptions>(rows[prefix + "dialect.messages"]).NativeMigration);
            }
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    [Fact]
    public void TheBuildBundleAloneRefusesToGuessChannels()
    {
        var rows = Compose("cn-324");
        Assert.Throws<ConfigException>(() => Bind<MigrationSourceOptions>(rows["cluster-migration-source"]));
        Assert.Throws<ConfigException>(() => Bind<MigrationAdmissionOptions>(rows["cluster-migration-admission"]));
    }

    private static Dictionary<string, PluginRow> Compose(string build, params string[] extra)
    {
        var root = RepositoryRoot();
        var composer = Composer(Path.Combine(root, "profiles"));
        return composer.Compose(build, [.. Bundles.Select(name => Path.Combine(root, "profiles", "bundles", name)), .. extra])
            .ToDictionary(row => row.Id);
    }

    private static ProfileComposer Composer(string profiles) => new(profiles, key => key switch
        {
            "BFF_CLUSTER_NODE_ID" => "a",
            "BFF_CLUSTER_DIRECTORY_URL" => "http://127.0.0.1:18193",
            "BFF_CLUSTER_NODE_KEY" => new string('k', 40),
            "BFF_CLUSTER_ACCOUNT_CONNECTION_STRING" => "Server=127.0.0.1;Port=1",
            "BFF_CLUSTER_DATA_DOMAIN_ID" => "0123456789abcdef0123456789abcdef",
            "BFF_PUBLIC_DOMAIN" => "example.test",
            _ => null,
        }, unsetReadsEmpty: true);

    private static string ChannelPatch(int channel, int[] channels)
    {
        var path = Path.Combine(Path.GetTempPath(), $"migration-channels-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, $"""
            - set:
                id: cluster-migration-source
                config:
                  channel: {channel}
            - set:
                id: cluster-migration-admission
                config:
                  channels: [{string.Join(", ", channels)}]
            """);
        return path;
    }

    private static T Bind<T>(PluginRow row) where T : class, new() => new PluginConfig(row).Bind<T>();

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BubbleFighter.Fantasy.sln")))
                return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
