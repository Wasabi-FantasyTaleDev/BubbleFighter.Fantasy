using BubbleFighter.Fantasy.MatchRelay;
using BubbleFighter.Fantasy.Contracts.Rooms;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

public sealed partial class MatchActionRelayTests
{
    // Independent length and identity pins from the client readers, not the production table.
    public static TheoryData<int, int, int, bool> Layouts => new()
    {
        { 32779, 8, 0, false }, { 32780, 10, 0, false }, { 32781, 12, 0, false },
        { 32784, 16, 0, false }, { 32791, 12, 0, false }, { 32792, 4, 0, true },
        { 32788, 24, 0, false }, { 32790, 4, 0, false },
        { 32810, 24, 8, false }, { 32813, 4, 0, false },
        { 32815, 8, 0, false }, { 32816, 8, 0, false },
        { 32835, 4, 0, false }, { 32836, 4, 0, true }, { 32837, 4, 0, true },
        { 32882, 12, 0, false }, { 32921, 12, 0, false },
        { 38271, 8, 4, true }, { 38272, 8, 0, true }, { 38273, 13, 0, true },
        { 38274, 4, 0, true }, { 38277, 32, 8, true }, { 38280, 9, 4, true },
        { 38281, 4, 0, true }, { 38282, 40, 0, true }, { 38283, 19, -1, true },
        { 32782, 4, 0, false }, { 32783, 8, 4, false }, { 32786, 9, 0, false },
        { 32811, 8, 0, false }, { 32821, 6, 0, false }, { 32834, 8, 0, false },
        { 32884, 16, 0, false }, { 32907, 4, -1, true }, { 32925, 32, 0, false },
        { 33286, 12, 0, false }, { 33301, 24, 16, false }, { 38284, 8, 0, false },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task RealPluginRoutesBothListenersWithPinnedBodies(int opcode, int length, int actorOffset, bool echo)
    {
        using var h = await Harness.CreateAsync();
        var bytes = Body(length, actorOffset);
        var layout = MatchAction1256.Read((ushort)opcode, bytes);
        Assert.Equal(length, layout.BodyBytes);
        Assert.Equal(actorOffset, layout.ActorOffset);
        Assert.Equal(echo, layout.EchoSender);
        foreach (var protocol in new[] { "kr-1256/game", "kr-1256/relay" })
        {
            await h.DispatchAsync((ushort)opcode, bytes, protocol: protocol);
            Assert.Equal((ushort)opcode, h.Guest.Sent.Last().Opcode);
            Assert.Equal(bytes, h.Guest.Sent.Last().Body);
        }
        Assert.Equal(2, h.Guest.Sent.Count);
        Assert.Equal(echo ? 2 : 0, h.Host.Sent.Count);
        Assert.Empty(h.HostGame.Sent);
        Assert.Empty(h.GuestGame.Sent);
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task WrongLengthsCannotReachPeers(int opcode, int length, int actorOffset, bool echo)
    {
        _ = echo;
        using var h = await Harness.CreateAsync();
        foreach (var size in new[] { length - 1, length + 1 })
            await Assert.ThrowsAsync<PacketFormatException>(() => h.DispatchAsync((ushort)opcode, new byte[size]));
        Assert.Empty(h.Guest.Sent);
        Assert.Throws<PacketFormatException>(() => MatchAction1256.Read((ushort)opcode, Body(length + 1, actorOffset)));
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task ActorValidationUsesTheOwnerFieldAndNotTheTarget(int opcode, int length, int actorOffset, bool echo)
    {
        _ = echo;
        using var h = await Harness.CreateAsync();
        if (actorOffset < 0)
        {
            // Damager keys are not character ids; key 7 must survive unchanged.
            var cleanup = new byte[length];
            BinaryPrimitives.WriteInt32LittleEndian(cleanup, 7);
            await h.DispatchAsync((ushort)opcode, cleanup);
            Assert.Equal(cleanup, Assert.Single(h.Host.Sent).Body);
            Assert.Equal(cleanup, Assert.Single(h.Guest.Sent).Body);
            return;
        }
        await Assert.ThrowsAsync<PacketFormatException>(() =>
            h.DispatchAsync((ushort)opcode, Body(length, actorOffset, 1100000)));
        Assert.Empty(h.Guest.Sent);
    }

    [Fact]
    public async Task ShootingHitAndCleanupKeepTheirOrderWithoutDuplicatingTheShot()
    {
        using var h = await Harness.CreateAsync();
        await h.DispatchAsync(32925, Body(32, 0));
        await h.DispatchAsync(33301, Body(24, 16));
        await h.DispatchAsync(32907, BitConverter.GetBytes(7));
        Assert.Equal(new ushort[] { 32925, 33301, 32907 }, h.Guest.Sent.Select(s => s.Opcode));
        Assert.Equal((ushort)32907, Assert.Single(h.Host.Sent).Opcode);
    }

    [Theory]
    [InlineData(1000004)]
    [InlineData(1000028)]
    [InlineData(-1)]
    public async Task UnregisteredEntitiesAreRejectedWithoutBreakingSubsequentPlayerActions(int entity)
    {
        using var h = await Harness.CreateAsync();
        await h.DispatchAsync(38281, Body(4, 0, entity));
        Assert.Empty(h.Host.Sent);
        Assert.Empty(h.Guest.Sent);
        await h.DispatchAsync(32925, Body(32, 0));
        Assert.Equal((ushort)32925, Assert.Single(h.Guest.Sent).Opcode);
        Assert.False(h.Host.Connection.IsClosed);
    }

    [Fact]
    public async Task GuestActionsUseTheirOwnEntityAndReachTheHost()
    {
        using var h = await Harness.CreateAsync();
        var shot = Body(32, 0, 1100000);
        var hit = Body(24, 16, 1100000);
        await h.DispatchAsync(32925, shot, h.Guest);
        await h.DispatchAsync(33301, hit, h.Guest);
        Assert.Equal(new ushort[] { 32925, 33301 }, h.Host.Sent.Select(s => s.Opcode));
        Assert.Equal(shot, h.Host.Sent[0].Body);
        Assert.Equal(hit, h.Host.Sent[1].Body);
        Assert.Empty(h.Guest.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendFailuresAreSuppressedOnlyForClosedRecipients(bool closed)
    {
        using var h = await Harness.CreateAsync();
        h.Guest.BeforeSend = async () =>
        {
            if (closed) await h.Guest.Connection.CloseAsync("test disconnect");
            throw new IOException("test send failure");
        };
        if (closed) await h.DispatchAsync(32925, Body(32, 0));
        else await Assert.ThrowsAsync<IOException>(() => h.DispatchAsync(32925, Body(32, 0)));
        Assert.Empty(h.Guest.Sent);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("unbound")]
    [InlineData("sender-uninitialized")]
    [InlineData("peer-uninitialized")]
    [InlineData("peer-missing")]
    [InlineData("sender-closed")]
    [InlineData("peer-closed")]
    [InlineData("main-closed")]
    [InlineData("result")]
    [InlineData("starting")]
    public async Task InvalidOrUninitializedTransportsNeverReceiveActions(string state)
    {
        using var h = await Harness.CreateAsync();
        var sender = h.Host;
        switch (state)
        {
            case "main": sender = h.HostGame; break;
            case "unbound": sender = new RecordingSession(99); break;
            case "sender-uninitialized": h.HostSeat.RelayInitialized = false; break;
            case "peer-uninitialized": h.GuestSeat.RelayInitialized = false; break;
            case "peer-missing": h.Registry.Release(h.Guest); break;
            case "sender-closed": await h.Host.Connection.CloseAsync("test"); break;
            case "peer-closed": await h.Guest.Connection.CloseAsync("test"); break;
            case "main-closed": await h.GuestGame.Connection.CloseAsync("test"); break;
            case "result": h.Room.MarkResult(); break;
            case "starting": h.Room.ResetToWaiting(); h.Room.MarkStarting(); break;
        }
        await h.DispatchAsync(32925, Body(32, 0), sender);
        Assert.Empty(h.Guest.Sent);
        Assert.Empty(h.GuestGame.Sent);
    }

    [Fact]
    public async Task DifferentRoomsAndReplacedConnectionsAreIsolated()
    {
        using var h = await Harness.CreateAsync();
        var otherGame = new RecordingSession(30);
        var (otherRoom, otherSeat) = h.Registry.Create(otherGame, RoomWireSupport.OrdinarySettingWithMap(), 3, "other");
        otherRoom.MarkStarting();
        var otherRelay = await h.BindAsync(otherSeat, 31);
        otherRoom.MarkPlaying();
        var replacement = await h.BindAsync(h.GuestSeat, 40);
        await h.DispatchAsync(32925, Body(32, 0));
        Assert.Single(replacement.Sent);
        Assert.Empty(h.Guest.Sent);
        Assert.Empty(otherRelay.Sent);
        await h.DispatchAsync(32925, Body(32, 0, 1100000), h.Guest);
        Assert.Empty(h.Host.Sent);
    }

    [Theory]
    [InlineData("replace-peer")]
    [InlineData("release-sender")]
    [InlineData("new-round")]
    public async Task InFlightFanoutRechecksConnectionsAndRoundBeforeEachSend(string change)
    {
        using var h = await Harness.CreateAsync();
        // Cleanup echoes to the sender first, allowing the next peer to change while a send awaits.
        RecordingSession? replacement = null;
        h.Host.BeforeSend = async () =>
        {
            switch (change)
            {
                case "replace-peer": replacement = await h.BindAsync(h.GuestSeat, 50); break;
                case "release-sender": h.Registry.Release(h.Host); break;
                case "new-round": h.Room.ResetToWaiting(); h.Room.MarkStarting(); break;
            }
        };
        await h.DispatchAsync(32907, BitConverter.GetBytes(7));
        Assert.Empty(h.Guest.Sent);
        if (replacement is not null) Assert.Empty(replacement.Sent);
    }

    [Fact]
    public async Task PeerDisconnectDuringAnAwaitedSendIsSkipped()
    {
        using var h = await Harness.CreateAsync();
        h.Host.BeforeSend = async () =>
        {
            await h.Guest.Connection.CloseAsync("test disconnect");
        };
        await h.DispatchAsync(32907, BitConverter.GetBytes(7));
        Assert.Single(h.Host.Sent);
        Assert.Empty(h.Guest.Sent);
    }

    [Fact]
    public async Task UnloadRemovesBothListenerRegistrationsAndUnknownPacketsStayUnhandled()
    {
        using var h = await Harness.CreateAsync();
        var events = new List<PacketReceivedEvent>();
        using var subscription = h.Events.On<PacketReceivedEvent>(events.Add);
        await h.DispatchAsync(32840, new byte[8]);
        Assert.False(Assert.Single(events).HasHandler);
        h.Relay.Unwind();
        h.InGame.Unwind();
        foreach (var protocol in new[] { "kr-1256/game", "kr-1256/relay" })
            await h.DispatchAsync(32925, Body(32, 0), protocol: protocol);
        Assert.All(events, e => Assert.False(e.HasHandler));
        Assert.Empty(h.Guest.Sent);
    }

    private static byte[] Body(int length, int actorOffset, int actor = 1000000)
    {
        var body = Enumerable.Range(0, length).Select(i => (byte)(i + 1)).ToArray();
        if (length == 32) body[4] = 0;
        if (actorOffset >= 0) BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(actorOffset), actor);
        return body;
    }

    [Fact]
    public async Task HordeRoundsForwardNpcActionsTheHostReports()
    {
        using var h = await Harness.CreateAsync();
        h.Room.ModeId = 101;
        // The host registers NPC entity 1000004 with C2S 38281.
        var body = BitConverter.GetBytes(1000004);
        await h.DispatchAsync(38281, body);
        Assert.Equal(body, Assert.Single(h.Guest.Sent).Body);
        Assert.Equal(body, Assert.Single(h.Host.Sent).Body);
    }

    [Fact]
    public async Task OrdinaryRoundsStillRejectActionsForEntitiesNoSeatOwns()
    {
        using var h = await Harness.CreateAsync();
        h.Room.ModeId = 1;
        await h.DispatchAsync(38281, BitConverter.GetBytes(1000004));
        Assert.Empty(h.Guest.Sent);
        Assert.Empty(h.Host.Sent);
    }

    [Fact]
    public async Task KnockbackAndRecoverySynchronizeTheAffectedCharacterWithoutEchoingIt()
    {
        using var h = await Harness.CreateAsync();
        // The affected player reports a different damage source.
        var body = Convert.FromHexString("40420F00E0C810006A21BABDD04B943E68EB733FA61B0000");
        await h.DispatchAsync(32788, body);
        await h.DispatchAsync(32790, BitConverter.GetBytes(1000000));
        Assert.Equal(new ushort[] { 32788, 32790 }, h.Guest.Sent.Select(p => p.Opcode));
        Assert.Equal(body, h.Guest.Sent[0].Body);
        Assert.Empty(h.Host.Sent);
        // The damage source cannot impersonate the affected player's report.
        await Assert.ThrowsAsync<PacketFormatException>(() => h.DispatchAsync(32788, body, h.Guest));
    }

    [Theory]
    [InlineData(false, false, 11)]
    [InlineData(true, false, 15)]
    [InlineData(false, true, 15)]
    [InlineData(true, true, 19)]
    public async Task CharacterResetParsesBothOptionalTags(bool first, bool second, int length)
    {
        using var h = await Harness.CreateAsync();
        var body = ResetBody(first, second);
        Assert.Equal(length, body.Length);
        await h.DispatchAsync(32926, body);
        Assert.Equal(body, Assert.Single(h.Guest.Sent).Body);
        Assert.Empty(h.Host.Sent);
        foreach (var size in Enumerable.Range(0, body.Length))
            await Assert.ThrowsAsync<PacketFormatException>(() => h.DispatchAsync(32926, body[..size]));
        await Assert.ThrowsAsync<PacketFormatException>(() => h.DispatchAsync(32926, [.. body, 0]));
        body[4] = 0;
        await Assert.ThrowsAsync<PacketFormatException>(() => h.DispatchAsync(32926, body));
        Assert.Single(h.Guest.Sent);
    }

    [Theory]
    [InlineData(38271, 8, 0)]
    [InlineData(38273, 13, 4)]
    [InlineData(38277, 32, 4)]
    [InlineData(38280, 9, 0)]
    public async Task EitherReportingParticipantCanCompleteAClientConfirmation(int opcode, int length, int offset)
    {
        using var h = await Harness.CreateAsync();
        var body = Body(length, offset);
        await h.DispatchAsync((ushort)opcode, body);
        Assert.Equal(body, Assert.Single(h.Host.Sent).Body);
        Assert.Equal(body, Assert.Single(h.Guest.Sent).Body);
    }

    [Fact]
    public async Task BubbleReleaseRescueAndPangKeepTheirConfirmationOrder()
    {
        using var h = await Harness.CreateAsync();
        // The local simulation waits for these acknowledgements before it emits the next stage.
        // 0x85C4C0 emits 38272 with -1 when the bubble timer releases the character.
        var release = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(release, 1000000);
        BinaryPrimitives.WriteInt32LittleEndian(release.AsSpan(4), -1);
        var sequence = new (ushort Opcode, byte[] Body)[]
        {
            (38282, Body(40, 0)), (38281, Body(4, 0)), (38272, release),
            (38282, Body(40, 0)), (38281, Body(4, 0)), (38273, Body(13, 0)), (38280, Body(9, 4)),
            (38282, Body(40, 0)), (38281, Body(4, 0)), (38271, Body(8, 4)), (38277, Body(32, 8)),
        };
        foreach (var (opcode, body) in sequence) await h.DispatchAsync(opcode, body);
        Assert.Equal(sequence.Select(p => p.Opcode), h.Host.Sent.Select(p => p.Opcode));
        Assert.Equal(sequence.Select(p => p.Opcode), h.Guest.Sent.Select(p => p.Opcode));
        Assert.Empty(h.HostGame.Sent);
        Assert.Empty(h.GuestGame.Sent);
        // The owner has already applied the reset; its 32926 synchronizes the remote presentation.
        await h.DispatchAsync(32926, ResetBody(false, false));
        Assert.Equal((ushort)32926, h.Guest.Sent.Last().Opcode);
        Assert.Equal(sequence.Length, h.Host.Sent.Count);
        Assert.Equal(RoomPhase.Playing, h.Room.Phase);
    }

    private static byte[] ResetBody(bool first, bool second)
    {
        var body = new List<byte>(BitConverter.GetBytes(1000000));
        foreach (var present in new[] { first, second })
        {
            body.Add(present ? (byte)0x3A : (byte)0xBA);
            if (present) body.AddRange(BitConverter.GetBytes(11));
        }
        body.Add(1);
        body.AddRange(new byte[4]);
        return body.ToArray();
    }

    [Fact]
    public async Task IndividualDeathDoesNotEndTheClassicTimedScoreRound()
    {
        using var h = await Harness.CreateAsync();
        h.Room.ModeId = 1;
        await h.DispatchAsync(625, [], h.HostGame, "kr-1256/game");
        Assert.Equal(RoomPhase.Playing, h.Room.Phase);
        Assert.Empty(h.HostGame.Sent);
        Assert.Empty(h.GuestGame.Sent);
        Assert.Empty(h.Host.Sent);
        Assert.Empty(h.Guest.Sent);
    }

    private sealed class Harness : IDisposable
    {
        private readonly PluginContext _routerContext;
        private readonly ServiceRegistry _services = new();
        internal EventBus Events { get; } = new(NullLogger.Instance);
        internal PluginContext InGame { get; }
        internal PluginContext Relay { get; }
        internal MatchRelayPlugin RelayPlugin { get; } = new();
        internal RoomRegistry Registry { get; private set; } = null!;
        internal PlayRoom Room { get; private set; } = null!;
        internal RoomSeat HostSeat { get; private set; } = null!;
        internal RoomSeat GuestSeat { get; private set; } = null!;
        internal RecordingSession HostGame { get; } = new(10);
        internal RecordingSession GuestGame { get; } = new(11);
        internal RecordingSession Host { get; private set; } = null!;
        internal RecordingSession Guest { get; private set; } = null!;

        private Harness()
        {
            _routerContext = new(new PluginRow { Id = "router", Plugin = "core.packet-router", Source = "test" }, _services, Events, NullLogger.Instance);
            InGame = new(new PluginRow { Id = "ingame", Plugin = "ingame", Source = "test" }, _services, Events, NullLogger.Instance);
            Relay = new(new PluginRow { Id = "match-relay", Plugin = "match-relay", Source = "test" }, _services, Events, NullLogger.Instance);
            _routerContext.Effect(_services.RegisterMany<IMessageSender<MatchSessionSignature>>(new RecordingSignatureSender()));
        }

        internal static async Task<Harness> CreateAsync()
        {
            var h = new Harness();
            try
            {
                await new PacketRouterPlugin().ActivateAsync(h._routerContext, CancellationToken.None);
                await new MessageRouterPlugin().ActivateAsync(h._routerContext, CancellationToken.None);
                await new InGamePlugin().ActivateAsync(h.InGame, CancellationToken.None);
                await h.RelayPlugin.ActivateAsync(h.Relay, CancellationToken.None);
                h.Registry = Assert.IsType<RoomRegistry>(h._services.Get<IMatchRoomDirectory>());
                (h.Room, h.HostSeat) = h.Registry.Create(h.HostGame, RoomWireSupport.OrdinarySettingWithMap(), 1, "host");
                (_, h.GuestSeat) = h.Registry.Join(h.GuestGame, h.Room.Id, 2, "guest");
                h.Room.MarkStarting();
                h.Host = await h.BindAsync(h.HostSeat, 20);
                h.Guest = await h.BindAsync(h.GuestSeat, 21);
                h.Room.MarkPlaying();
                return h;
            }
            catch { h.Dispose(); throw; }
        }

        internal async Task<RecordingSession> BindAsync(RoomSeat seat, long id)
        {
            seat.EndpointAdvertised = true;
            var session = new RecordingSession(id);
            var ticket = new ArrayBufferWriter<byte>();
            seat.Key.Write(new PacketWriter(ticket));
            var binding = await Registry.BindAsync(session, new MatchAuthenticate(ticket.WrittenMemory, seat.UserNum, seat.Nickname), CancellationToken.None);
            Registry.MarkInitialized(session, binding);
            return session;
        }

        internal Task DispatchAsync(ushort opcode, byte[] body, RecordingSession? sender = null, string protocol = "kr-1256/relay") =>
            _services.Get<IPacketRouter>().DispatchAsync(protocol, sender ?? Host, new(opcode, body), CancellationToken.None).AsTask();

        /// <summary>The host seat's state in the running round.</summary>
        internal SeatRoundState HostState => State(HostSeat);

        /// <summary>The guest seat's state in the running round.</summary>
        internal SeatRoundState GuestState => State(GuestSeat);

        private SeatRoundState State(RoomSeat seat) =>
            RelayPlugin.Rounds!.For(_services.Get<IRoomRoster>().Find(Room.Id)!).Seat(seat.Slot);

        public void Dispose() { Relay.Unwind(); InGame.Unwind(); _routerContext.Unwind(); }
    }

    private sealed class RecordingSession(long id) : ISessionWithFeatures
    {
        /// <inheritdoc />
        public ISessionFeatures Features { get; } = new TestSessionFeatures();

        internal List<(ushort Opcode, byte[] Body)> Sent { get; } = [];
        internal Func<Task>? BeforeSend { get; set; }
        public long Id => id;
        public IConnection Connection { get; } = new FakeConnection();
        public ClientProfile Profile { get; } = new("test", "kr", 1256, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public async ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            if (BeforeSend is { } before) await before();
            Sent.Add((opcode, payload.ToArray()));
        }
    }

    private sealed class FakeConnection : IConnection
    {
        public long Id => 0;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed { get; private set; }
        public ValueTask CloseAsync(string reason) { IsClosed = true; return ValueTask.CompletedTask; }
    }
}
