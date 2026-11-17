using System.Buffers.Binary;
using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>
/// The shared clan port: a connection is greeted and answered without a build, and its login hands it, and every
/// later frame, to the clan protocol of the build its account's game session is on.
/// </summary>
public sealed class ClanGatewayTests
{
    private const int UserNumber = 42;

    [Fact]
    public async Task LoginHandsTheConnectionToTheBuildOfItsGameSession()
    {
        await using var rig = await Rig.StartAsync("kr-812");
        Assert.Equal([0, 0, 0xEA, 0x03], await rig.Client.ReceiveExactlyAsync(4, rig.Timeout));

        await rig.SendAsync(ClanGatewayPlugin.HeartbeatOpcode, []);
        Assert.Equal([0, 0, 0xEA, 0x03], await rig.Client.ReceiveExactlyAsync(4, rig.Timeout));

        await rig.SendAsync(ClanGatewayPlugin.LoginOpcode, Login(UserNumber));
        var home = rig.Table.ForConnection(OpcodeConnection.Clan).C2S("ClanHomeRequest");
        await rig.SendAsync(home, []);
        await rig.SendAsync(ClanGatewayPlugin.HeartbeatOpcode, []);
        await rig.WaitForAsync(5);

        // The login reaches the gateway, which hands it on; from then on the build's protocol gets every frame.
        Assert.Equal(
            [("clan", 1003), ("clan", 1005), ("kr-812/clan", 1005), ("kr-812/clan", home), ("kr-812/clan", 1003)],
            rig.Router.Dispatched.Select(d => (d.Protocol, (int)d.Opcode)).ToArray());
        Assert.Equal("kr-812", rig.Router.Sessions.Last().Profile.Id);
        Assert.False(rig.Client.IsClosed);
    }

    [Fact]
    public async Task LoginWithoutAGameSessionIsClosed()
    {
        await using var rig = await Rig.StartAsync("kr-812");
        await rig.SendAsync(ClanGatewayPlugin.LoginOpcode, Login(UserNumber + 1));
        await rig.WaitForCloseAsync();
        Assert.Equal("clan login without a game session", rig.Client.CloseReason);
        Assert.DoesNotContain(rig.Router.Dispatched, d => d.Protocol != "clan");
    }

    [Fact]
    public async Task LoginOfABuildWithoutAClanConnectionIsClosed()
    {
        await using var rig = await Rig.StartAsync("cn-324");
        await rig.SendAsync(ClanGatewayPlugin.LoginOpcode, Login(UserNumber));
        await rig.WaitForCloseAsync();
        Assert.Equal("build cn-324 has no clan connection", rig.Client.CloseReason);
        Assert.DoesNotContain(rig.Router.Dispatched, d => d.Protocol != "clan");
    }

    /// <summary>A login body: the user number, then bytes the gateway does not read.</summary>
    private static byte[] Login(int userNumber)
    {
        var body = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(body, userNumber);
        return body;
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(15));
        private readonly List<IDisposable> _registrations = [];
        private WireSession? _game;
        private Task? _run;
        private VersionVerifiedScope? _verified;

        public ProtocolHandshakeTests.LoopbackConnection Client { get; } = new();
        public RecordingRouter Router { get; } = new();
        public IOpcodeTable Table { get; private set; } = null!;
        public CancellationToken Timeout => _timeout.Token;

        public static async Task<Rig> StartAsync(string build)
        {
            var rig = new Rig();
            var dialect = TestWireDialects.Get(build);
            rig.Table = OpcodeTableLoader.Load(Path.Combine(Root(), "profiles", build, "opcodes.yaml"), dialect.Profile);
            var events = new EventBus(NullLogger.Instance);
            rig._game = new WireSession(new ProtocolHandshakeTests.LoopbackConnection(), dialect.Profile, build + "/game", dialect,
                dialect.CreateEncryptor(PacketDirection.ServerToClient), events, 4, 4, NullLogger.Instance);
            rig._registrations.Add(rig._game.RegisterFeature<IPlayerSession>(new PlayerSession(new PlayerIdentity(UserNumber, "user", "clanner", 1))));
            rig._registrations.AddRange(ClanGatewayPlugin.RegisterHandlers("clan", rig.Router, new Sessions(rig._game), new Tables(rig.Table),
                NullLogger.Instance));
            // The build's own clan protocol is the recording router's: it records what reaches it.
            rig._verified = new VersionVerifiedScope(4);
            var protocol = ClanGatewayPlugin.CreateProtocol("clan", new ProtocolOptions(), rig.Router, events, rig._verified,
                NullLogger.Instance, CancellationToken.None);
            rig._run = protocol.RunAsync(rig.Client, CancellationToken.None).AsTask();
            await Task.Yield();
            return rig;
        }

        public async Task SendAsync(ushort opcode, byte[] body)
        {
            await Client.ClientOutput.WriteAsync(PacketFrame_ClanServer.Encode(opcode, body), Timeout);
        }

        public async Task WaitForAsync(int dispatched)
        {
            while (Router.Dispatched.Count < dispatched)
                await Task.Delay(10, Timeout);
        }

        public async Task WaitForCloseAsync()
        {
            while (!Client.IsClosed)
                await Task.Delay(10, Timeout);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.CloseAsync("test over");
            if (_run is not null)
                await _run;
            foreach (var registration in _registrations)
                registration.Dispose();
            if (_game is not null)
            {
                await _game.StopAsync();
                _game.Dispose();
            }
            _verified?.Dispose();
            _timeout.Dispose();
        }
    }

    /// <summary>Runs the gateway's own handlers and records every dispatch, standing in for each build's clan protocol.</summary>
    private sealed class RecordingRouter : IPacketRouter
    {
        private readonly ConcurrentDictionary<(string, ushort), PacketHandler> _handlers = new();
        private readonly ConcurrentQueue<(string Protocol, ushort Opcode)> _dispatched = new();
        private readonly ConcurrentQueue<ISession> _sessions = new();

        public IReadOnlyList<(string Protocol, ushort Opcode)> Dispatched => [.. _dispatched];
        public IReadOnlyList<ISession> Sessions => [.. _sessions];

        public IDisposable Register(string protocolName, ushort opcode, PacketHandler handler)
        {
            if (!_handlers.TryAdd((protocolName, opcode), handler))
                throw new InvalidOperationException($"{protocolName} {opcode} is already registered.");
            return new Registration(() => _handlers.TryRemove((protocolName, opcode), out _));
        }

        public async ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct)
        {
            _dispatched.Enqueue((protocolName, packet.Opcode));
            _sessions.Enqueue(session);
            if (_handlers.TryGetValue((protocolName, packet.Opcode), out var handler))
                await handler(session, packet, ct);
        }

        private sealed class Registration(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class Sessions(ISession game) : ISessionRegistry
    {
        public IReadOnlyList<ISession> All => [game];
        public void Add(ISession session) => throw new NotSupportedException();
        public void Remove(ISession session) => throw new NotSupportedException();
    }

    private sealed class Tables(IOpcodeTable table) : IOpcodeTableRegistry
    {
        public IReadOnlyList<IOpcodeTable> All => [table];
        public IOpcodeTable Get(string profileId) => profileId == table.Profile.Id ? table : throw new InvalidOperationException();
        public bool TryGet(string profileId, out IOpcodeTable? found)
        {
            found = profileId == table.Profile.Id ? table : null;
            return found is not null;
        }
        public IDisposable Register(IOpcodeTable value) => throw new NotSupportedException();
    }

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BubbleFighter.Fantasy.sln"))) return dir.FullName;
        throw new InvalidOperationException("Repository root missing.");
    }
}
