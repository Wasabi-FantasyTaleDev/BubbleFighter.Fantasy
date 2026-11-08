using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

public sealed class CenterPreloginTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task FreshMigrationOnlyRequestsOrdinaryLoginAndCannotRepeat(int stage)
    {
        var (context, packets, table, session) = Fixture();
        try
        {
            var body = new byte[8];
            body[4] = (byte)stage;
            Assert.Equal((ushort)54, table.C2S("gcpReqMoveInto"));
            await packets.DispatchAsync("kr-1256/game", session, new(table.C2S("gcpReqMoveInto"), body), default);
            Assert.Equal(table.S2C("gspRepGamePatch"), Assert.Single(session.Sent));
            Assert.Equal(new byte[8], Assert.Single(session.Bodies));
            Assert.False(session.TryFeature<IPlayerSession>(out _));
            Assert.Equal(1, session.Admission.Calls);
            await Assert.ThrowsAsync<PacketFormatException>(() => packets.DispatchAsync("kr-1256/game", session,
                new(table.C2S("gcpReqMoveInto"), body), default).AsTask());
            Assert.Single(session.Sent);
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(packets.Handlers);
    }

    [Theory]
    [InlineData(0, 0, 7)]
    [InlineData(0, 0, 9)]
    [InlineData(1, 0, 8)]
    [InlineData(-1, 0, 8)]
    [InlineData(0, -1, 8)]
    [InlineData(0, 2, 8)]
    public async Task TruncatedExtendedAndIdentityBearingRequestsGrantNoContinuation(int identity, int stage, int length)
    {
        var (context, packets, table, session) = Fixture();
        try
        {
            var body = new byte[length];
            BitConverter.GetBytes(identity).CopyTo(body, 0);
            if (length >= 8) BitConverter.GetBytes(stage).CopyTo(body, 4);
            await Assert.ThrowsAsync<PacketFormatException>(() => packets.DispatchAsync("kr-1256/game", session,
                new(table.C2S("gcpReqMoveInto"), body), default).AsTask());
            Assert.Empty(session.Sent);
            Assert.Empty(session.Connection.Items);
        }
        finally { await context.UnwindAsync(); }
    }

    [Fact]
    public async Task AnAuthenticatedSessionCannotUseThePreloginPath()
    {
        var (context, packets, table, session) = Fixture();
        try
        {
            session.SetLobbyPlayer(7);
            await Assert.ThrowsAsync<PacketFormatException>(() => packets.DispatchAsync("kr-1256/game", session,
                new(table.C2S("gcpReqMoveInto"), new byte[8]), default).AsTask());
            Assert.Empty(session.Sent);
        }
        finally { await context.UnwindAsync(); }
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(2147483648L)]
    [InlineData(4294967295L)]
    public async Task RetainedIdentityDispatchDoesNotGrantHandshakeContinuation(long identity)
    {
        var messages = new MatchMessagesMountTests.Messages();
        var (context, packets, table, session) = Fixture(migration: true, messages: messages);
        try
        {
            var body = new byte[8];
            BitConverter.GetBytes(checked((uint)identity)).CopyTo(body, 0);
            body[4] = 1;
            await packets.DispatchAsync("kr-1256/game", session, new(table.C2S("gcpReqMoveInto"), body), default);
            Assert.Equal(new MoveIntoRequested(identity, 1), Assert.Single(messages.Received));
            Assert.Equal(0, session.Admission.Calls);
            Assert.Empty(session.Sent);
            Assert.False(session.TryFeature<IPlayerSession>(out _));
            await Assert.ThrowsAsync<PacketFormatException>(() => packets.DispatchAsync("kr-1256/game", session,
                new(table.C2S("gcpReqMoveInto"), body), default).AsTask());
            Assert.Single(messages.Received);
        }
        finally { await context.UnwindAsync(); }
    }

    [Fact]
    public async Task MissingMigrationHandlerFailsWithoutOpeningTheHandshakeGate()
    {
        var messages = new MatchMessagesMountTests.Messages { HasHandler = false };
        var (context, packets, table, session) = Fixture(migration: true, messages: messages);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => packets.DispatchAsync("kr-1256/game", session,
                new(table.C2S("gcpReqMoveInto"), new byte[] { 7, 0, 0, 0, 0, 0, 0, 0 }), default).AsTask());
            Assert.Equal(0, session.Admission.Calls);
            Assert.Empty(session.Sent);
        }
        finally { await context.UnwindAsync(); }
    }

    [Fact]
    public async Task ZeroIdentityStillRequiresTheExplicitPreloginOption()
    {
        var messages = new MatchMessagesMountTests.Messages();
        var (context, packets, table, session) = Fixture(prelogin: false, migration: true, messages: messages);
        try
        {
            await Assert.ThrowsAsync<PacketFormatException>(() => packets.DispatchAsync("kr-1256/game", session,
                new(table.C2S("gcpReqMoveInto"), new byte[8]), default).AsTask());
            Assert.Empty(messages.Received);
            Assert.Equal(0, session.Admission.Calls);
        }
        finally { await context.UnwindAsync(); }
    }

    private static (PluginContext, MatchMessagesMountTests.Packets, IOpcodeTable, Session) Fixture(
        bool prelogin = true, bool migration = false, IMessageRouter? messages = null)
    {
        var profile = new ClientProfile("kr-1256", "kr", 1256, 0, 1304, "kr-1256");
        var root = Directory.GetParent(TestBuilds.PacketsDirectory())!.FullName;
        var table = OpcodeTableLoader.Load(Path.Combine(root, "profiles", "kr-1256", "opcodes.yaml"), profile)
            .ForConnection(OpcodeConnection.Game);
        var packets = new MatchMessagesMountTests.Packets();
        var services = new ServiceRegistry();
        var context = new PluginContext(new PluginRow { Id = "prelogin", Plugin = "test", Source = "test" },
            services, new EventBus(NullLogger.Instance), NullLogger.Instance);
        context.Effect(services.Register<IPacketRouter>(packets));
        if (messages is not null) context.Effect(services.Register<IMessageRouter>(messages));
        new CenterPreloginBinding("kr-1256/game", table, TestBuilds.All.Tables, "kr-1256", prelogin, migration).Bind(context);
        return (context, packets, table, new(profile));
    }

    private sealed class Admission : IInitialHandshakeContinuation
    {
        internal int Calls { get; private set; }
        public void ContinueWithoutVersionVerification() => Calls++;
    }

    private sealed class Session : ISessionWithFeatures
    {
        private readonly ClientProfile _profile;
        internal Admission Admission { get; } = new();
        internal Session(ClientProfile profile)
        {
            _profile = profile;
            _inner = new(profile);
            Features.Register<IInitialHandshakeContinuation>(Admission);
        }
        private readonly MatchMessagesMountTests.Session _inner;
        public ISessionFeatures Features { get; } = new TestSessionFeatures();
        public long Id => _inner.Id;
        public BubbleFighter.Fantasy.Abstractions.Net.IConnection Connection => _inner.Connection;
        public ClientProfile Profile => _profile;
        public System.Collections.Concurrent.ConcurrentDictionary<string, object> Items => _inner.Items;
        internal List<ushort> Sent => _inner.Sent;
        internal List<byte[]> Bodies => _inner.Bodies;
        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
            _inner.SendAsync(opcode, payload, ct);
    }
}
