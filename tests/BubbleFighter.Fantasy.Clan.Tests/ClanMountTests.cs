using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Clan;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Dialect;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Persistence.Memory;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

/// <summary>
/// Mounts the clan row beside the shared message row on a real plugin host and pins what the client sees
/// after slice S9 moved the plugin onto typed messages: the three empty requests dispatch to the same frames
/// in the same order as before the move, a malformed body is rejected before the open is stored or a frame is
/// sent, and no neighbour opcode reaches a clan handler. This replaces the pre-move <c>ClanRequestParseTests</c>
/// and <c>ClanPluginWirePins</c>.
/// </summary>
public sealed class ClanMountTests
{
    private const string GameProtocol = "kr-1256/game";
    private const string CaptureRow = "test.clan-mount-capture";
    private const long UserNum = 42;

    /// <summary>The clan-server bounce records the open and sends nothing: no game-port reply is proven.</summary>
    [Fact]
    public async Task ClanServerAckSendsNoFrame()
    {
        await using var rig = await Rig.StartAsync();
        await rig.DispatchAsync("Unknown25", []);
        Assert.True(rig.WasHandled("Unknown25"));

        Assert.Empty(rig.FramePins());
        Assert.Equal(new ClanOpenRecord(UserNum, ClanOpenKind.ClanServerAck), await rig.LastOpenAsync());
    }

    /// <summary>The clan-window open answers the empty passport, mark list and guild info, in that order.</summary>
    [Fact]
    public async Task MainOpenSendsTheThreeEmptyOpenSnapshotsInTheOldOrder()
    {
        await using var rig = await Rig.StartAsync();
        await rig.DispatchAsync("Unknown2040", []);
        Assert.True(rig.WasHandled("Unknown2040"));

        Assert.Equal(
            [
                // gspClanPassport (S2C 21): empty name, word, word, empty byte list = 16 bytes.
                "gspClanPassport=00000000000000000000000000000000",
                // gspReqClanMarkInfo (S2C 140): word, empty counted table = 8 bytes.
                "gspReqClanMarkInfo=0000000000000000",
                // gspResGuildInfo (S2C 918): i64, bool32 false, bool32 false = 16 bytes.
                "gspResGuildInfo=00000000000000000000000000000000",
            ],
            rig.FramePins());
        Assert.Equal(new ClanOpenRecord(UserNum, ClanOpenKind.MainOpen), await rig.LastOpenAsync());
    }

    /// <summary>The attendance check-in answers the empty attendance state only.</summary>
    [Fact]
    public async Task AttendanceCheckInSendsTheEmptyAttendanceState()
    {
        await using var rig = await Rig.StartAsync();
        await rig.DispatchAsync("Unknown2041", []);
        Assert.True(rig.WasHandled("Unknown2041"));

        Assert.Equal(["gspClanAttendanceState=00000000"], rig.FramePins());
        Assert.Equal(new ClanOpenRecord(UserNum, ClanOpenKind.Attendance), await rig.LastOpenAsync());
    }

    /// <summary>Any request body with a byte is a format error before the open or a frame exists.</summary>
    [Theory]
    [InlineData("Unknown25")]
    [InlineData("Unknown2040")]
    [InlineData("Unknown2041")]
    public async Task NonEmptyRequestBodyIsRejectedBeforeAnyRecordOrFrame(string request)
    {
        await using var rig = await Rig.StartAsync();
        var error = await Assert.ThrowsAsync<PacketFormatException>(() => rig.DispatchAsync(request, [0x00]));

        Assert.Equal(0, error.Offset);
        Assert.Empty(rig.FramePins());
        Assert.Null(await rig.LastOpenAsync());
    }

    /// <summary>Opcode neighbours of the three requests reach no clan handler and send nothing.</summary>
    [Fact]
    public async Task NeighbourOpcodesReachNoClanHandler()
    {
        await using var rig = await Rig.StartAsync();
        var requests = new[] { "Unknown25", "Unknown2040", "Unknown2041" }
            .Select(rig.OpcodeOf)
            .ToHashSet();
        var neighbours = requests
            .SelectMany(opcode => new[] { (ushort)(opcode - 1), (ushort)(opcode + 1) })
            .Where(opcode => !requests.Contains(opcode))
            .Distinct()
            .ToArray();
        Assert.NotEmpty(neighbours);

        foreach (var opcode in neighbours)
        {
            await rig.DispatchAsync(opcode, []);
            Assert.False(rig.WasHandled(opcode), $"Opcode {opcode} reached a clan handler.");
        }

        Assert.Empty(rig.FramePins());
        Assert.Null(await rig.LastOpenAsync());
    }

    /// <summary>The row set the scenario hosts use, with the clan row served by a mounted clan-enabled message row.</summary>
    private static List<PluginRow> Rows() =>
    [
        Row("core.protocol-registry", "core.protocol-registry"),
        Row("core.session-registry", "core.session-registry"),
        Row("core.packet-router", "core.packet-router"),
        Row("core.message-router", "core.message-router"),
        Row("core.client-profiles", "core.client-profiles"),
        Row("core.opcode-tables", "core.opcode-tables"),
        Row("core.wire-dialects", "core.wire-dialects"),
        Row("persistence", "persistence.memory"),
        Row("dialect", "dialect", TestWireDialects.RowConfig("kr-1256")),
        // Only the clan option is on: this row must publish the clan binding and senders, nothing else.
        SharedDialectRows.Kr1256(new()
        {
            ["clan"] = true,
            ["login"] = false,
            ["main_menu_init"] = false,
            ["equipment"] = false,
            ["equipment_view"] = false,
        }),
        Row("protocol", "protocol", new() { ["opcode_table_path"] = Path.Combine(RepoRoot(), "profiles", "kr-1256", "opcodes.yaml") }),
        Row("clan", "clan", new() { ["protocol"] = GameProtocol }),
        Row("capture", CaptureRow),
    ];

    private static PluginRow Row(string id, string plugin, Dictionary<string, object?>? config = null) =>
        new()
        {
            Id = id, Plugin = plugin, Source = "clan-mount-test",
            ClientBuild = new("kr", 1256, 1304), Family = "modern", Config = config,
        };

    private static PluginCatalog Catalog() => PluginCatalog.FromDefinitions(
    [
        Descriptor("core.protocol-registry", typeof(ConnectionProtocolRegistryPlugin)),
        Descriptor("core.session-registry", typeof(SessionRegistryPlugin)),
        Descriptor("core.packet-router", typeof(PacketRouterPlugin)),
        Descriptor("core.message-router", typeof(MessageRouterPlugin)),
        Descriptor("core.client-profiles", typeof(ClientProfileRegistryPlugin)),
        Descriptor("core.opcode-tables", typeof(OpcodeTableRegistryPlugin)),
        Descriptor("core.wire-dialects", typeof(WireDialectRegistryPlugin)),
        Descriptor("persistence.memory", typeof(MemoryPersistencePlugin)),
        Descriptor("dialect", typeof(DialectPlugin)),
        Descriptor("dialect.messages", typeof(MessagesPlugin)),
        Descriptor("protocol", typeof(ProtocolPlugin)),
        Descriptor("clan", typeof(ClanPlugin)),
        Descriptor(CaptureRow, typeof(ClanMountCapturePlugin)),
    ]);

    private static PluginDescriptor Descriptor(string id, Type type) => new(id, null, type, "clan-mount-test", null);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BubbleFighter.Fantasy.sln")))
                return dir.FullName;
        throw new InvalidOperationException("The repository root was not found above the test output folder.");
    }

    /// <summary>
    /// Publishes the host's packet router, opcode table and clan store to the test as the clan row's
    /// neighbours, and records the <c>PacketReceivedEvent</c> so a dispatch can prove whether a handler existed.
    /// </summary>
    [Plugin(CaptureRow, Requires = [typeof(IPacketRouter), typeof(IClanStore), typeof(IOpcodeTableRegistry)])]
    public sealed class ClanMountCapturePlugin : IPlugin
    {
        /// <summary>The active host's capture, cleared when the row unloads.</summary>
        internal static ClanMountCapturePlugin? Current { get; private set; }

        /// <summary>Receives the packet router the clan frames are dispatched through.</summary>
        internal IPacketRouter Router { get; private set; } = null!;

        /// <summary>Receives the store the clan row records its opens in.</summary>
        internal IClanStore Store { get; private set; } = null!;

        /// <summary>Receives the table registry the request opcodes are resolved through.</summary>
        internal IOpcodeTableRegistry Tables { get; private set; } = null!;

        /// <summary>Gets whether each dispatched opcode had a registered handler.</summary>
        internal Dictionary<ushort, bool> Handled { get; } = [];

        /// <inheritdoc />
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Router = context.Services.Get<IPacketRouter>();
            Store = context.Services.Get<IClanStore>();
            Tables = context.Services.Get<IOpcodeTableRegistry>();
            context.Effect(context.Events.On<PacketReceivedEvent>(e => Handled[e.Packet.Opcode] = e.HasHandler));
            context.Effect(() => Current = null);
            Current = this;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The started host and the session its clan frames are dispatched for.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly PluginHost _host;

        private Rig(PluginHost host) => _host = host;

        internal PluginHost Host => _host;

        internal ClanMountCapturePlugin Capture => ClanMountCapturePlugin.Current
            ?? throw new InvalidOperationException("The capture row is not active.");

        internal RecordingSession Session { get; } = new();

        internal IOpcodeTable Table => Capture.Tables.ForProtocol(GameProtocol);

        internal static async Task<Rig> StartAsync()
        {
            var host = new PluginHost(Rows(), Catalog(), NullLoggerFactory.Instance);
            try
            {
                await host.StartAsync(CancellationToken.None);
            }
            catch
            {
                await host.ShutdownAsync(CancellationToken.None);
                throw;
            }

            return new Rig(host);
        }

        internal ushort OpcodeOf(string request) => Table.C2S(request);

        internal Task DispatchAsync(string request, byte[] body) => DispatchAsync(OpcodeOf(request), body);

        internal Task DispatchAsync(ushort opcode, byte[] body) =>
            Capture.Router.DispatchAsync(GameProtocol, Session, new InboundPacket(opcode, body), CancellationToken.None).AsTask();

        internal bool WasHandled(string request) => WasHandled(OpcodeOf(request));

        internal bool WasHandled(ushort opcode) => Capture.Handled[opcode];

        internal string[] FramePins() =>
            Session.Frames.Select(frame => $"{NameOf(frame.Opcode)}={Convert.ToHexString(frame.Payload)}").ToArray();

        internal ValueTask<ClanOpenRecord?> LastOpenAsync() => Capture.Store.FindLastOpenAsync(UserNum, CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await _host.ShutdownAsync(CancellationToken.None);
        }

        private string NameOf(ushort opcode) =>
            Table.TryGet(PacketDirection.ServerToClient, opcode, out var entry) && entry is not null
                ? entry.Name
                : throw new InvalidOperationException($"The build's opcode table names no S2C opcode {opcode}.");
    }

    /// <summary>A logged-in game session that records every frame the row sends.</summary>
    private sealed class RecordingSession : ISessionWithFeatures
    {
        internal RecordingSession() => this.SetLobbyPlayer(UserNum, "clanner");

        internal List<(ushort Opcode, byte[] Payload)> Frames { get; } = [];
        public long Id => 1;
        public IConnection Connection { get; } = new TestConnection();
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public ClientProfile Profile { get; } = new("kr-1256", "kr", 1256, 0, 0, "clan-mount-test");
        public ConcurrentDictionary<string, object> Items { get; } = new();

        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            Frames.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A live connection identity; the pipes are unused by these handlers.</summary>
    private sealed class TestConnection : IConnection
    {
        public long Id => 7;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
