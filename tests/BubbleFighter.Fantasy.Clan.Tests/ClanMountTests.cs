using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Clan;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Presents;
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
public sealed partial class ClanMountTests
{
    private const string GameProtocol = "kr-1256/game";
    private const string ClanProtocol = "kr-1256/clan";
    private const string CaptureRow = "test.clan-mount-capture";
    private const string CardsRow = "test.clan-mount-cards";

    /// <summary>The 30-member creation card of kr-1256's item table, as the rig's card row lists it.</summary>
    private const int Card30 = 100819;

    /// <summary>The 10-member creation card of kr-1256's item table.</summary>
    private const int Card10 = 101979;
    private const long UserNum = 42;

    /// <summary>
    /// The passport request of a logged-in session is answered with the passport the client echoes as its clan
    /// login (reader kr-1256 sub_99C1D0): the account id, the counted nickname, two zero words and a 32-byte ticket.
    /// </summary>
    [Fact]
    public async Task PassportRequestAnswersThePassportWithATicket()
    {
        await using var rig = await Rig.StartAsync();
        await rig.DispatchAsync("gcpReqClanPassport", []);
        Assert.True(rig.WasHandled("gcpReqClanPassport"));

        var frame = Assert.Single(rig.Session.Frames);
        Assert.Equal(rig.Table.S2C("gspClanPassport"), frame.Opcode);
        // 4 (id) + 4 + 7 * 2 (counted "clanner") + 4 + 4 + 4 (count) + 32 (ticket) = 66 bytes.
        Assert.Equal(66, frame.Payload.Length);
        var reader = new PacketReader(frame.Payload);
        Assert.Equal((uint)UserNum, reader.ReadUInt32());
        Assert.Equal("clanner", reader.ReadWideStringPrefixed());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(0u, reader.ReadUInt32());
        Assert.Equal(32u, reader.ReadUInt32());
        Assert.Equal(32, reader.Remaining);
        Assert.Equal(new ClanOpenRecord(UserNum, ClanOpenKind.ClanServerAck), await rig.LastOpenAsync());
    }

    /// <summary>
    /// With passport_on_login (the kr-812 family, which asks for a passport only once it is logged in to the clan server,
    /// cn-717 sub_4C6E70 case 1002) the passport goes out right after the game login; without it nothing is sent unasked.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PassportIsSentAfterTheGameLoginWhereTheClientCannotAsk(bool passportOnLogin)
    {
        await using var rig = await Rig.StartAsync(passportOnLogin);
        var login = new LoginSucceeded((int)UserNum, "account", "clanner", 1, DateTime.UtcNow, 300001, [300001], null, null, null);
        await rig.Capture.Events.SerialAsync(new LoginMenuInstalled(GameProtocol, rig.Session, login), CancellationToken.None);
        if (!passportOnLogin)
        {
            Assert.Empty(rig.Session.Frames);
            return;
        }
        var frame = Assert.Single(rig.Session.Frames);
        Assert.Equal(rig.Table.S2C("gspClanPassport"), frame.Opcode);
        Assert.Equal(66, frame.Payload.Length);
    }

    /// <summary>A session that has not logged in has no account to state: the passport request sends nothing.</summary>
    [Fact]
    public async Task PassportRequestBeforeLoginSendsNothing()
    {
        await using var rig = await Rig.StartAsync();
        var anonymous = new RecordingSession(loggedIn: false, connectionId: 8);
        await rig.DispatchAsync(GameProtocol, anonymous, rig.OpcodeOf("gcpReqClanPassport"), []);

        Assert.Empty(anonymous.Frames);
        Assert.Null(await rig.LastOpenAsync());
    }

    /// <summary>
    /// The clan connection is greeted on every heartbeat until it logs in, then accepts the echoed passport once
    /// (dispatcher kr-1256 sub_525170 cases 0x3EA and 0x3EE): hello, result 1, and no further hello.
    /// </summary>
    [Fact]
    public async Task ClanConnectionLogsInWithTheEchoedPassport()
    {
        await using var rig = await Rig.StartAsync();
        var clan = new RecordingSession(loggedIn: false, connectionId: 9);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanHeartbeat"), []);
        Assert.Equal([(rig.ClanTable.S2C("ClanHello"), "")], clan.Pins());

        await rig.DispatchAsync("gcpReqClanPassport", []);
        var login = EchoPassport(Assert.Single(rig.Session.Frames).Payload);
        // 4 (id) + 16 (NUL-terminated "clanner") + 4 + 4 + 4 (count) + 32 (ticket) = 64 bytes.
        Assert.Equal(64, login.Length);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanLogin"), login);
        Assert.Equal((rig.ClanTable.S2C("ClanLoginResult"), "01000000"), clan.Pins()[^1]);
        Assert.Null(clan.Closed);

        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanHeartbeat"), []);
        Assert.Equal(2, clan.Frames.Count);
    }

    /// <summary>A ticket is single-use: the same login on a second clan connection is refused with -1 and closed.</summary>
    [Fact]
    public async Task ClanLoginTicketCannotBeReplayed()
    {
        await using var rig = await Rig.StartAsync();
        await rig.DispatchAsync("gcpReqClanPassport", []);
        var login = EchoPassport(Assert.Single(rig.Session.Frames).Payload);
        var first = new RecordingSession(loggedIn: false, connectionId: 9);
        var second = new RecordingSession(loggedIn: false, connectionId: 10);
        await rig.DispatchAsync(ClanProtocol, first, rig.ClanTable.C2S("ClanLogin"), login);
        await rig.DispatchAsync(ClanProtocol, second, rig.ClanTable.C2S("ClanLogin"), login);

        Assert.Equal([(rig.ClanTable.S2C("ClanLoginResult"), "01000000")], first.Pins());
        Assert.Equal([(rig.ClanTable.S2C("ClanLoginResult"), "FFFFFFFF")], second.Pins());
        Assert.NotNull(second.Closed);
    }

    /// <summary>A login whose ticket is not the issued one is answered -1 (authentication not completed) and closed.</summary>
    [Fact]
    public async Task ClanLoginWithAWrongTicketIsRefused()
    {
        await using var rig = await Rig.StartAsync();
        await rig.DispatchAsync("gcpReqClanPassport", []);
        var login = EchoPassport(Assert.Single(rig.Session.Frames).Payload);
        login[^1] ^= 0xFF;
        var clan = new RecordingSession(loggedIn: false, connectionId: 9);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanLogin"), login);

        Assert.Equal([(rig.ClanTable.S2C("ClanLoginResult"), "FFFFFFFF")], clan.Pins());
        Assert.NotNull(clan.Closed);
    }

    /// <summary>A logged-in clan connection is closed on the heartbeat after its game session ended.</summary>
    [Fact]
    public async Task ClanConnectionEndsWithItsGameSession()
    {
        await using var rig = await Rig.StartAsync();
        await rig.DispatchAsync("gcpReqClanPassport", []);
        var clan = new RecordingSession(loggedIn: false, connectionId: 9);
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanLogin"), EchoPassport(rig.Session.Frames[0].Payload));
        Assert.Null(clan.Closed);

        await rig.Session.Connection.CloseAsync("test");
        await rig.DispatchAsync(ClanProtocol, clan, rig.ClanTable.C2S("ClanHeartbeat"), []);
        Assert.Equal("game session ended", clan.Closed);
    }

    /// <summary>
    /// Rebuilds the clan login the way the client does from a passport (kr-1256 sub_9D2FF0 case 21 into
    /// kr-1256 sub_525DF0): the same five pieces, the string NUL-terminated instead of counted.
    /// </summary>
    private static byte[] EchoPassport(byte[] passport)
    {
        var reader = new PacketReader(passport);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteUInt32(reader.ReadUInt32());
        writer.WriteWideCString(reader.ReadWideStringPrefixed());
        writer.WriteUInt32(reader.ReadUInt32());
        writer.WriteUInt32(reader.ReadUInt32());
        var count = reader.ReadUInt32();
        writer.WriteUInt32(count);
        writer.WriteBytes(reader.ReadBytes((int)count));
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The clan-window open answers the empty mark list and guild info, in that order, and no passport.</summary>
    [Fact]
    public async Task MainOpenSendsTheTwoEmptyOpenSnapshots()
    {
        await using var rig = await Rig.StartAsync();
        await rig.DispatchAsync("gcpReqClanMainOpen_", []);
        Assert.True(rig.WasHandled("gcpReqClanMainOpen_"));

        Assert.Equal(
            [
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
        await rig.DispatchAsync("gcpReqClanAttendanceCheckIn_", []);
        Assert.True(rig.WasHandled("gcpReqClanAttendanceCheckIn_"));

        Assert.Equal(["gspClanAttendanceState=" + new string('0', 28)], rig.FramePins());
        Assert.Equal(new ClanOpenRecord(UserNum, ClanOpenKind.Attendance), await rig.LastOpenAsync());
    }

    /// <summary>Any request body with a byte is a format error before the open or a frame exists.</summary>
    [Theory]
    [InlineData("gcpReqClanPassport")]
    [InlineData("gcpReqClanMainOpen_")]
    [InlineData("gcpReqClanAttendanceCheckIn_")]
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
        var requests = new[] { "gcpReqClanPassport", "gcpReqClanMainOpen_", "gcpReqClanAttendanceCheckIn_" }
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
    private static List<PluginRow> Rows(bool passportOnLogin = false) =>
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
        Row("cards", CardsRow),
        Row("clan.connections", "clan.connections"),
        // The disband sweep runs every second so the final disband can be seen without waiting a minute.
        Row("clan", "clan", new() { ["protocol"] = GameProtocol, ["disband_check_seconds"] = 1, ["passport_on_login"] = passportOnLogin }),
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
        Descriptor("clan.connections", typeof(ClanConnectionsPlugin)),
        Descriptor(CaptureRow, typeof(ClanMountCapturePlugin)),
        Descriptor(CardsRow, typeof(ClanMountCardsPlugin)),
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
    [Plugin(CaptureRow, Requires = [typeof(IPacketRouter), typeof(IClanStore), typeof(IClanDirectory), typeof(IInventoryRepository),
        typeof(IOpcodeTableRegistry)])]
    public sealed class ClanMountCapturePlugin : IPlugin
    {
        /// <summary>The active host's capture, cleared when the row unloads.</summary>
        internal static ClanMountCapturePlugin? Current { get; private set; }

        /// <summary>Receives the packet router the clan frames are dispatched through.</summary>
        internal IPacketRouter Router { get; private set; } = null!;

        /// <summary>Receives the store the clan row records its opens in.</summary>
        internal IClanStore Store { get; private set; } = null!;

        /// <summary>Receives the directory the clan row reads, so a test can store clans the way another node would.</summary>
        internal IClanDirectory Clans { get; private set; } = null!;

        /// <summary>Receives the stored bags the clan row takes creation cards from.</summary>
        internal IInventoryRepository Inventory { get; private set; } = null!;

        /// <summary>Receives the table registry the request opcodes are resolved through.</summary>
        internal IOpcodeTableRegistry Tables { get; private set; } = null!;

        internal IEventBus Events { get; private set; } = null!;

        /// <summary>Gets whether each dispatched opcode had a registered handler.</summary>
        internal Dictionary<ushort, bool> Handled { get; } = [];

        /// <inheritdoc />
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            Router = context.Services.Get<IPacketRouter>();
            Store = context.Services.Get<IClanStore>();
            Clans = context.Services.Get<IClanDirectory>();
            Inventory = context.Services.Get<IInventoryRepository>();
            Tables = context.Services.Get<IOpcodeTableRegistry>();
            Events = context.Events;
            context.Effect(context.Events.On<PacketReceivedEvent>(e => Handled[e.Packet.Opcode] = e.HasHandler));
            context.Effect(() => Current = null);
            Current = this;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Publishes kr-1256's two creation cards through the real ShopItem parser, from a sheet holding only the
    /// columns it reads and one item of another function.
    /// </summary>
    [Plugin(CardsRow, Provides = [typeof(IClanCreationCards)])]
    public sealed class ClanMountCardsPlugin : IPlugin
    {
        /// <inheritdoc />
        public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
        {
            context.Effect(context.Services.Register(ClanCreationCardsPlugin.Parse(
            [
                ["itemId", "function", "parameter"],
                [Card30.ToString(CultureInfo.InvariantCulture), "22", "30"],
                [Card10.ToString(CultureInfo.InvariantCulture), "22", "10"],
                ["100001", "1", "30"],
            ], 8)));
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

        internal IOpcodeTable ClanTable => Capture.Tables.ForProtocol(ClanProtocol);

        internal static async Task<Rig> StartAsync(bool passportOnLogin = false)
        {
            var host = new PluginHost(Rows(passportOnLogin), Catalog(), NullLoggerFactory.Instance);
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

        internal Task DispatchAsync(ushort opcode, byte[] body) => DispatchAsync(GameProtocol, Session, opcode, body);

        internal Task DispatchAsync(string protocol, ISession session, ushort opcode, byte[] body) =>
            Capture.Router.DispatchAsync(protocol, session, new InboundPacket(opcode, body), CancellationToken.None).AsTask();

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

    /// <summary>A session that records every frame the row sends: the logged-in game session, or a bare connection.</summary>
    private sealed class RecordingSession : ISessionWithFeatures
    {
        private readonly TestConnection _connection;

        internal RecordingSession(bool loggedIn = true, long connectionId = 7, long userNum = UserNum, string nickname = "clanner")
        {
            _connection = new TestConnection(connectionId);
            if (loggedIn)
                this.SetLobbyPlayer(userNum, nickname);
        }

        internal List<(ushort Opcode, byte[] Payload)> Frames { get; } = [];

        internal string? Closed => _connection.Closed;

        internal (ushort Opcode, string Hex)[] Pins() =>
            Frames.Select(frame => (frame.Opcode, Convert.ToHexString(frame.Payload))).ToArray();

        public long Id => _connection.Id;
        public IConnection Connection => _connection;
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
    private sealed class TestConnection(long id) : IConnection
    {
        internal string? Closed { get; private set; }
        public long Id => id;
        public EndPoint RemoteEndPoint => new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => Closed is not null;
        public ValueTask CloseAsync(string reason)
        {
            Closed ??= reason;
            return ValueTask.CompletedTask;
        }
    }
}
