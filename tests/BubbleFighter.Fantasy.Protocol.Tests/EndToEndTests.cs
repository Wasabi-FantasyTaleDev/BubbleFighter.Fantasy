using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Wire;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Net.Tcp;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>
/// End-to-end tests that run the TCP listener plugin and the protocol plugin in-process
/// through the real kernel, then drive the wire protocol with a raw socket.
/// </summary>
public sealed class EndToEndTests
{
    /// <summary>Shared sink the probe plugin writes into.</summary>
    public sealed class ProbeState
    {
        /// <summary>Close reasons observed via <see cref="ConnectionClosedEvent"/>.</summary>
        public readonly List<string> CloseReasons = [];

        /// <summary><c>Reported</c> values observed via <see cref="ClientVersionMismatchEvent"/>.</summary>
        public readonly List<int> MismatchReported = [];

        /// <summary>The live session registry, captured at activation.</summary>
        public ISessionRegistry? Sessions;

        /// <summary>Resets the sink.</summary>
        public void Clear()
        {
            lock (CloseReasons) CloseReasons.Clear();
            lock (MismatchReported) MismatchReported.Clear();
            Sessions = null;
        }
    }

    /// <summary>Test plugin that records connection events and the session registry.</summary>
    [Plugin("test.probe", Requires = [typeof(ISessionRegistry)])]
    public sealed class ProbePlugin : IPlugin
    {
        /// <summary>The shared sink; cleared by each test before use.</summary>
        public static readonly ProbeState State = new();

        /// <inheritdoc />
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            State.Sessions = context.Services.Get<ISessionRegistry>();
            context.Effect(context.Events.On<ConnectionClosedEvent>(e =>
            {
                lock (State.CloseReasons) State.CloseReasons.Add(e.Reason);
            }));
            context.Effect(context.Events.On<ClientVersionMismatchEvent>(e =>
            {
                lock (State.MismatchReported) State.MismatchReported.Add(e.Reported);
            }));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Test plugin whose packet handler always throws.</summary>
    [Plugin("test.throwing-handler", Requires = [typeof(IPacketRouter)])]
    public sealed class ThrowingHandlerPlugin : IPlugin
    {
        /// <inheritdoc />
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Get<IPacketRouter>().Register(
                ProtocolPlugin.GameProtocolName, WirePins.PreHandshakeOpcode,
                (session, packet, ct) => throw new InvalidOperationException("boom")));
            return ValueTask.CompletedTask;
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static PluginHost BuildHost(
        int port,
        int relayPort = 0,
        bool throwingHandler = false,
        Dictionary<string, object?>? protocolExtras = null)
    {
        var opcodeTable = Path.Combine(AppContext.BaseDirectory, "opcodes.yaml");
        PluginRow MakeRow(string id, string plugin, Dictionary<string, object?>? config = null) =>
            new() { Id = id, Plugin = plugin, Source = "test", ClientBuild = new("kr", 1256, 1304), Family = "modern", Config = config };
        var protocolConfig = new Dictionary<string, object?> { ["opcode_table_path"] = opcodeTable };
        if (protocolExtras is not null)
        {
            foreach (var (key, value) in protocolExtras)
                protocolConfig[key] = value;
        }
        var rows = new List<PluginRow>
        {
            MakeRow("core.protocol-registry", "core.protocol-registry"),
            MakeRow("core.session-registry", "core.session-registry"),
            MakeRow("core.packet-router", "core.packet-router"),
            MakeRow("core.client-profiles", "core.client-profiles"),
            MakeRow("core.opcode-tables", "core.opcode-tables"),
            MakeRow("core.wire-dialects", "core.wire-dialects"),
            MakeRow("dialect", "dialect", TestWireDialects.RowConfig("kr-1256")),
            MakeRow("protocol", "protocol", protocolConfig),
            MakeRow("game", "net.tcp", new()
            {
                ["listen"] = $"127.0.0.1:{port}",
                ["protocol"] = "kr-1256/game",
                ["idle_timeout_seconds"] = "60",
            }),
            MakeRow("probe", "test.probe"),
        };
        if (throwingHandler)
            rows.Add(MakeRow("throwing", "test.throwing-handler"));
        if (relayPort != 0)
            rows.Add(MakeRow("relay", "net.tcp", new()
            {
                ["listen"] = $"127.0.0.1:{relayPort}",
                ["protocol"] = "kr-1256/relay",
                ["idle_timeout_seconds"] = "60",
            }));
        var catalog = PluginCatalog.ScanAssemblies(
            [typeof(Dialect.DialectPlugin).Assembly, typeof(ProtocolPlugin).Assembly, typeof(TcpListenerPlugin).Assembly, typeof(PluginHost).Assembly, typeof(EndToEndTests).Assembly]);
        return new PluginHost(rows, catalog, NullLoggerFactory.Instance);
    }

    private static async Task<byte[]> ReceiveExactlyAsync(Socket socket, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(offset), CancellationToken.None);
            if (received == 0)
                throw new IOException("connection closed");
            offset += received;
        }
        return buffer;
    }

    private static async Task<Socket> ConnectAsync(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ReceiveTimeout = 15_000,
            SendTimeout = 15_000,
        };
        await socket.ConnectAsync(IPAddress.Loopback, port);
        return socket;
    }

    private static async Task SendFrameAsync(Socket socket, SimpleCipher cipher, ushort opcode, byte[] payload)
    {
        var frame = PacketFrame_8Bytes.Encode(opcode, payload);
        cipher.EncryptInPlace(frame);
        await socket.SendAsync(frame);
    }

    private static byte[] PatchPayload(int clientVersionMajor) =>
        WirePins.GamePatchPayload(clientVersionMajor);

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }
        return false;
    }

    [Fact]
    public async Task PatchHandshakeRoundTrip()
    {
        ProbePlugin.State.Clear();
        var port = FreePort();
        var host = BuildHost(port);
        await host.StartAsync(CancellationToken.None);
        try
        {
            using var socket = await ConnectAsync(port);
            var greeting = await ReceiveExactlyAsync(socket, BootstrapGreeting.Size);
            Assert.Equal(WirePins.GreetingSize, greeting.Length);
            Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(greeting));
            Assert.All(greeting[2..], b => Assert.Equal(0, b));

            await SendFrameAsync(socket, SimpleCipher.ClientToServer(), WirePins.PatchRequestOpcode, PatchPayload(1256));

            var reply = await ReceiveExactlyAsync(socket, WirePins.RepGamePatchFrameSize);
            Assert.Equal(WirePins.RepGamePatchFrameSize, reply.Length);
            Assert.Equal(WirePins.RepGamePatchS2CFrameCipherHex, Convert.ToHexString(reply));
            SimpleCipher.ServerToClient().DecryptInPlace(reply);
            Assert.Equal(WirePins.RepGamePatchPlainFrameHex, Convert.ToHexString(reply));
            Assert.True(PacketFrame_8Bytes.TryReadHeader(reply, out var header));
            Assert.Equal(WirePins.RepGamePatchPayloadSize, header.PayloadLength);
            Assert.Equal(WirePins.PatchReplyOpcode, header.Opcode);
            Assert.True(PacketFrame_8Bytes.ValidatePayload(header, reply.AsSpan(PacketFrame_8Bytes.HeaderSize)));
            Assert.All(reply[PacketFrame_8Bytes.HeaderSize..], b => Assert.Equal(0, b));
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WrongClientVersionClosesConnection()
    {
        ProbePlugin.State.Clear();
        var port = FreePort();
        var host = BuildHost(port);
        await host.StartAsync(CancellationToken.None);
        try
        {
            using var socket = await ConnectAsync(port);
            var greeting = await ReceiveExactlyAsync(socket, BootstrapGreeting.Size);
            Assert.Equal(WirePins.GreetingSize, greeting.Length);
            Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(greeting));

            var payload = PatchPayload(9999);
            Assert.Equal(WirePins.GamePatchPayloadSize, payload.Length);
            await SendFrameAsync(socket, SimpleCipher.ClientToServer(), WirePins.PatchRequestOpcode, payload);

            var buffer = new byte[16];
            var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
            Assert.Equal(0, received);

            var sawMismatch = await WaitForAsync(() =>
            {
                lock (ProbePlugin.State.MismatchReported) return ProbePlugin.State.MismatchReported.Contains(9999);
            });
            Assert.True(sawMismatch, "no ClientVersionMismatchEvent for version 9999");
            var sawClose = await WaitForAsync(() =>
            {
                lock (ProbePlugin.State.CloseReasons)
                    return ProbePlugin.State.CloseReasons.Any(r => r.Contains("client version mismatch"));
            });
            Assert.True(sawClose, "no close reason mentioning client version mismatch");
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task FirstPacketNotGamePatchClosesConnection()
    {
        ProbePlugin.State.Clear();
        var port = FreePort();
        var host = BuildHost(port);
        await host.StartAsync(CancellationToken.None);
        try
        {
            using var socket = await ConnectAsync(port);
            var greeting = await ReceiveExactlyAsync(socket, BootstrapGreeting.Size);
            Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(greeting));

            await SendFrameAsync(socket, SimpleCipher.ClientToServer(), WirePins.PreHandshakeOpcode, [1, 2, 3]);

            var buffer = new byte[16];
            var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
            Assert.Equal(0, received);

            var sawGate = await WaitForAsync(() =>
            {
                lock (ProbePlugin.State.CloseReasons)
                    return ProbePlugin.State.CloseReasons.Any(r => r.Contains("gcpReqGamePatch"));
            });
            Assert.True(sawGate, "no close reason mentioning gcpReqGamePatch was recorded");
            lock (ProbePlugin.State.MismatchReported)
                Assert.Contains(-1, ProbePlugin.State.MismatchReported);
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ThrowingPacketHandlerClosesConnection()
    {
        ProbePlugin.State.Clear();
        var port = FreePort();
        var host = BuildHost(port, throwingHandler: true);
        await host.StartAsync(CancellationToken.None);
        try
        {
            using var socket = await ConnectAsync(port);
            var greeting = await ReceiveExactlyAsync(socket, BootstrapGreeting.Size);
            Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(greeting));

            var cipher = SimpleCipher.ClientToServer();
            await SendFrameAsync(socket, cipher, WirePins.PatchRequestOpcode, PatchPayload(1256));
            var reply = await ReceiveExactlyAsync(socket, PacketFrame_8Bytes.HeaderSize + 8);
            SimpleCipher.ServerToClient().DecryptInPlace(reply);

            await SendFrameAsync(socket, cipher, WirePins.PreHandshakeOpcode, [1, 2, 3]);

            var buffer = new byte[16];
            var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
            Assert.Equal(0, received);

            var sawFailure = await WaitForAsync(() =>
            {
                lock (ProbePlugin.State.CloseReasons)
                    return ProbePlugin.State.CloseReasons.Any(r => r.Contains("handler failure") && r.Contains("Unknown521"));
            });
            Assert.True(sawFailure, "no close reason mentioning the failed handler was recorded");
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ConfiguredGreetingPins258BytesThenPinnedCipherHandshake()
    {
        ProbePlugin.State.Clear();
        var port = FreePort();
        var host = BuildHost(port, protocolExtras: new()
        {
            ["bootstrap_status"] = 9,
            ["bootstrap_command_line"] = "run",
        });
        await host.StartAsync(CancellationToken.None);
        try
        {
            using var socket = await ConnectAsync(port);
            var greeting = await ReceiveExactlyAsync(socket, WirePins.GreetingSize);
            Assert.Equal(WirePins.GreetingSize, greeting.Length);
            Assert.Equal(9, BinaryPrimitives.ReadUInt16LittleEndian(greeting));
            Assert.Equal("720075006E00", Convert.ToHexString(greeting.AsSpan(2, 6)));
            Assert.All(greeting[8..], b => Assert.Equal(0, b));

            var wire = Convert.FromHexString(WirePins.GamePatchC2SFrameCipherHex);
            Assert.Equal(WirePins.GamePatchFrameSize, wire.Length);
            await socket.SendAsync(wire);

            var reply = await ReceiveExactlyAsync(socket, WirePins.RepGamePatchFrameSize);
            Assert.Equal(WirePins.RepGamePatchS2CFrameCipherHex, Convert.ToHexString(reply));
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RelayAcceptsAnyFirstPacket()
    {
        ProbePlugin.State.Clear();
        var port = FreePort();
        var relayPort = FreePort();
        var host = BuildHost(port, relayPort: relayPort);
        await host.StartAsync(CancellationToken.None);
        try
        {
            using var socket = await ConnectAsync(relayPort);
            var greeting = await ReceiveExactlyAsync(socket, BootstrapGreeting.Size);
            Assert.Equal(WirePins.GreetingSize, greeting.Length);
            Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(greeting));

            await SendFrameAsync(socket, SimpleCipher.ClientToServer(), WirePins.PreHandshakeOpcode, [1, 2, 3]);
            await Task.Delay(400);

            var closed = socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0;
            Assert.False(closed, "relay connection was closed after a non-handshake first packet");
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None);
        }
    }
}
