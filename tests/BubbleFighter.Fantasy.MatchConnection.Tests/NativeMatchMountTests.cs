using System.Collections.Concurrent;
using BubbleFighter.Fantasy.RoomCore;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.MatchConnection.Tests;

/// <summary>Exercises installed native lifecycle rows with independent protocol directories and no modern account directory.</summary>
public sealed class NativeMatchMountTests
{
    [Fact]
    public async Task SevenNativeRowsSelectTheirOwnSeatsAndUnloadIndependently()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var builds = new[] { "kr-812", "cn-717", "cn-324", "cn-279", "cn-406", "id-400", "id-1809" };
        var lengths = new[] { 20, 20, 18, 12, 20, 20, 20 };
        var entries = new List<(PluginContext Context, RoomRegistry Registry, RoomSeat Seat, Session Relay, Replies Replies)>();
        for (var index = 0; index < builds.Length; index++)
        {
            var build = builds[index];
            var (registry, seat) = fixture.AddRoom(build);
            fixture.Publish<IMatchCapabilityDirectory>(registry);
            fixture.Binding<MatchCapabilityAuthenticate>(registry.Protocol);
            var replies = new Replies(registry.Protocol);
            fixture.Publish<IMessageSender<MatchAuthenticationAccepted>>(replies);
            var report = build != "cn-279";
            if (report)
            {
                fixture.Binding<MatchSceneIdentityRegistered>(registry.Protocol);
                fixture.Publish<IMessageSender<MatchSceneAccepted>>(replies);
            }
            if (index == 0) fixture.Binding<MatchMapNamed>(registry.Protocol);
            var context = fixture.MatchRow(build, report);
            await new MatchConnectionPlugin().ActivateAsync(context, default);
            var ticket = await registry.IssueCapabilityAsync(seat.Game, lengths[index], default);
            var relay = new Session(10 + index, build);
            Assert.True(await fixture.Router.DispatchAsync(registry.Protocol, relay, new MatchCapabilityAuthenticate(ticket), default));
            Assert.Same(relay, seat.Relay);
            Assert.Equal(!report, seat.RelayInitialized);
            Assert.Single(replies.Sent);
            Assert.False(await fixture.Router.DispatchAsync(registry.Protocol, relay, new MatchAuthenticate(ticket, 7, "host"), default));
            Assert.False(await fixture.Router.DispatchAsync(registry.Protocol, relay, new MatchSceneRegistered(7, 1000000, "host"), default));
            if (report)
                Assert.True(await fixture.Router.DispatchAsync(registry.Protocol, relay, new MatchSceneIdentityRegistered(1000000, "host"), default));
            else
                Assert.False(await fixture.Router.DispatchAsync(registry.Protocol, relay, new MatchSceneIdentityRegistered(1000000, "host"), default));
            Assert.True(seat.RelayInitialized);
            Assert.Equal(report ? 2 : 1, replies.Sent.Count);
            Assert.Equal(index == 0, await fixture.Router.DispatchAsync(registry.Protocol, relay,
                new MatchMapNamed(1000000, "host", "rel_Village01_Byway"), default));
            entries.Add((context, registry, seat, relay, replies));
        }
        Assert.False(fixture.Services.TryGet<IMatchRoomDirectory>(out _));
        await entries[2].Context.UnwindAsync();
        Assert.True(entries[2].Relay.Connection.IsClosed);
        Assert.Null(entries[2].Seat.Relay);
        Assert.DoesNotContain(fixture.Router.Handlers, handler => handler.Protocol == "cn-324/relay");
        Assert.Same(entries[0].Relay, entries[0].Seat.Relay);
        Assert.False(entries[0].Relay.Connection.IsClosed);
        fixture.Events.Emit(new ConnectionClosedEvent(entries[4].Relay.Connection, "test closed"));
        Assert.Null(entries[4].Seat.Relay);
        Assert.False(entries[4].Registry.TryGetSessionOfConnection(entries[4].Relay.Connection.Id, out _));
        Assert.Same(entries[5].Relay, entries[5].Seat.Relay);
        foreach (var entry in entries) await entry.Context.UnwindAsync();
        Assert.Empty(fixture.Router.Handlers);
        Assert.All(entries, entry => Assert.Null(entry.Seat.Relay));
    }

    [Theory]
    [InlineData("directory-missing", "capability directory")]
    [InlineData("directory-wrong-scope", "capability directory")]
    [InlineData("directory-duplicate", "capability directory")]
    [InlineData("join-missing", "MatchCapabilityAuthenticate")]
    [InlineData("join-duplicate", "MatchCapabilityAuthenticate")]
    [InlineData("scene-missing", "MatchSceneIdentityRegistered")]
    [InlineData("acceptance-missing", "MatchAuthenticationAccepted")]
    [InlineData("scene-reply-missing", "MatchSceneAccepted")]
    [InlineData("acceptance-duplicate", "MatchAuthenticationAccepted")]
    [InlineData("map-duplicate", "map binding")]
    public async Task NativeLoadRejectsMissingAmbiguousOrWrongScopeServicesBeforeMountingRoutes(string fault, string expected)
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var (registry, _) = fixture.AddRoom("kr-812");
        if (fault == "directory-wrong-scope")
        {
            var (wrong, _) = fixture.AddRoom("cn-324");
            fixture.Publish<IMatchCapabilityDirectory>(wrong);
        }
        else if (fault != "directory-missing")
        {
            fixture.Publish<IMatchCapabilityDirectory>(registry);
            if (fault == "directory-duplicate") fixture.Publish<IMatchCapabilityDirectory>(registry);
        }
        if (fault != "join-missing") fixture.Binding<MatchCapabilityAuthenticate>(registry.Protocol);
        if (fault == "join-duplicate") fixture.Binding<MatchCapabilityAuthenticate>(registry.Protocol);
        if (fault != "scene-missing") fixture.Binding<MatchSceneIdentityRegistered>(registry.Protocol);
        var replies = new Replies(registry.Protocol);
        if (fault != "acceptance-missing") fixture.Publish<IMessageSender<MatchAuthenticationAccepted>>(replies);
        if (fault != "scene-reply-missing") fixture.Publish<IMessageSender<MatchSceneAccepted>>(replies);
        if (fault == "acceptance-duplicate") fixture.Publish<IMessageSender<MatchAuthenticationAccepted>>(replies);
        if (fault == "map-duplicate")
        {
            fixture.Binding<MatchMapNamed>(registry.Protocol);
            fixture.Binding<MatchMapNamed>(registry.Protocol);
        }
        var row = fixture.MatchRow("kr-812", report: true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new MatchConnectionPlugin().ActivateAsync(row, default).AsTask());
        Assert.Contains(expected, error.Message);
        Assert.Empty(fixture.Router.Handlers);
        Assert.Empty(replies.Sent);
    }

    [Fact]
    public async Task DirectSuccessNeedsNoSceneCodecAndDoesNotBorrowAnotherProtocolsReplySender()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var (registry, seat) = fixture.AddRoom("cn-279");
        fixture.Publish<IMatchCapabilityDirectory>(registry);
        fixture.Binding<MatchCapabilityAuthenticate>(registry.Protocol);
        var replies = new Replies(registry.Protocol) { BeforeAcceptance = () => Assert.True(seat.RelayInitialized) };
        fixture.Publish<IMessageSender<MatchAuthenticationAccepted>>(replies);
        var decoy = new Replies("kr-1256/relay");
        fixture.Publish<IMessageSender<MatchAuthenticationAccepted>>(decoy);
        fixture.Publish<IMessageSender<MatchSceneAccepted>>(decoy);
        fixture.Binding<MatchSceneIdentityRegistered>(decoy.Protocol);
        var row = fixture.MatchRow("cn-279", report: false);
        await new MatchConnectionPlugin().ActivateAsync(row, default);
        var ticket = await registry.IssueCapabilityAsync(seat.Game, 12, default);
        var relay = new Session(10, "cn-279");
        Assert.True(await fixture.Router.DispatchAsync(registry.Protocol, relay, new MatchCapabilityAuthenticate(ticket), default));
        Assert.IsType<MatchAuthenticationAccepted>(Assert.Single(replies.Sent));
        Assert.Empty(decoy.Sent);
        Assert.Single(fixture.Router.Handlers);
        await row.UnwindAsync();
        Assert.True(relay.Connection.IsClosed);
        Assert.Null(seat.Relay);
    }

    [Fact]
    public async Task ForgedNativeReportClosesOnlyTheClaimingTransportThroughTheInstalledRoute()
    {
        await using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var (registry, seat) = fixture.AddRoom("kr-812");
        fixture.Publish<IMatchCapabilityDirectory>(registry);
        fixture.Binding<MatchCapabilityAuthenticate>(registry.Protocol);
        fixture.Binding<MatchSceneIdentityRegistered>(registry.Protocol);
        var replies = new Replies(registry.Protocol);
        fixture.Publish<IMessageSender<MatchAuthenticationAccepted>>(replies);
        fixture.Publish<IMessageSender<MatchSceneAccepted>>(replies);
        var row = fixture.MatchRow("kr-812", report: true);
        await new MatchConnectionPlugin().ActivateAsync(row, default);
        var ticket = await registry.IssueCapabilityAsync(seat.Game, 20, default);
        var relay = new Session(10, "kr-812");
        await fixture.Router.DispatchAsync(registry.Protocol, relay, new MatchCapabilityAuthenticate(ticket), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Router.DispatchAsync(registry.Protocol, relay,
            new MatchSceneIdentityRegistered(1100000, "host"), default).AsTask());
        Assert.Single(replies.Sent);
        Assert.True(relay.Connection.IsClosed);
        Assert.Null(seat.Relay);
        Assert.False(seat.Game.Connection.IsClosed);
        Assert.Single(registry.RequireGame(seat.Game).Room.Seats);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal ServiceRegistry Services { get; } = new();
        internal EventBus Events { get; } = new(NullLogger.Instance);
        internal IMessageRouter Router => Services.Get<IMessageRouter>();
        private readonly List<PluginContext> _contexts = [];
        private readonly List<RoomRegistry> _registries = [];
        private PluginContext Root => _contexts[0];
        internal async Task InitializeAsync()
        {
            var root = Context("root", new());
            var bootstrap = new PluginContext(new PluginRow { Id = "router", Plugin = "core.message-router", Source = "test" },
                Services, Events, NullLogger.Instance);
            IMessageRouter router;
            try
            {
                await new MessageRouterPlugin().ActivateAsync(bootstrap, default);
                router = bootstrap.Services.Get<IMessageRouter>();
            }
            finally { await bootstrap.UnwindAsync(); }
            // Commit the detached router in this embedding before binding the consumers' declared views.
            root.Effect(Services.Register<IMessageRouter>(router));
        }
        internal (RoomRegistry Registry, RoomSeat Seat) AddRoom(string build)
        {
            var registry = new RoomRegistry(new InGameOptions { RelayProtocol = build + "/relay" });
            _registries.Add(registry);
            var setting = new SessionSettingBlob(0, 0, "", 0, 0, 0, 0, "", 2,
                MapMetaBlob.Empty() with { Unknown4 = 1, Unknown68 = "rel_Village01_Byway" }, 0, 0, 0, 0, "", 0, 0, 0, 0, 0);
            var (room, seat) = registry.Create(new Session(1, build), setting, 7, "host");
            room.MarkStarting();
            return (registry, seat);
        }
        internal void Publish<T>(T service) where T : class => Root.Effect(Services.RegisterMany<T>(service));
        internal void Binding<T>(string protocol) where T : IMessage => Publish<IInboundMessageBinding<T>>(new Binding<T>(protocol));
        internal PluginContext MatchRow(string build, bool report)
        {
            var context = Context(build + ".match", new()
            {
                ["protocol"] = build + "/relay",
                ["capability_authentication"] = true,
                ["require_capability_scene_report"] = report,
            });
            context.BindServices(typeof(MatchConnectionPlugin).GetCustomAttribute<PluginAttribute>()!);
            return context;
        }
        private PluginContext Context(string id, Dictionary<string, object?> config)
        {
            var context = new PluginContext(new PluginRow { Id = id, Plugin = "match-connection", Source = "test", Config = config },
                Services, Events, NullLogger.Instance);
            _contexts.Add(context);
            return context;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var context in _contexts.AsEnumerable().Reverse()) await context.UnwindAsync();
            foreach (var registry in _registries) await registry.StopAllAsync();
        }
    }

    private sealed class Binding<T>(string protocol) : IInboundMessageBinding<T> where T : IMessage
    {
        public string Protocol => protocol;
    }

    private sealed class Replies(string protocol) : IMessageSender<MatchAuthenticationAccepted>, IMessageSender<MatchSceneAccepted>
    {
        public string Protocol => protocol;
        internal List<IMessage> Sent { get; } = [];
        internal Action? BeforeAcceptance { get; init; }
        public ValueTask SendAsync(ISession session, MatchAuthenticationAccepted message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            BeforeAcceptance?.Invoke();
            Sent.Add(message);
            return ValueTask.CompletedTask;
        }
        public ValueTask SendAsync(ISession session, MatchSceneAccepted message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Sent.Add(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Session(long id, string build) : ISession
    {
        public long Id => id;
        public IConnection Connection { get; } = new Connection(id);
        public ClientProfile Profile { get; } = new(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Connection(long id) : IConnection
    {
        public long Id => id;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason) { IsClosed = true; return ValueTask.CompletedTask; }
    }
}
