using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Exercises installed native request adapters against reviewed connection-scoped tables.</summary>
public sealed class MatchMessagesMountTests
{
    /// <summary>Disabling automatic teaching preserves the native menu's connection-ready reply.</summary>
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, null, true)]
    [InlineData(true, true, true)]
    public async Task TutorialOfferDoesNotControlConnectionReadiness(bool automaticTutorial, bool? offer, bool notified)
    {
        var profile = Profile("cn-324");
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "cn-324", "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        var binding = new RoomBinding("cn-324/game", TestBuilds.Of("cn-324").Schema, new Tables(table),
            true, TestBuilds.Packets, "cn-324", 60, automaticTutorial);
        var session = new Session(profile);
        await binding.SendAsync(session, new MainMenuRoomOpened(42, "Player", 300001, [300001])
        {
            OfferTutorial = offer,
        }, default);
        var readiness = table.S2C("gspRepMoveInto");
        var index = session.Sent.IndexOf(readiness);
        Assert.True(index >= 0);
        Assert.Equal("010000003C00000000000000", Convert.ToHexString(session.Bodies[index]));
        Assert.True(index > session.Sent.IndexOf(table.S2C("gspGotoMenu")));
        var notification = table.S2C("gspNotifyAutoTutorial_");
        Assert.Equal(notified, session.Sent.Contains(notification));
        if (notified)
        {
            Assert.True(session.Sent.IndexOf(notification) > index);
            Assert.Empty(session.Bodies[session.Sent.IndexOf(notification)]);
        }
    }

    [Theory]
    [InlineData("kr-812", false)]
    [InlineData("cn-717", false)]
    [InlineData("cn-324", false)]
    [InlineData("cn-279", false)]
    [InlineData("cn-406", false)]
    [InlineData("id-400", false)]
    [InlineData("id-1809", false)]
    [InlineData("kr-812", true)]
    public async Task MountedNativeBodiesReachOnlyTheirOwnTypedRoutesAndUnload(string build, bool remap)
    {
        var profile = Profile(build);
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var full = OpcodeTableLoader.Load(Path.Combine(root, "profiles", build, "opcodes.yaml"), profile);
        var table = remap ? new OpcodeTable(profile, full.All.Concat(full.ForConnection(OpcodeConnection.Relay).All)
            .Distinct().Select(entry => entry with { Value = checked((ushort)(entry.Value + 5000)) })) : full;
        var relay = table.ForConnection(OpcodeConnection.Relay);
        Assert.Null(table.Find(PacketDirection.ClientToServer, "rcpReqJoinRelaySession"));
        Assert.Equal((ushort)(remap ? 5001 : 1), relay.C2S("rcpReqJoinRelaySession"));
        var hasReport = TestBuilds.Packets.CapabilityOf(build, "relay_validation_report");
        var leaveOpcode = relay.Find(PacketDirection.ClientToServer, "rcpReqLeaveGame");
        Assert.Equal(hasReport, relay.Find(PacketDirection.ClientToServer, "rcpReqReportHackCheckData").HasValue);
        using var fixture = new Fixture(table);
        var context = fixture.Context(build, build + "/relay");
        await new MatchMessagesPlugin().ActivateAsync(context, default);
        var session = new Session(profile);
        var joinFixture = RelayConnectionTableTests.Bodies.Cast<object[]>().Single(row => (string)row[0] == build);
        var key = Convert.FromHexString((string)joinFixture[1]);
        var join = Convert.FromHexString((string)joinFixture[1] + (string)joinFixture[2]);
        try
        {
            Assert.Equal((hasReport ? 2 : 1) + (leaveOpcode.HasValue ? 1 : 0), fixture.Packets.Handlers.Count);
            await Request(relay.C2S("rcpReqJoinRelaySession"), join);
            Assert.Equal(key, Assert.IsType<MatchCapabilityAuthenticate>(Assert.Single(fixture.Messages.Received)).Ticket.ToArray());
            Assert.Empty(session.Sent);
            Assert.Single(fixture.Services.GetAll<IInboundMessageBinding<MatchCapabilityAuthenticate>>());
            Assert.Equal(hasReport ? 1 : 0, fixture.Services.GetAll<IInboundMessageBinding<MatchSceneIdentityRegistered>>().Count);
            Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<MatchAuthenticate>>());
            Assert.Empty(fixture.Services.GetAll<IMessageSender<MatchAuthenticationAccepted>>());
            Assert.DoesNotContain(fixture.Packets.Handlers.Keys, key => key.Item1.EndsWith("/game", StringComparison.Ordinal));
            await Assert.ThrowsAsync<PacketFormatException>(() => Request(relay.C2S("rcpReqJoinRelaySession"), join[..^1]));
            await Assert.ThrowsAsync<PacketFormatException>(() => Request(relay.C2S("rcpReqJoinRelaySession"), [.. join, 0]));
            Assert.Single(fixture.Messages.Received);
            if (hasReport)
            {
                var reportFixture = RelayValidationTableTests.Bodies.Cast<object[]>().Single(row => (string)row[0] == build);
                var report = Convert.FromHexString((string)reportFixture[1]);
                await Request(relay.C2S("rcpReqReportHackCheckData"), report);
                Assert.Equal(new MatchSceneIdentityRegistered(0x11223344, "A"), fixture.Messages.Received[^1]);
                await Assert.ThrowsAsync<PacketFormatException>(() => Request(relay.C2S("rcpReqReportHackCheckData"), report[..^1]));
                await Assert.ThrowsAsync<PacketFormatException>(() => Request(relay.C2S("rcpReqReportHackCheckData"), [.. report, 0]));
                Assert.Equal(2, fixture.Messages.Received.Count);
            }
            Assert.Equal(leaveOpcode.HasValue ? 1 : 0, fixture.Services.GetAll<IInboundMessageBinding<MatchLeaveRequested>>().Count);
            if (leaveOpcode is { } leave)
            {
                Assert.Empty(TestBuilds.Packets.Resolve(build, "RelayLeaveGameRequest"));
                await Request(leave, []);
                Assert.IsType<MatchLeaveRequested>(fixture.Messages.Received[^1]);
                var received = fixture.Messages.Received.Count;
                await Assert.ThrowsAsync<PacketFormatException>(() => Request(leave, [0]));
                Assert.Equal(received, fixture.Messages.Received.Count);
            }
            var before = fixture.Messages.Received.Count;
            var other = new Session(Profile(build == "kr-812" ? "cn-324" : "kr-812"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Packets.DispatchAsync(build + "/relay", other,
                new(relay.C2S("rcpReqJoinRelaySession"), join), default).AsTask());
            Assert.Equal(before, fixture.Messages.Received.Count);
            fixture.Messages.HasHandler = false;
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Request(relay.C2S("rcpReqJoinRelaySession"), join));
            Assert.Contains("No native match handler", failure.Message);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<MatchCapabilityAuthenticate>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<MatchSceneIdentityRegistered>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<MatchLeaveRequested>>());

        Task Request(ushort opcode, byte[] body) => fixture.Packets.DispatchAsync(build + "/relay", session, new(opcode, body), default).AsTask();
    }

    [Theory]
    [InlineData("missing-build")]
    [InlineData("game-protocol")]
    [InlineData("other-protocol")]
    [InlineData("other-table")]
    [InlineData("missing-join")]
    [InlineData("missing-report")]
    public async Task InvalidScopeOrRequiredRequestFailsBeforePublishingEffects(string fault)
    {
        var build = fault == "other-table" ? "cn-324" : "kr-812";
        var entries = new List<OpcodeEntry>();
        if (fault != "missing-join") entries.Add(Entry(1, "rcpReqJoinRelaySession"));
        if (fault != "missing-report") entries.Add(Entry(12, "rcpReqReportHackCheckData"));
        using var fixture = new Fixture(new OpcodeTable(Profile(build), entries));
        var protocol = fault switch { "game-protocol" => "kr-812/game", "other-protocol" => "cn-324/relay", _ => "kr-812/relay" };
        var context = fixture.Context(fault == "missing-build" ? null : "kr-812", protocol);
        try { await Assert.ThrowsAsync<ConfigException>(() => new MatchMessagesPlugin().ActivateAsync(context, default).AsTask()); }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<MatchCapabilityAuthenticate>>());
        Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<MatchSceneIdentityRegistered>>());
    }

    /// <summary>A table that retypes a relay field the wire helpers consume is refused when the row binds.</summary>
    [Fact]
    public async Task AConsumedFieldWithTheWrongTypeIsRefusedWhenTheRowBinds()
    {
        var directory = PacketTableEdits.WithFieldType("RelayJoinRequest", "key", "RelaySessionKey", "u32");
        var profile = Profile("kr-812");
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "kr-812", "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Relay);
        using var fixture = new Fixture(table);
        var context = fixture.Context("kr-812", "kr-812/relay", packetsDirectory: directory.FullName);
        try
        {
            var error = await Assert.ThrowsAsync<ConfigException>(() => new MatchMessagesPlugin().ActivateAsync(context, default).AsTask());
            Assert.Contains("key", error.Message, StringComparison.Ordinal);
            Assert.Contains("Relay connection", error.Message, StringComparison.Ordinal);
            Assert.Empty(fixture.Packets.Handlers);
            Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<MatchCapabilityAuthenticate>>());
            Assert.Empty(fixture.Services.GetAll<IInboundMessageBinding<MatchSceneIdentityRegistered>>());
        }
        finally { await context.UnwindAsync(); }
    }

    private static OpcodeEntry Entry(ushort value, string name) => new(value, PacketDirection.ClientToServer, name, Evidence.Verified, null, null)
        { Connection = OpcodeConnection.Relay };
    private static ClientProfile Profile(string build) => new(build, build[..2], int.Parse(build[3..]), 0, 0, build);

    internal sealed class Fixture : IDisposable
    {
        internal ServiceRegistry Services { get; } = new();
        internal Packets Packets { get; } = new();
        internal Messages Messages { get; } = new();
        private readonly IDisposable[] _registrations;
        internal Fixture(IOpcodeTable table) => _registrations =
        [Services.Register<IPacketRouter>(Packets), Services.Register<IMessageRouter>(Messages), Services.Register<IOpcodeTableRegistry>(new Tables(table))];
        internal PluginContext Context(string? build, string protocol, string plugin = "dialect.match", string? packetsDirectory = null,
            Dictionary<string, object?>? extra = null)
        {
            var config = new Dictionary<string, object?> { ["protocol"] = protocol, ["packets_directory"] = packetsDirectory ?? TestBuilds.PacketsDirectory() };
            foreach (var (key, value) in extra ?? [])
                config[key] = value;
            return new(new PluginRow
            {
                Id = "native-requests", Plugin = plugin, Source = "test",
                ClientBuild = build is null ? null : new ClientBuild(build[..2], int.Parse(build[3..]), 0),
                Config = config,
            }, Services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        }
        public void Dispose() { foreach (var registration in _registrations.Reverse()) registration.Dispose(); }
    }

    internal sealed class Messages : IMessageRouter
    {
        internal List<IMessage> Received { get; } = [];
        internal bool HasHandler { get; set; } = true;
        public IReadOnlyList<MessageHandlerRegistration> Handlers => [];
        public IMessageRoutes For(IPluginContext owner) => throw new NotSupportedException();
        public ValueTask<bool> DispatchAsync<T>(string protocol, ISession session, T message, CancellationToken ct) where T : IMessage
        { Received.Add(message); return ValueTask.FromResult(HasHandler); }
    }

    internal sealed class Packets : IPacketRouter
    {
        internal Dictionary<(string, ushort), PacketHandler> Handlers { get; } = [];
        public IDisposable Register(string protocolName, ushort opcode, PacketHandler handler)
        { Handlers.Add((protocolName, opcode), handler); return new Removal(() => Handlers.Remove((protocolName, opcode))); }
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

    internal sealed class Session(ClientProfile profile) : ISession
    {
        internal List<ushort> Sent { get; } = [];
        internal List<byte[]> Bodies { get; } = [];
        internal bool FailSend { get; set; }
        public long Id => 1;
        public IConnection Connection { get; } = new Connection();
        public ClientProfile Profile => profile;
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            if (FailSend) throw new IOException("Native reply transport failed.");
            Sent.Add(opcode);
            Bodies.Add(payload.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Connection : IConnection
    {
        public long Id => 1;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason) { IsClosed = true; return ValueTask.CompletedTask; }
    }
}
