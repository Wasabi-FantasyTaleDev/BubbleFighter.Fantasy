using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Exercises native match and lobby departure through the mounted shared room codec.</summary>
public sealed class RoomLeaveRoutingTests
{
    /// <summary>The round selector mounts through its named route, preserves other fields, and unloads all registrations.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-406")]
    [InlineData("cn-408")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("kr-812", true)]
    public async Task RoundSelectionRoutesToSharedMessageAndRetainedSettings(string build, bool serverCreated = false)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        var services = new ServiceRegistry();
        var packets = new Routes();
        var messages = new Messages();
        using var packetRegistration = services.Register<IPacketRouter>(packets);
        using var messageRegistration = services.Register<IMessageRouter>(messages);
        using var tablesRegistration = services.Register<IOpcodeTableRegistry>(new Tables(table));
        var context = new PluginContext(new PluginRow { Id = "round-selector", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var (schema, play) = TestBuilds.Of(build);
        var codec = new PlayRoomCodec(build + "/game", build, schema, play,
            new Tables(table), NullLogger.Instance, TestBuilds.Packets);
        var session = new Session(profile);
        try
        {
            codec.Mount(context);
            var native = Convert.FromHexString("0100000001000000410001000000A903");
            await packets.DispatchAsync(build + "/game", session,
                new(table.C2S("gcpRoomChangeRoundSetting"), native), default);
            var request = Assert.IsType<RoomRoundSettingsChangeRequested>(Assert.Single(messages.Received));
            Assert.Equal(1, request.Index);
            Assert.Equal(build, request.Lineage);
            Assert.Equal(native, request.Bytes.ToArray());
            var original = serverCreated
                ? RoomCodec.WriteGameSettingEnvelope(TestBuilds.Packets, build, schema,
                    new RoomValues(RoomCodec.PlayRoomKind, []) { Play = true })
                : PacketCodec.Write(TestBuilds.Packets, build, "GameSetting", new Dictionary<string, object?>());
            var settings = new RoomBuildSettings(build, serverCreated ? default : original)
            {
                RoundSelections = [new(1, native)],
                Mode = serverCreated ? 101 : null,
            };
            await codec.SendAsync(session, new RoomSettingsChanged(new(7, "room", serverCreated ? 101 : 1, "map", 4, 0, false, [], [], settings)), default);
            var reply = Assert.Single(session.Sent);
            Assert.Equal(table.S2C("gspRoomChangeGameSetting"), reply.Opcode);
            var expected = PacketCodec.Read(TestBuilds.Packets, build, "GameSetting", original);
            expected["unknown_368"] = "A";
            expected["unknown_284"] = "\u03A9";
            var unchosen = serverCreated ? "Random_Hot_Dif1" : play.Wire.UnchosenMapKey;
            expected["map_directory"] = unchosen;
            expected["current_map"] = unchosen;
            expected["map_name"] = unchosen;
            expected["npc_table_key"] = play.Wire.UnchosenMapTheme;
            if (serverCreated)
                expected["mode"] = 101;
            Assert.Equal(PacketCodec.Write(TestBuilds.Packets, build, "GameSetting", expected), reply.Body);
            var mismatched = settings with { RoundSelections = [new(0, native)] };
            await Assert.ThrowsAsync<InvalidOperationException>(() => codec.SendAsync(session,
                new RoomSettingsChanged(new(7, "room", 1, "map", 4, 0, false, [], [], mismatched)), default).AsTask());
            Assert.Single(session.Sent);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(packets.Handlers);
        Assert.Empty(services.GetAll<IInboundMessageBinding<RoomRoundSettingsChangeRequested>>());
    }

    [Theory]
    [InlineData("kr-812")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("cn-324")]
    [InlineData("kr-812", true)]
    public async Task BothEmptyRequestsReachSharedLeaveAndUnloadTogether(string build, bool remap = false)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        IOpcodeTable table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        if (remap)
            table = new OpcodeTable(profile, table.All.Select(entry => entry with { Value = checked((ushort)(entry.Value + 5000)) }));
        var services = new ServiceRegistry();
        var packets = new Routes();
        var messages = new Messages();
        using var packetRegistration = services.Register<IPacketRouter>(packets);
        using var messageRegistration = services.Register<IMessageRouter>(messages);
        using var tablesRegistration = services.Register<IOpcodeTableRegistry>(new Tables(table));
        var context = new PluginContext(new PluginRow { Id = "room-leave", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        var (schema, play) = TestBuilds.Of(build);
        var intercepted = false;
        var hookCalls = 0;
        var codec = new PlayRoomCodec(build + "/game", build, schema, play,
            new Tables(table), NullLogger.Instance, TestBuilds.Packets)
        {
            LeaveRequestFirst = (_, _, _) => { hookCalls++; return ValueTask.FromResult(intercepted); },
        };
        var session = new Session(profile);
        var protocol = build + "/game";
        var esc = table.C2S("gcpRoomReqLeaveGame");
        var lobby = table.C2S("gcpReqLeaveSession");
        Assert.NotEqual(esc, lobby);
        try
        {
            codec.Mount(context);
            if (!remap && build is "kr-812" or "cn-717" or "id-1809")
            {
                Assert.Equal((ushort)404, lobby);
                Assert.Equal((ushort)506, esc);
                Assert.DoesNotContain((protocol, (ushort)20008), packets.Handlers.Keys);
            }
            foreach (var opcode in new[] { esc, lobby })
                await packets.DispatchAsync(protocol, session, new(opcode, ReadOnlyMemory<byte>.Empty), default);
            Assert.Equal(2, messages.Received.Count);
            Assert.All(messages.Received, message => Assert.IsType<RoomLeaveRequested>(message));
            Assert.Equal(2, hookCalls);

            // A native tutorial owns its departure and must not also leave an ordinary room.
            intercepted = true;
            await packets.DispatchAsync(protocol, session, new(esc, ReadOnlyMemory<byte>.Empty), default);
            Assert.Equal(2, messages.Received.Count);
            Assert.Equal(3, hookCalls);
            intercepted = false;

            // The native packet has no fields; a trailing byte closes without a typed mutation.
            await packets.DispatchAsync(protocol, session, new(esc, new byte[1]), default);
            Assert.Equal("malformed gcpRoomReqLeaveGame", session.Transport.CloseReason);
            Assert.Equal(2, messages.Received.Count);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(packets.Handlers);
        Assert.Empty(services.GetAll<IInboundMessageBinding<RoomLeaveRequested>>());
    }

    // The leaver's gspRoomSetSession, 398 bytes.
    private const string RecordedLeaveSession =
        
        "0000000000000B0000004CD1A4C2B8D229BC1CC8A9BA85C7C8B2E4B200300030010000000400000000000000000001000000" +
        "6500000000000000090000004CBEACB9C0C9200015ACC0BC20007CC230D114000000720065006C005F00560069006C006C00" +
        "610067006500300037005F004500610073007900300032000300000041006C006C00140000006E00700063005F0056006900" +
        "6C006C00610067006500300037005F004500610073007900300031001400000080A9030080A9030080A9030080A903000100" +
        "0000010000000000000000000000000000000000000003000000FFFFFFFF0000000000000000000000000000000000000000" +
        "0000000006000000520061006E0064006F006D0006000000520061006E0064006F006D0006000000520061006E0064006F00" +
        "6D00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF000000000000000000000000000000000000000000000000";

    [Theory]
    [InlineData("kr-812", true)]
    [InlineData("cn-324", false)]
    public async Task TheLeaveSessionRestatesTheRoomWhereTheBuildDoes(string build, bool restates)
    {
        var profile = new ClientProfile(build, build[..2], int.Parse(build[3..]), 0, 0, build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        IOpcodeTable table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        var (schema, play) = TestBuilds.Of(build);
        Assert.Equal(restates, play.Wire.RestateLeftRoom);
        // The same builds wait for the stage switch before the plaza burst; cn-324 sends it at once.
        Assert.Equal(restates ? 150 : 0, play.Wire.PlazaEntrySettleMs);
        var codec = new PlayRoomCodec(build + "/game", build, schema, play, new Tables(table), NullLogger.Instance, TestBuilds.Packets);
        var recorded = Convert.FromHexString(RecordedLeaveSession);
        // The reference room: title at +6 (eleven characters), GameSetting from +50 up to the 36-byte tail.
        var title = System.Text.Encoding.Unicode.GetString(recorded, 10, 22);
        var settings = recorded[50..^36];
        var leaver = new RoomMemberView(0, 282, "TestPlayer01", 10, 2, false, 300010, [300010]);
        // The master chose the recorded map, so the block went through a settings change after the create.
        // The recorded block follows kr-812's layout; another lineage's codec ignores it.
        RoomBuildSettings? held = null;
        if (restates)
        {
            var chosen = RoomCodec.ReadGameSettingValues(schema, settings);
            var change = PacketCodec.Write(TestBuilds.Packets, build, "RoomGameSettingChangeRequest", new Dictionary<string, object?>
            {
                ["map_directory"] = chosen["map_directory"], ["current_map"] = chosen["current_map"],
                ["unknown_60"] = chosen["npc_table_key"], ["map_key"] = chosen["map_name"],
                ["unknown_116"] = chosen["unknown_4"], ["unknown_117"] = chosen["unknown_5"],
                ["mode"] = chosen["mode"], ["sub_mode"] = chosen["unknown_12"],
            });
            held = new RoomBuildSettings(build, settings, change);
        }
        var room = new RoomView(7, title, 101, "rel_Village07_Easy02", 4, 0, false, [], [leaver], held);
        var session = new Session(profile);

        await codec.SendAsync(session, new RoomLeft(7, leaver) { Room = room }, default);

        var sent = Assert.Single(session.Sent, packet => packet.Opcode == table.S2C("gspRoomSetSession"));
        if (restates)
        {
            Assert.Equal(Convert.ToHexString(recorded), Convert.ToHexString(sent.Body));
            return;
        }
        // A build that does not restate keeps the bare main-menu session, without the room's title.
        Assert.NotEqual(recorded.Length, sent.Body.Length);
        Assert.Equal(-1, sent.Body.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes(title)));
    }

    private sealed class Messages : IMessageRouter
    {
        internal List<IMessage> Received { get; } = [];
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct) where T : IMessage
        {
            Received.Add(message);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class Routes : IPacketRouter
    {
        internal Dictionary<(string, ushort), PacketHandler> Handlers { get; } = [];
        public IDisposable Register(string protocolName, ushort opcode, PacketHandler handler)
        {
            Handlers.Add((protocolName, opcode), handler);
            return new Removal(() => Handlers.Remove((protocolName, opcode)));
        }
        public ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct) =>
            Handlers[(protocolName, packet.Opcode)](session, packet, ct);
        private sealed class Removal(Action remove) : IDisposable { public void Dispose() => remove(); }
    }

    private sealed class Tables(IOpcodeTable table) : IOpcodeTableRegistry
    {
        public IReadOnlyList<IOpcodeTable> All => [table];
        public IDisposable Register(IOpcodeTable value) => throw new NotSupportedException();
        public IOpcodeTable Get(string profileId) => table;
        public bool TryGet(string profileId, out IOpcodeTable? value) { value = table; return true; }
    }

    private sealed class Session(ClientProfile profile) : ISession
    {
        internal Connection Transport { get; } = new();
        public long Id => 1;
        public IConnection Connection => Transport;
        public ClientProfile Profile => profile;
        public ConcurrentDictionary<string, object> Items { get; } = new();
        internal List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Connection : IConnection
    {
        internal string? CloseReason { get; private set; }
        public long Id => 1;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => CloseReason is not null;
        public ValueTask CloseAsync(string reason) { CloseReason = reason; return ValueTask.CompletedTask; }
    }
}
