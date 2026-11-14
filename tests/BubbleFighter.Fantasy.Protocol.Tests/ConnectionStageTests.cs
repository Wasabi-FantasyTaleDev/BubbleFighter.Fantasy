using System.Net;
using System.Net.Sockets;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Listeners;
using BubbleFighter.Fantasy.Diagnostics;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Net.Tcp;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

public sealed class ConnectionStageTests
{
    private const string Secret = "ticket=stage-private-credential";

    [Plugin("test.stage-close", RequiresMany = [typeof(IListenerEndpoint)])]
    public sealed class ClosePlugin : IPlugin
    {
        internal static TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal static int Port;

        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Port = Assert.Single(context.Services.GetAll<IListenerEndpoint>()).LocalEndPoint.Port;
            context.Effect(context.Events.On<ConnectionClosedEvent>(_ => Closed.TrySetResult(), priority: -1));
            return ValueTask.CompletedTask;
        }
    }

    [Plugin("test.center-stage-reply", Requires = [typeof(IPacketRouter), typeof(IOpcodeTableRegistry)])]
    public sealed class ReplyPlugin : IPlugin
    {
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            var table = context.Services.Get<IOpcodeTableRegistry>().ForProtocol("cn-324/game");
            var request = table.C2S("gcpReqCenterUserCnt");
            var reply = table.S2C("gspRepCenterUserCnt");
            context.Effect(context.Services.Get<IPacketRouter>().Register("cn-324/game", request,
                (session, _, ct) => session.SendAsync(reply, Encoding.ASCII.GetBytes(Secret), ct)));
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Cn324SocketStagesReachFileWithConsolePacketsDisabledAndNoCredentialContents()
    {
        var (text, greeting, response) = await RunSocketAsync("cn-324", true, true);
        Assert.Equal(258, greeting.Length);
        Assert.Equal(2, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(greeting));
        Assert.Equal(Secret, Encoding.ASCII.GetString(response));
        var stages = text.Split('\n').Where(line => line.Contains("connection-stage", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "accepted", "greeting-written", "first-client-opcode", "center-status-request",
            "center-status-response", "closed" }, stages.Select(Stage));
        Assert.Contains("profile=cn-324 protocol=cn-324/game", stages[1]);
        Assert.Contains("bytes=258 status=2", stages[1]);
        Assert.Contains("opcode=2 bytes=0", stages[2]);
        Assert.Contains("opcode=22 bytes=0", stages[3]);
        Assert.Contains($"opcode=15 bytes={Encoding.ASCII.GetByteCount(Secret)}", stages[4]);
        Assert.Contains("reason=peer-eof", stages[5]);
        Assert.DoesNotContain(Secret, string.Join('\n', stages));
        Assert.DoesNotContain(Convert.ToHexString(Encoding.ASCII.GetBytes(Secret)), string.Join('\n', stages));
    }

    [Fact]
    public async Task GreetingThenEofHasNoInventedClientOpcodeOrCenterStatus()
    {
        var (text, greeting, _) = await RunSocketAsync("cn-324", false, false);
        Assert.Equal(258, greeting.Length);
        Assert.Contains("stage=greeting-written", text);
        Assert.Contains("stage=closed", text);
        Assert.DoesNotContain("stage=first-client-opcode", text);
        Assert.DoesNotContain("stage=center-status-", text);
    }

    [Theory]
    [InlineData("cn-324")]
    [InlineData("kr-1256")]
    public async Task FirstFrameIsRecordedBeforeAdmissionRejectionWithoutPayload(string profile)
    {
        var (text, _, _) = await RunSocketAsync(profile, true, false);
        var stages = string.Join('\n', text.Split('\n').Where(line => line.Contains("connection-stage", StringComparison.Ordinal)));
        Assert.Contains("stage=first-client-opcode", stages);
        Assert.Contains($"opcode=3 bytes={Encoding.ASCII.GetByteCount(Secret)}", stages);
        Assert.Contains("reason=handshake-rejected", stages);
        Assert.DoesNotContain("stage=center-status-", stages);
        Assert.DoesNotContain(Secret, stages);
        Assert.DoesNotContain(Convert.ToHexString(Encoding.ASCII.GetBytes(Secret)), stages);
    }

    [Theory]
    [InlineData("error: ticket=private\r\npassword=secret", "transport-or-protocol-error")]
    [InlineData("handler failure: password=private", "handler-failed")]
    [InlineData("account=private password=secret", "other")]
    [InlineData("peer ended with an incomplete frame (9 buffered bytes)", "incomplete-frame")]
    public void CloseStageNeverCopiesArbitraryCloseText(string reason, string expected)
    {
        var connection = new ProtocolHandshakeTests.LoopbackConnection();
        Assert.Equal($"connection-stage stage=closed conn=1 reason={expected}",
            ConnectionStageLog.Closed(new(connection, reason)));
    }

    [Fact]
    public async Task CompletedGreetingPipeDoesNotReportWrittenOrStartParsing()
    {
        var dialect = TestWireDialects.Get("cn-324");
        var table = OpcodeTableLoader.Load(Path.Combine(Root(), "profiles", "cn-324", "opcodes.yaml"), dialect.Profile);
        var events = new EventBus(NullLogger.Instance);
        var greetings = new List<BootstrapGreetingWrittenEvent>();
        using var subscription = events.On<BootstrapGreetingWrittenEvent>(greetings.Add);
        using var verified = new VersionVerifiedScope(1);
        var protocol = new WireProtocol("cn-324/game", dialect, dialect.BuildServerPreamble(2, Secret),
            new() { BootstrapStatus = 2, PatchRequestName = "gcpFirstConnect" }, new RejectingRouter(), table,
            events, null, verified, NullLogger.Instance, CancellationToken.None, true);
        var connection = new ProtocolHandshakeTests.LoopbackConnection();
        await connection.ClientInput.CompleteAsync();
        await protocol.RunAsync(connection, CancellationToken.None);
        Assert.Empty(greetings);
        Assert.Equal("bootstrap greeting write stopped", connection.CloseReason);
    }

    [Theory]
    [InlineData("cn-324/game", PacketDirection.ServerToClient, 15, true)]
    [InlineData("kr-1256/game", PacketDirection.ServerToClient, 15, false)]
    [InlineData("kr-1256/game", PacketDirection.ServerToClient, 16, true)]
    [InlineData("cn-324/relay", PacketDirection.ServerToClient, 15, false)]
    [InlineData("cn-324/game", PacketDirection.ClientToServer, 15, false)]
    public async Task CenterStageUsesTheBuildDirectionAndConnectionTable(string protocol, PacketDirection direction, ushort opcode, bool expected)
    {
        var profile = protocol.Split('/')[0];
        var dialect = TestWireDialects.Get(profile);
        var table = OpcodeTableLoader.Load(Path.Combine(Root(), "profiles", profile, "opcodes.yaml"), dialect.Profile);
        using var session = new WireSession(new ProtocolHandshakeTests.LoopbackConnection(), dialect.Profile, protocol, dialect,
            dialect.CreateEncryptor(PacketDirection.ServerToClient), new EventBus(NullLogger.Instance), 1, 1, NullLogger.Instance);
        try
        {
            var stage = ConnectionStageLog.CenterFrame(new FixedRegistry(table), session, protocol, direction, opcode, 22);
            Assert.Equal(expected, stage is not null);
        }
        finally { await session.StopAsync(); }
    }

    private sealed class FixedRegistry(IOpcodeTable table) : IOpcodeTableRegistry
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

    private sealed class RejectingRouter : IPacketRouter
    {
        public IDisposable Register(string protocolName, ushort opcode, PacketHandler handler) => throw new NotSupportedException();
        public ValueTask DispatchAsync(string protocolName, ISession session, InboundPacket packet, CancellationToken ct) =>
            throw new InvalidOperationException("The stopped greeting must not start parsing.");
    }

    private static string Stage(string line) => line.Split("stage=", StringSplitOptions.None)[1].Split(' ')[0].Trim();

    private static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BubbleFighter.Fantasy.sln"))) return dir.FullName;
        throw new InvalidOperationException("Repository root missing.");
    }

    private static async Task<(string Text, byte[] Greeting, byte[] Response)> RunSocketAsync(string profile, bool send, bool center)
    {
        var directory = Directory.CreateTempSubdirectory("bff-stage-").FullName;
        ClosePlugin.Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var file = Path.Combine(directory, "stages.log");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var dialect = TestWireDialects.Get(profile);
        PluginRow Row(string id, string plugin, Dictionary<string, object?>? config = null) => new()
        {
            Id = id, Plugin = plugin, Source = "test", Family = profile == "cn-324" ? "legacy" : "modern",
            ClientBuild = new(dialect.Profile.Region, dialect.Profile.ClientVersionMajor, dialect.Profile.ResourceVersion), Config = config,
        };
        var rows = new List<PluginRow>
        {
            Row("core.protocol-registry", "core.protocol-registry"), Row("core.session-registry", "core.session-registry"),
            Row("core.packet-router", "core.packet-router"), Row("core.client-profiles", "core.client-profiles"),
            Row("core.opcode-tables", "core.opcode-tables"), Row("core.wire-dialects", "core.wire-dialects"),
            Row("dialect", "dialect", TestWireDialects.RowConfig(profile)),
            Row("protocol", "protocol", new()
            {
                ["opcode_table_path"] = Path.Combine(Root(), "profiles", profile, "opcodes.yaml"),
                ["patch_request_name"] = profile == "cn-324" ? "gcpFirstConnect" : "gcpReqGamePatch",
                ["bootstrap_status"] = 2, ["bootstrap_command_line"] = Secret,
                ["alternate_handshake_sequence"] = center ? new List<object?> { "gcpStartGPK", "gcpReqCenterUserCnt" } : new List<object?>(),
            }),
            Row("packet-log", "diagnostics.packet-log", new()
            {
                ["packets_enabled"] = true, ["file"] = file, ["console_packets"] = false, ["catalog_directory"] = "", ["preview_bytes"] = 0,
            }),
            Row("close-probe", "test.stage-close"),
        };
        if (center) rows.Add(Row("reply", "test.center-stage-reply"));
        rows.Add(Row("game", "net.tcp", new()
        {
            ["listen"] = "127.0.0.1:0", ["protocol"] = profile + "/game", ["idle_timeout_seconds"] = 0,
        }));
        var catalog = PluginCatalog.ScanAssemblies([typeof(Dialect.DialectPlugin).Assembly, typeof(ProtocolPlugin).Assembly,
            typeof(PacketLogPlugin).Assembly, typeof(TcpListenerPlugin).Assembly, typeof(PluginHost).Assembly, typeof(ConnectionStageTests).Assembly]);
        var host = new PluginHost(rows, catalog, NullLoggerFactory.Instance);
        byte[] greeting;
        byte[] response = [];
        try
        {
            await host.StartAsync(ct);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ClosePlugin.Port, ct);
            var stream = client.GetStream();
            greeting = new byte[258];
            await stream.ReadExactlyAsync(greeting, ct);
            if (send)
            {
                var encrypt = dialect.CreateEncryptor(PacketDirection.ClientToServer);
                if (center)
                {
                    foreach (var opcode in new ushort[] { 2, 22 })
                    {
                        var frame = dialect.EncodeFrame(opcode, []);
                        encrypt.Apply(frame);
                        await stream.WriteAsync(frame, ct);
                    }
                    var bytes = new byte[5 + Encoding.ASCII.GetByteCount(Secret)];
                    await stream.ReadExactlyAsync(bytes, ct);
                    var decrypt = dialect.CreateDecryptor(PacketDirection.ServerToClient);
                    decrypt.Apply(bytes);
                    response = bytes[5..];
                    client.Client.Shutdown(SocketShutdown.Send);
                }
                else
                {
                    var frame = dialect.EncodeFrame(3, Encoding.ASCII.GetBytes(Secret));
                    encrypt.Apply(frame);
                    await stream.WriteAsync(frame, ct);
                }
            }
            else client.Client.Shutdown(SocketShutdown.Send);
            Assert.Equal(0, await stream.ReadAsync(new byte[1], ct));
            await ClosePlugin.Closed.Task.WaitAsync(ct);
        }
        finally { await host.ShutdownAsync(CancellationToken.None); }
        try { return (await File.ReadAllTextAsync(file, ct), greeting, response); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
