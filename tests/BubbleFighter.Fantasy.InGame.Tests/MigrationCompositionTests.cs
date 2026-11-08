using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Cluster;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed class MigrationCompositionTests
{
    [Theory]
    [InlineData("forward")]
    [InlineData("reverse")]
    [InlineData("missing-rooms")]
    [InlineData("duplicate-source")]
    public async Task RealNodeRoomsBrokerAndSourceValidateBindingsAndReloadTogether(string scenario)
    {
        PluginRow[] rows = [
            new() { Id = "packets", Plugin = "core.packet-router", Source = "test" },
            new() { Id = "messages", Plugin = "core.message-router", Source = "test" },
            new() { Id = "sessions", Plugin = "core.session-registry", Source = "test" },
            new() { Id = "fixtures", Plugin = "test.migration-composition", Source = "test" },
            new() { Id = "rooms", Plugin = "ingame", Source = "test" },
            new() { Id = "node", Plugin = "cluster.node", Source = "test", Config = new Dictionary<string, object?>
            {
                ["node_id"] = "a", ["directory_url"] = "http://127.0.0.1:1",
                ["heartbeat_key"] = new string('k', 32), ["builds"] = new[] { "kr-1256" },
            } },
            new() { Id = "broker", Plugin = "cluster.migration-broker", Source = "test", Config = new Dictionary<string, object?>
            {
                ["node_id"] = "a", ["directory_url"] = "http://127.0.0.1:1", ["access_key"] = new string('k', 32),
            } },
            new() { Id = "source", Plugin = "cluster.migration-source", Source = "test", Config = new Dictionary<string, object?>
            { ["client_build"] = "kr-1256", ["channel"] = 50 } },
        ];
        if (scenario == "reverse") Array.Reverse(rows);
        if (scenario == "missing-rooms") rows = rows.Where(row => row.Id != "rooms").ToArray();
        if (scenario == "duplicate-source") rows = [.. rows,
            new() { Id = "duplicate", Plugin = "cluster.migration-source", Source = "test",
                Config = new Dictionary<string, object?> { ["client_build"] = "kr-1256", ["channel"] = 50 } }];
        var host = new PluginHost(rows, PluginCatalog.ScanAssemblies([typeof(InGamePlugin).Assembly, typeof(ClusterNodePlugin).Assembly,
            typeof(PluginHost).Assembly, typeof(MigrationCompositionTests).Assembly]), NullLoggerFactory.Instance);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            if (scenario is "missing-rooms" or "duplicate-source")
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(limit.Token).AsTask());
                Assert.Contains(scenario == "missing-rooms" ? "exactly one room migration endpoint" : "already bound", error.ToString(), StringComparison.Ordinal);
                return;
            }
            await host.StartAsync(limit.Token);
            Assert.All(host.Instances, row => Assert.Equal(PluginState.Active, row.State));
            var sourceReload = await host.ReloadAsync("source", limit.Token);
            Assert.Equal(new[] { "source" }, sourceReload.ReloadedRowIds);
            Assert.All(host.Instances, row => Assert.Equal(PluginState.Active, row.State));
            var nodeReload = await host.ReloadAsync("node", limit.Token);
            Assert.DoesNotContain("rooms", nodeReload.ReloadedRowIds);
            Assert.Contains("source", nodeReload.ReloadedRowIds);
            Assert.All(host.Instances, row => Assert.Equal(PluginState.Active, row.State));
        }
        finally { await host.ShutdownAsync(default); }
        Assert.All(host.Instances, row => Assert.Equal(PluginState.Unloaded, row.State));
    }

    [Plugin("test.migration-composition", Provides = [typeof(IOnlinePlayers), typeof(IAccountSessionHandoff)],
        ProvidesMany = [typeof(IInboundMessageBinding<RoomCreateRequested>), typeof(IMessageSender<RoomEntered>),
            typeof(IMessageSender<RoomSeatUpdated>), typeof(IMessageSender<RoomSeatVacated>),
            typeof(IMessageSender<RoomMasterChanged>), typeof(IMessageSender<ChannelMoveOffered>), typeof(IMessageSender<ServerNotice>)])]
    public sealed class Fixtures : IPlugin, IOnlinePlayers, IAccountSessionHandoff
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Register<IOnlinePlayers>(this));
            context.Effect(context.Services.Register<IAccountSessionHandoff>(this));
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<RoomCreateRequested>>(new CreateBinding()));
            context.Effect(context.Services.RegisterMany<IMessageSender<RoomEntered>>(new Sender<RoomEntered>()));
            context.Effect(context.Services.RegisterMany<IMessageSender<RoomSeatUpdated>>(new Sender<RoomSeatUpdated>()));
            context.Effect(context.Services.RegisterMany<IMessageSender<RoomSeatVacated>>(new Sender<RoomSeatVacated>()));
            context.Effect(context.Services.RegisterMany<IMessageSender<RoomMasterChanged>>(new Sender<RoomMasterChanged>()));
            context.Effect(context.Services.RegisterMany<IMessageSender<ChannelMoveOffered>>(new Sender<ChannelMoveOffered>()));
            context.Effect(context.Services.RegisterMany<IMessageSender<ServerNotice>>(new Sender<ServerNotice>()));
            return ValueTask.CompletedTask;
        }

        public ISession? FindByUserNum(int userNum) => null;
        public ISession? FindByNickname(string nickname) => null;
        public ValueTask HandOffAsync(ISession session, AccountHandoff handoff, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> TryWithdrawAsync(ISession session, string migrationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> TryClaimAsync(ISession session, long userNum, string migrationId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CreateBinding : IInboundMessageBinding<RoomCreateRequested>
    {
        public string Protocol => "kr-1256/game";
    }

    private sealed class Sender<T> : IMessageSender<T> where T : IMessage
    {
        public string Protocol => "kr-1256/game";
        public ValueTask SendAsync(ISession session, T message, CancellationToken ct) => throw new NotSupportedException();
    }
}
