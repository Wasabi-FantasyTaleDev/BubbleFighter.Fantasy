using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Architecture.Tests;

public sealed class NativeMatchHostingTests
{
    [Fact]
    public async Task NativeDialectWireRowsDecodeIntoTheHostedLifecycleAndReloadTheirConsumers()
    {
        var builds = new[] { "kr-812", "cn-717", "cn-324", "cn-279", "cn-406", "id-400", "id-1809" };
        var prefix = "native-wire-" + Guid.NewGuid().ToString("N");
        var packetsDirectory = FindRepositoryDirectory("packets");
        var rows = new List<PluginRow>
        {
            PluginBoundaryTests.Row(prefix + ".messages", "core.message-router"),
            PluginBoundaryTests.Row(prefix + ".packets", "core.packet-router"),
            PluginBoundaryTests.Row(prefix + ".tables", "core.opcode-tables"),
        };
        foreach (var build in builds)
        {
            rows.Add(PluginBoundaryTests.Row(prefix + ".adapter." + build, "test.native-match-adapter", new()
            {
                ["protocol"] = build + "/relay", ["scene_report"] = build != "cn-279", ["wire_requests"] = true,
            }));
            rows.Add(PluginBoundaryTests.Row(prefix + ".dialect." + build, "dialect.match", new()
            {
                ["protocol"] = build + "/relay", ["packets_directory"] = packetsDirectory,
            }) with { ClientBuild = new ClientBuild(build[..2], int.Parse(build[3..]), 0) });
            rows.Add(PluginBoundaryTests.Row(prefix + ".replies." + build, "dialect.match-replies", new()
            {
                ["protocol"] = build + "/relay", ["packets_directory"] = packetsDirectory,
            }) with { ClientBuild = new ClientBuild(build[..2], int.Parse(build[3..]), 0) });
            rows.Add(PluginBoundaryTests.Row(prefix + ".offers." + build, "dialect.match-offers", new()
            {
                ["protocol"] = build + "/game", ["packets_directory"] = packetsDirectory,
            }) with { ClientBuild = new ClientBuild(build[..2], int.Parse(build[3..]), 0) });
            rows.Add(PluginBoundaryTests.Row(prefix + ".offer-observer." + build, "test.native-offer-observer", new()
            {
                ["protocol"] = build + "/game",
            }));
            rows.Add(PluginBoundaryTests.Row(prefix + ".match." + build, "match-connection", new()
            {
                ["protocol"] = build + "/relay", ["capability_authentication"] = true,
                ["require_capability_scene_report"] = build != "cn-279",
            }));
        }
        var host = new PluginHost(rows, PluginBoundaryTests.Discover(), NullLoggerFactory.Instance);
        var sessions = new List<Session>();
        try
        {
            await host.StartAsync(default);
            Assert.Equal(45, host.Instances.Count);
            Assert.All(host.Instances, row => Assert.Equal(PluginState.Active, row.State));
            foreach (var build in builds) await Enter(build);
            var old = Adapter.Instances[prefix + ".adapter.kr-812"];
            var reload = await host.ReloadAsync(prefix + ".adapter.kr-812", default);
            Assert.Contains(prefix + ".dialect.kr-812", reload.ReloadedRowIds);
            Assert.Contains(prefix + ".match.kr-812", reload.ReloadedRowIds);
            Assert.Contains(prefix + ".offers.kr-812", reload.ReloadedRowIds);
            // Every dialect declares the shared set of table providers, so their reload invalidates all dependent leases.
            Assert.All(sessions, session => Assert.True(session.Connection.IsClosed));
            Assert.NotSame(old, Adapter.Instances[prefix + ".adapter.kr-812"]);
            foreach (var build in builds) await Enter(build);
        }
        finally { await host.ShutdownAsync(default); }
        Assert.All(sessions, session => Assert.True(session.Connection.IsClosed));

        async Task Enter(string build)
        {
            var adapter = Adapter.Instances[prefix + ".adapter." + build];
            var session = new Session(sessions.Count + 1, build);
            session.BeforeSend = (opcode, body) =>
            {
                Assert.Empty(body.ToArray());
                Assert.Equal(opcode == 13 || build == "cn-279", adapter.Initialized);
            };
            sessions.Add(session);
            var keyLength = build switch { "cn-279" => 12, "cn-324" => 18, _ => 20 };
            var tailLength = build is "cn-279" or "cn-324" or "cn-406" or "id-400" ? 4 : 5;
            var join = new byte[keyLength + tailLength];
            join[0] = 42;
            await adapter.Packets!.DispatchAsync(adapter.Protocol, session, new(1, join), default);
            if (build != "cn-279")
            {
                const string prefixHex = "4433221101000000410088776655";
                const string equipment = "01000000020000000300000002000000040000000500000002000000AABB"
                    + "0100000006000000070000000200000008000000090000000A000000010000004100"
                    + "010000000B000000000000000000F83F";
                const string presets = "01000000010000000400000001000000010100000002";
                const string tail = "0100000042000304000500060007000802000000090A";
                var hex = build == "cn-324" ? prefixHex + "01000000020000000300000002000000040000000500000002000000AABB010000004200030400"
                    : prefixHex + equipment + presets + tail + (build == "cn-717" ? "01000000" : "");
                await adapter.Packets.DispatchAsync(adapter.Protocol, session, new(12, Convert.FromHexString(hex)), default);
            }
            Assert.Same(session, adapter.Attached);
            Assert.True(adapter.Initialized);
            Assert.Equal(build == "cn-279" ? new ushort[] { 1 } : new ushort[] { 1, 13 }, session.Sent);
            Assert.Equal(0, adapter.Replies);
            var game = new Session(1000 + session.Id, build);
            game.BeforeSend = (opcode, body) =>
            {
                Assert.Equal(601, opcode);
                Assert.Equal(new byte[] { 1, 2, 3, 4, 0xD1, 0x1B }.Concat(join.Take(keyLength)), body.ToArray());
            };
            var observer = OfferObserver.Instances[prefix + ".offer-observer." + build];
            await observer.Sender.SendAsync(game, new(IPAddress.Parse("1.2.3.4"), 7121, join.AsMemory(0, keyLength)), default);
            await adapter.Packets!.DispatchAsync(build + "/game", game,
                new(601, Convert.FromHexString(build == "id-400" ? "0100000000000000" : "01000000")), default);
            Assert.Equal(new MatchConnectionReported(true), Assert.Single(observer.GameReports));
        }
    }

    private static string FindRepositoryDirectory(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, name);
            if (File.Exists(Path.Combine(path, "builds.yaml"))) return path;
        }
        throw new InvalidOperationException("Repository packet data were not found.");
    }

    [Fact]
    public async Task NativeRowsActivateWithoutAccountDirectoryAndRespectUnloadAndAdapterReload()
    {
        var builds = new[] { "kr-812", "cn-717", "cn-324", "cn-279", "cn-406", "id-400", "id-1809" };
        var prefix = "native-" + Guid.NewGuid().ToString("N");
        var rows = new List<PluginRow> { PluginBoundaryTests.Row(prefix + ".router", "core.message-router") };
        foreach (var build in builds)
        {
            rows.Add(PluginBoundaryTests.Row(prefix + ".adapter." + build, "test.native-match-adapter", new()
            {
                ["protocol"] = build + "/relay", ["scene_report"] = build != "cn-279",
            }));
            rows.Add(PluginBoundaryTests.Row(prefix + ".match." + build, "match-connection", new()
            {
                ["protocol"] = build + "/relay", ["capability_authentication"] = true,
                ["require_capability_scene_report"] = build != "cn-279",
            }));
        }
        var host = new PluginHost(rows, PluginBoundaryTests.Discover(), NullLoggerFactory.Instance);
        var sessions = new Dictionary<string, Session>();
        try
        {
            await host.StartAsync(default);
            Assert.Equal(15, host.Instances.Count);
            Assert.All(host.Instances, row => Assert.Equal(PluginState.Active, row.State));
            foreach (var build in builds)
            {
                var adapter = Adapter.Instances[prefix + ".adapter." + build];
                var session = new Session(sessions.Count + 1, build);
                sessions.Add(build, session);
                Assert.True(await adapter.Router.DispatchAsync(adapter.Protocol, session, new MatchCapabilityAuthenticate(new byte[] { 42 }), default));
                if (build != "cn-279")
                    Assert.True(await adapter.Router.DispatchAsync(adapter.Protocol, session, new MatchSceneIdentityRegistered(1000000, "host"), default));
                Assert.True(adapter.Initialized);
                Assert.Equal(build == "cn-279" ? 1 : 2, adapter.Replies);
            }
            await host.UnloadAsync(prefix + ".match.cn-324", default);
            Assert.True(sessions["cn-324"].Connection.IsClosed);
            Assert.False(sessions["kr-812"].Connection.IsClosed);
            Assert.Null(Adapter.Instances[prefix + ".adapter.cn-324"].Attached);
            var old = Adapter.Instances[prefix + ".adapter.kr-812"];
            var reload = await host.ReloadAsync(prefix + ".adapter.kr-812", default);
            Assert.Contains(prefix + ".match.kr-812", reload.ReloadedRowIds);
            Assert.True(sessions["kr-812"].Connection.IsClosed);
            var current = Adapter.Instances[prefix + ".adapter.kr-812"];
            Assert.NotSame(old, current);
            var fresh = new Session(100, "kr-812");
            sessions["fresh"] = fresh;
            Assert.True(await current.Router.DispatchAsync(current.Protocol, fresh, new MatchCapabilityAuthenticate(new byte[] { 42 }), default));
            Assert.True(await current.Router.DispatchAsync(current.Protocol, fresh, new MatchSceneIdentityRegistered(1000000, "host"), default));
            Assert.True(current.Initialized);
        }
        finally { await host.ShutdownAsync(default); }
        Assert.All(sessions.Values, session => Assert.True(session.Connection.IsClosed));
        Assert.DoesNotContain(Adapter.Instances.Keys, key => key.StartsWith(prefix, StringComparison.Ordinal));
    }

    public sealed class Options
    {
        public string Protocol { get; set; } = "";
        public bool SceneReport { get; set; } = true;
        public bool WireRequests { get; set; }
    }

    [Plugin("test.native-match-adapter", Requires = [typeof(IMessageRouter)], Optional = [typeof(IPacketRouter), typeof(IOpcodeTableRegistry)],
        ProvidesMany = [typeof(IMatchCapabilityDirectory)],
        ConditionalProvidesMany = [typeof(IOpcodeTable), typeof(IInboundMessageBinding<MatchCapabilityAuthenticate>), typeof(IInboundMessageBinding<MatchSceneIdentityRegistered>), typeof(IMessageSender<MatchAuthenticationAccepted>), typeof(IMessageSender<MatchSceneAccepted>)])]
    public sealed class Adapter : IPlugin, IMatchCapabilityDirectory,
        IInboundMessageBinding<MatchCapabilityAuthenticate>, IInboundMessageBinding<MatchSceneIdentityRegistered>,
        IMessageSender<MatchAuthenticationAccepted>, IMessageSender<MatchSceneAccepted>
    {
        public static ConcurrentDictionary<string, Adapter> Instances { get; } = new();
        public string Protocol { get; private set; } = "";
        public IMessageRouter Router { get; private set; } = null!;
        public IPacketRouter? Packets { get; private set; }
        private bool _wireRequests;
        public ISession? Attached { get; private set; }
        public bool Initialized { get; private set; }
        public int Replies { get; private set; }
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken ct)
        {
            var options = context.Config.Bind<Options>();
            Protocol = options.Protocol;
            _wireRequests = options.WireRequests;
            Router = context.Services.Get<IMessageRouter>();
            context.Effect(context.Services.RegisterMany<IMatchCapabilityDirectory>(this));
            if (options.WireRequests)
            {
                Packets = context.Services.Get<IPacketRouter>();
                var build = Protocol[..Protocol.IndexOf('/')];
                var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
                var table = new NativeTable(profile, options.SceneReport, OpcodeConnection.Game);
                context.Effect(context.Services.Get<IOpcodeTableRegistry>().Register(table));
                context.Effect(context.Services.RegisterMany<IOpcodeTable>(table));
            }
            else
            {
                context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MatchCapabilityAuthenticate>>(this));
                context.Effect(context.Services.RegisterMany<IMessageSender<MatchAuthenticationAccepted>>(this));
                if (options.SceneReport)
                {
                    context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MatchSceneIdentityRegistered>>(this));
                    context.Effect(context.Services.RegisterMany<IMessageSender<MatchSceneAccepted>>(this));
                }
            }
            Instances[context.Row.Id] = this;
            context.Effect(() => Instances.TryRemove(context.Row.Id, out _));
            return ValueTask.CompletedTask;
        }
        public ValueTask<ReadOnlyMemory<byte>> IssueCapabilityAsync(ISession game, int ticketLength, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<MatchSeatBinding> BindAsync(ISession session, MatchAuthenticate authentication, CancellationToken ct) =>
            throw new InvalidOperationException("Native rows must not use account-bearing admission.");
        public ValueTask<MatchSeatBinding> BindCapabilityAsync(ISession session, MatchCapabilityAuthenticate authentication, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_wireRequests)
            {
                Assert.Equal(42, authentication.Ticket.Span[0]);
                Assert.All(authentication.Ticket.ToArray().Skip(1), value => Assert.Equal(0, value));
            }
            else Assert.Equal(new byte[] { 42 }, authentication.Ticket.ToArray());
            Assert.Equal(session.Profile.Id + "/relay", Protocol);
            Attached = session;
            return ValueTask.FromResult(new MatchSeatBinding(1, 0, 7, _wireRequests ? 0x11223344 : 1000000, _wireRequests ? "A" : "host", "map", 1));
        }
        public bool IsCurrent(ISession session, MatchSeatBinding binding) => ReferenceEquals(Attached, session) && !session.Connection.IsClosed;
        public void MarkInitialized(ISession session, MatchSeatBinding binding)
        {
            Assert.True(IsCurrent(session, binding));
            Initialized = true;
        }
        public void Release(ISession session)
        {
            if (!ReferenceEquals(Attached, session)) return;
            Attached = null;
            Initialized = false;
        }
        public ValueTask SendAsync(ISession session, MatchAuthenticationAccepted message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Replies++;
            return ValueTask.CompletedTask;
        }
        public ValueTask SendAsync(ISession session, MatchSceneAccepted message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.True(Initialized);
            Replies++;
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("test.native-offer-observer", Requires = [typeof(IMessageRouter)], RequiresMany = [typeof(IMatchCapabilityOfferSender)])]
    public sealed class OfferObserver : IPlugin
    {
        public static ConcurrentDictionary<string, OfferObserver> Instances { get; } = new();
        public IMatchCapabilityOfferSender Sender { get; private set; } = null!;
        public List<MatchConnectionReported> GameReports { get; } = [];
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken ct)
        {
            var options = context.Config.Bind<Options>();
            Sender = context.Services.GetAll<IMatchCapabilityOfferSender>().Single(sender => sender.Protocol == options.Protocol);
            context.Effect(context.Services.Get<IMessageRouter>().For(context).On<MatchConnectionReported>(options.Protocol, (_, report, _) =>
            {
                GameReports.Add(report);
                return ValueTask.CompletedTask;
            }));
            Instances[context.Row.Id] = this;
            context.Effect(() => Instances.TryRemove(context.Row.Id, out _));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NativeTable(ClientProfile profile, bool sceneReport, OpcodeConnection connection) : IOpcodeTable
    {
        public ClientProfile Profile => profile;
        public OpcodeConnection Connection => connection;
        public IOpcodeTable ForConnection(OpcodeConnection value) => new NativeTable(profile, sceneReport, value);
        public IEnumerable<OpcodeEntry> All => connection == OpcodeConnection.Relay
            ? sceneReport ? [Entry(1, "rcpReqJoinRelaySession"), Entry(12, "rcpReqReportHackCheckData"),
                Entry(1, "rspRelayJoinAccepted_", PacketDirection.ServerToClient), Entry(13, "rspRelaySceneAccepted_", PacketDirection.ServerToClient)]
                : [Entry(1, "rcpReqJoinRelaySession"), Entry(1, "rspRelayJoinAccepted_", PacketDirection.ServerToClient)]
            : [new(601, PacketDirection.ServerToClient, "gspConnectToRelayServer", Evidence.Verified, null, null),
               new(601, PacketDirection.ClientToServer, "gcpReportRelayConnected", Evidence.Verified, null, null)];
        public bool TryGet(PacketDirection direction, ushort value, out OpcodeEntry? entry)
        { entry = All.FirstOrDefault(item => item.Direction == direction && item.Value == value); return entry is not null; }
        public string Describe(PacketDirection direction, ushort value) => TryGet(direction, value, out var entry) ? entry!.Name : "Unknown";
        private static OpcodeEntry Entry(ushort value, string name, PacketDirection direction = PacketDirection.ClientToServer) => new(value, direction, name, Evidence.Verified, null, null,
            Connection: OpcodeConnection.Relay);
    }

    private sealed class Session(long id, string build) : ISession
    {
        public long Id => id;
        public IConnection Connection { get; } = new Connection(id);
        public ClientProfile Profile { get; } = new(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public List<ushort> Sent { get; } = [];
        public Action<ushort, ReadOnlyMemory<byte>>? BeforeSend { get; set; }
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            BeforeSend?.Invoke(opcode, payload);
            Sent.Add(opcode);
            return ValueTask.CompletedTask;
        }
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
