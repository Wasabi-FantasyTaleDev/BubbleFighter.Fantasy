using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Listeners;
using System.Net;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Cluster.Tests;

public sealed class NodeLifecycleTests
{
    [Fact]
    public async Task IndependentDirectoryNodeDoesNotRequireASharedDatabaseProvider()
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "", []));
        options.Selectors = [options.Selectors[1]];
        options.Listen = "127.0.0.1:0";
        Assert.Empty(options.Validate());
        var directory = new ServerDirectory(options);
        await using var server = await DirectoryServer.StartAsync(options, directory, default);
        var host = new PluginHost([
            new() { Id = "sessions", Plugin = "core.session-registry", Source = "test" },
            new() { Id = "rooms", Plugin = "test.cluster-rooms", Source = "test" },
            new() { Id = "node", Plugin = "cluster.node", Source = "test", Config = new Dictionary<string, object?>()
            {
                ["node_id"] = "a", ["directory_url"] = server.Addresses.Single(),
                ["heartbeat_key"] = options.Nodes[0].HeartbeatKey, ["builds"] = new[] { "kr-1256" },
                ["heartbeat_seconds"] = 1, ["request_timeout_seconds"] = 1,
            } },
        ], PluginCatalog.ScanAssemblies([typeof(ClusterNodePlugin).Assembly, typeof(SessionRegistryPlugin).Assembly,
            typeof(NodeLifecycleTests).Assembly]), NullLoggerFactory.Instance);
        try
        {
            await host.StartAsync(default);
            host.MarkReady();
            await UntilAsync(() => directory.ListServers("kr-1256")[0].Targets[0].Online);
            Assert.All(host.Instances, row => Assert.Equal(PluginState.Active, row.State));
        }
        finally { await host.ShutdownAsync(default); }
        Assert.False(directory.ListServers("kr-1256")[0].Targets[0].Online);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task RealHostReportsReadinessAndMatchRoomsThenReleasesLeaseOnUnload(bool destination, bool handoff)
    {
        var options = ServerDirectoryTests.Options(ServerDirectoryTests.Node("a", "formal", ["friends"]));
        options.Listen = "127.0.0.1:0";
        var directory = new ServerDirectory(options);
        await using var server = await DirectoryServer.StartAsync(options, directory, default);
        var host = new PluginHost([
            new() { Id = "sessions", Plugin = "core.session-registry", Source = "test" },
            new() { Id = "domain", Plugin = "test.cluster-domain", Source = "test" },
            new() { Id = "rooms", Plugin = "test.cluster-rooms", Source = "test" },
            new() { Id = "broker", Plugin = "cluster.migration-broker", Source = "test", Config = new Dictionary<string, object?>()
            {
                ["node_id"] = "a", ["directory_url"] = server.Addresses.Single(), ["access_key"] = options.Nodes[0].HeartbeatKey,
            } },
            new() { Id = "arrivals", Plugin = "cluster.migration-arrivals", Source = "test" },
            new() { Id = "handoff", Plugin = "test.destination-handoff", Source = "test", Disabled = !handoff },
            new() { Id = "receiver", Plugin = destination ? "cluster.migration-destination" : "test.migration-receiver", Source = "test",
                Config = destination ? new Dictionary<string, object?>
                {
                    ["node_id"] = "a", ["endpoints"] = new[] { new Dictionary<string, object?>
                    { ["client_build"] = "kr-1256", ["listener_id"] = "test-game-listener", ["host"] = "game.example", ["port"] = 7100 } },
                } : new Dictionary<string, object?>() },
            new() { Id = "node", Plugin = "cluster.node", Source = "test", Config = new Dictionary<string, object?>()
            {
                ["node_id"] = "a", ["directory_url"] = server.Addresses.Single(),
                ["heartbeat_key"] = options.Nodes[0].HeartbeatKey, ["builds"] = new[] { "kr-1256" },
                ["heartbeat_seconds"] = 1, ["request_timeout_seconds"] = 1,
            } },
        ], PluginCatalog.ScanAssemblies([typeof(ClusterNodePlugin).Assembly, typeof(SessionRegistryPlugin).Assembly,
            typeof(NodeLifecycleTests).Assembly]), NullLoggerFactory.Instance);
        try
        {
            if (!handoff)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(default).AsTask());
                Assert.IsType<ConfigException>(error.InnerException);
                Assert.Contains("account-session handoff", error.Message, StringComparison.Ordinal);
                Assert.Equal(PluginState.Failed, host.Instances.Single(row => row.RowId == "receiver").State);
                return;
            }
            await host.StartAsync(default);
            Assert.All(host.Instances, row => Assert.Equal(PluginState.Active, row.State));
            Assert.Empty(directory.ListServers("kr-1256")[0].Targets);
            host.MarkReady();
            await UntilAsync(() => directory.ListServers("kr-1256")[0].Targets.Count == 1);
            var room = Assert.Single(directory.ListRooms("a", "kr-1256"));
            Assert.Equal(1, room.RoomId);
            Assert.True(room.Locked);
            Assert.Equal("Match", room.Title);
            Assert.Equal(Rooms.InstanceId, room.InstanceId);
            var listing = Assert.IsType<RoomListEntry>(room.Listing);
            Assert.Equal("Host", listing.MasterNickname);
            Assert.Equal((ushort)3, listing.ModeId);
            Assert.Equal(new byte[] { 1, 2, 3 }, listing.BuildSettings!.Bytes.ToArray());
            Assert.Empty(listing.BuildSettings.SessionPrefix.ToArray());
            Assert.Equal(0, directory.ListServers("kr-1256")[0].Targets[0].Players);
            await host.ReloadAsync("node", default);
            await UntilAsync(() => directory.ListServers("kr-1256")[0].Targets.Count == 1);
            await host.ReloadAsync("receiver", default);
            Assert.All(host.Instances, row => Assert.Equal(PluginState.Active, row.State));
        }
        finally { await host.ShutdownAsync(default); }
        Assert.All(host.Instances, row => Assert.Equal(PluginState.Unloaded, row.State));
        Assert.Empty(directory.ListServers("kr-1256")[0].Targets);
        Assert.Empty(directory.ListRooms("a", "kr-1256"));
    }

    [Plugin("test.cluster-domain", Provides = [typeof(IPersistentDataDomainIdentity)])]
    public sealed class DataDomain : IPlugin, IPersistentDataDomainIdentity
    {
        /// <inheritdoc />
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Register<IPersistentDataDomainIdentity>(this));
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ValueTask<string> ReadAsync(CancellationToken ct) => ValueTask.FromResult("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    }

    [Plugin("test.migration-receiver", Requires = [typeof(IMigrationBroker)])]
    public sealed class MigrationReceiver : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Get<IMigrationBroker>().Serve(
                (_, _) => ValueTask.FromResult(new MigrationOffer(null, 0, null, TimeSpan.Zero)),
                (_, _) => ValueTask.CompletedTask));
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("test.destination-handoff", Provides = [typeof(IAccountSessionHandoff)])]
    public sealed class DestinationHandoff : IPlugin, IAccountSessionHandoff
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Register<IAccountSessionHandoff>(this));
            return ValueTask.CompletedTask;
        }
        public ValueTask HandOffAsync(BubbleFighter.Fantasy.Abstractions.Protocol.ISession session, AccountHandoff handoff, CancellationToken ct) =>
            throw new InvalidOperationException("No game socket is migrated by the lifecycle fixture.");
        public ValueTask<bool> TryWithdrawAsync(BubbleFighter.Fantasy.Abstractions.Protocol.ISession session, string id, CancellationToken ct) =>
            throw new InvalidOperationException("No game socket is migrated by the lifecycle fixture.");
        public ValueTask<bool> TryClaimAsync(BubbleFighter.Fantasy.Abstractions.Protocol.ISession session, long userNum, string id, CancellationToken ct) =>
            throw new InvalidOperationException("No game socket is migrated by the lifecycle fixture.");
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(25, deadline.Token);
    }

    [Plugin("test.cluster-rooms", ProvidesMany = [typeof(IRoomAdminDirectory), typeof(IRoomPeerRoster), typeof(IPublishedRoomDirectory), typeof(IListenerEndpoint)])]
    public sealed class Rooms : IPlugin, IRoomAdminDirectory, IRoomPeerRoster, IPublishedRoomDirectory, IListenerEndpoint
    {
        public static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public string ClientBuild => "kr-1256";
        public string Protocol => "kr-1256/game";
        public string RowId => "test-game-listener";
        public IPEndPoint LocalEndPoint => new(IPAddress.Loopback, 7100);
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.RegisterMany<IRoomAdminDirectory>(this));
            context.Effect(context.Services.RegisterMany<IRoomPeerRoster>(this));
            context.Effect(context.Services.RegisterMany<IPublishedRoomDirectory>(this));
            context.Effect(context.Services.RegisterMany<IListenerEndpoint>(this));
            return ValueTask.CompletedTask;
        }
        public ValueTask<IReadOnlyList<AdminRoomSnapshot>> ListAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AdminRoomSnapshot>>([
                new(1, "Match", ClientBuild, "normal", null, "Waiting", 1, 8, true, "private-password", "private-owner", []),
                new(2, "Plaza", ClientBuild, "plaza", null, "Waiting", 1, 8, false, null, null, []),
            ]);
        public ValueTask<IReadOnlyList<PublishedRoomSnapshot>> ListPublishedAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<PublishedRoomSnapshot>>([
                new(InstanceId, new(1, "Match", "Host", 3, 1, 8, true, new("kr-1256", new byte[] { 1, 2, 3 })), "map", "Waiting"),
            ]);
        public ValueTask<AdminRoomSnapshot?> GetAsync(int roomId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IReadOnlyList<RoomRosterView> Snapshot() =>
            [new(1, RoomStage.Waiting, 1, 0, [], false) { IsMatchRoom = true }, new(2, RoomStage.Waiting, 1, 0, [], false)];
        public RoomRosterView? Find(int roomId) => throw new NotSupportedException();
        public RoomRosterView? FindBySession(long sessionId) => throw new NotSupportedException();
        public ValueTask<bool> ExecuteAsync(int roomId, Func<RoomRosterView, CancellationToken, ValueTask> operation, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
