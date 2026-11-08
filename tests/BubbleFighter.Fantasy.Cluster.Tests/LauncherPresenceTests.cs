using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Cluster;
using BubbleFighter.Fantasy.Contracts.Launcher;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

public sealed class LauncherPresenceTests
{
    private const string Domain = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task ExplicitMembersExcludeOtherSelectorsAndDeduplicateAccountsAcrossNodesAndBuilds()
    {
        using var fixture = new Fixture();
        var directory = new ServerDirectory(fixture.Options);
        Report(directory, "a", [new(1, "kr-1256", "Outgoing", 2, true)]);
        Report(directory, "b", [new(1, "cn-406", "Current", 3), new(2, "kr-1256", null, null)]);
        Report(directory, "test", [new(3, "kr-1256", "Excluded", 3)]);
        Report(directory, "overseas", [new(4, "kr-1256", "Excluded", 3)]);
        var result = await directory.ReadAsync("formal", true, default);
        Assert.Equal(2, result.OnlinePlayers); Assert.Equal(2, result.PeakOnlinePlayers);
        Assert.True(result.Complete); Assert.False(result.RosterComplete);
        var player = Assert.Single(result.Players);
        Assert.Equal("Current", player.Name); Assert.Equal("cn-406", player.BuildId);
        Assert.Equal(3, directory.ListSelectionServers("kr-1256")[0].Targets.Count);
        Assert.Empty((await directory.ReadAsync("formal", false, default)).Players);
    }

    [Fact]
    public async Task ExpiryAndWrongDomainsArePartialButCannotEraseThePersistedPeak()
    {
        using var fixture = new Fixture(); var clock = new Clock();
        var directory = new ServerDirectory(fixture.Options, clock);
        Report(directory, "a", [new(1, "kr-1256", "One", 1)]);
        Report(directory, "b", [new(2, "kr-1256", "Two", 1)]);
        clock.Advance(16);
        var expired = await directory.ReadAsync("formal", true, default);
        Assert.Equal(0, expired.OnlinePlayers); Assert.Equal(2, expired.PeakOnlinePlayers);
        Assert.False(expired.Complete);
        Report(directory, "a", [new(1, "kr-1256", "Wrong", 1)], new string('b', 32));
        Assert.Equal(0, (await directory.ReadAsync("formal", true, default)).OnlinePlayers);
        var restored = new ServerDirectory(fixture.Options, clock);
        Assert.Equal(2, (await restored.ReadAsync("formal", false, default)).PeakOnlinePlayers);
        Assert.DoesNotContain("One", File.ReadAllText(fixture.Path));
    }

    [Fact]
    public async Task OldNodeReportsRemainAcceptedAndExplicitlyMarkThePopulationIncomplete()
    {
        using var fixture = new Fixture(); var directory = new ServerDirectory(fixture.Options);
        Report(directory, "a", null);
        var snapshot = await directory.ReadAsync("formal", true, default);
        Assert.False(snapshot.Complete); Assert.Equal(0, snapshot.OnlinePlayers);
        var old = new NodeHeartbeat("token", 1, 1, false, Domain, [], []);
        Assert.DoesNotContain("presence", JsonSerializer.Serialize(old, JsonSerializerOptions.Web));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("build")]
    [InlineData("name")]
    [InlineData("level")]
    [InlineData("bound")]
    public void InvalidPrivateReportsAreRejectedBeforeReplacingAcceptedPresence(string defect)
    {
        using var fixture = new Fixture(); var directory = new ServerDirectory(fixture.Options);
        var grant = directory.Register("a", Guid.NewGuid().ToString("N"))!;
        LauncherPresenceMember[] players = defect switch
        {
            "duplicate" => [new(1, "kr-1256", "One", 1), new(1, "cn-406", "Two", 1)],
            "build" => [new(1, "unknown", "One", 1)],
            "name" => [new(1, "kr-1256", "bad\nname", 1)],
            "level" => [new(1, "kr-1256", null, 1)],
            _ => Enumerable.Range(1, 5).Select(id => new LauncherPresenceMember(id, "kr-1256", "Name", 1)).ToArray(),
        };
        fixture.Options.MaxPresencePlayersPerNode = 4;
        Assert.False(directory.Observe("a", new(grant.Token, grant.Epoch, 1, false, Domain,
            [new("kr-1256", true, 1), new("cn-406", true, 0)], []) { Presence = players }));
    }

    [Fact]
    public void CheckpointRejectsMembershipChangesAndMalformedStateInsteadOfResettingHistory()
    {
        using var fixture = new Fixture();
        var directory = new ServerDirectory(fixture.Options);
        Report(directory, "a", [new(1, "kr-1256", "One", 1)]);
        fixture.Options.PresenceGroups[0].Nodes = ["a"];
        Assert.Throws<InvalidDataException>(() => new ServerDirectory(fixture.Options));
        File.WriteAllText(fixture.Path, "{bad");
        Assert.Throws<JsonException>(() => new ServerDirectory(fixture.Options));
    }

    [Fact]
    public async Task AuthenticatedClientGetsOnlyTheConfiguredGroupAndPublicCards()
    {
        using var fixture = new Fixture(); fixture.Options.Listen = "127.0.0.1:0";
        var directory = new ServerDirectory(fixture.Options);
        Report(directory, "a", [new(1, "kr-1256", "One", 1)]); Report(directory, "b", []);
        await using var server = await DirectoryServer.StartAsync(fixture.Options, directory, default);
        var options = new LauncherPresenceClientOptions { NodeId = "a", Group = "formal",
            DirectoryUrl = server.Addresses.Single(), AccessKey = fixture.Options.Nodes[0].HeartbeatKey };
        using var http = new HttpClient { BaseAddress = new Uri(options.DirectoryUrl) };
        var client = new LauncherPresenceClient(options, http);
        Assert.Empty((await client.ReadAsync("formal", false, default)).Players);
        Assert.Equal("One", Assert.Single((await client.ReadAsync("formal", true, default)).Players).Name);
        using var unauthenticated = await http.GetAsync("/v1/nodes/a/presence/formal");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Options.Nodes[2].HeartbeatKey);
        using var excluded = await http.GetAsync("/v1/nodes/test/presence/formal");
        Assert.Equal(HttpStatusCode.NotFound, excluded.StatusCode);
    }

    [Theory]
    [InlineData("{\"onlinePlayers\":-1,\"peakOnlinePlayers\":1,\"complete\":true,\"rosterComplete\":false,\"players\":[]}")]
    [InlineData("{\"onlinePlayers\":2,\"peakOnlinePlayers\":1,\"complete\":true,\"rosterComplete\":false,\"players\":[]}")]
    [InlineData("{\"onlinePlayers\":0,\"peakOnlinePlayers\":0,\"complete\":true,\"rosterComplete\":false,\"players\":[],\"accountNumber\":123}")]
    [InlineData("{\"onlinePlayers\":1,\"peakOnlinePlayers\":1,\"complete\":true,\"rosterComplete\":false,\"players\":[{\"name\":\"One\",\"level\":1,\"buildId\":\"kr-1256\"}]}")]
    public async Task RemoteCountsRejectMalformedOrLeakingSnapshots(string payload)
    {
        var options = new LauncherPresenceClientOptions { Group = "formal", NodeId = "a", AccessKey = new string('x', 32) };
        using var http = new HttpClient(new Reply(payload)) { BaseAddress = new Uri("http://example.invalid") };
        await Assert.ThrowsAsync<InvalidDataException>(() => new LauncherPresenceClient(options, http)
            .ReadAsync("formal", false, default).AsTask());
    }

    [Fact]
    public async Task RealHeartbeatPublishesPrivatePresenceAndUnloadRemovesIt()
    {
        using var fixture = new Fixture(); fixture.Options.Listen = "127.0.0.1:0";
        var directory = new ServerDirectory(fixture.Options); Report(directory, "b", []);
        await using var server = await DirectoryServer.StartAsync(fixture.Options, directory, default);
        var host = new PluginHost([
            new() { Id = "sessions", Plugin = "core.session-registry", Source = "test" },
            new() { Id = "domain", Plugin = "test.cluster-domain", Source = "test" },
            new() { Id = "presence", Plugin = "test.launcher-presence", Source = "test" },
            new() { Id = "node", Plugin = "cluster.node", Source = "test", Config = new Dictionary<string, object?>
            {
                ["node_id"] = "a", ["directory_url"] = server.Addresses.Single(),
                ["heartbeat_key"] = fixture.Options.Nodes[0].HeartbeatKey, ["builds"] = new[] { "kr-1256" },
                ["heartbeat_seconds"] = 1, ["request_timeout_seconds"] = 1, ["report_launcher_presence"] = true,
            } },
        ], PluginCatalog.ScanAssemblies([typeof(ClusterNodePlugin).Assembly, typeof(SessionRegistryPlugin).Assembly,
            typeof(LauncherPresenceTests).Assembly]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(default); host.MarkReady();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((await directory.ReadAsync("formal", true, deadline.Token)).OnlinePlayers == 0)
                await Task.Delay(25, deadline.Token);
            Assert.Equal("Reported", Assert.Single((await directory.ReadAsync("formal", true, default)).Players).Name);
        }
        finally { await host.ShutdownAsync(default); }
        Assert.Equal(0, (await directory.ReadAsync("formal", false, default)).OnlinePlayers);
        Assert.All(host.Instances, instance => Assert.Equal(PluginState.Unloaded, instance.State));
    }

    [Plugin("test.launcher-presence", Provides = [typeof(ILocalLauncherPresence)])]
    public sealed class ReportedPresence : IPlugin, ILocalLauncherPresence
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Register<ILocalLauncherPresence>(this));
            return ValueTask.CompletedTask;
        }
        public IReadOnlyList<LauncherPresenceMember> Capture(bool includeRoster) => [new(7, "kr-1256", "Reported", 3)];
    }

    private sealed class Reply(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json") });
    }

    private static void Report(ServerDirectory directory, string node, LauncherPresenceMember[]? players, string domain = Domain)
    {
        var grant = directory.Register(node, Guid.NewGuid().ToString("N"))!;
        Assert.True(directory.Observe(node, new(grant.Token, grant.Epoch, 1, false, domain,
            [new("kr-1256", true, 1), new("cn-406", true, 1)], []) { Presence = players }));
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        internal void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "presence-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(_directory, "peak.json");
        internal DirectoryOptions Options { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            Options = ServerDirectoryTests.Options([.. new[] { "a", "b", "test", "overseas" }.Select(id =>
            {
                var node = ServerDirectoryTests.Node(id, "formal", []);
                node.Endpoints = [node.Endpoints[0], new() { Build = "cn-406", Host = id + ".example", Port = 7230, Capacity = 8 }];
                return node;
            })]);
            Options.Selectors[0].Nodes = ["a", "b", "overseas"];
            Options.PresenceGroups = [new() { Id = "formal", Nodes = ["a", "b"], PersistentDomainId = Domain, PeakStatePath = Path }];
            Assert.Empty(Options.Validate());
        }
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
