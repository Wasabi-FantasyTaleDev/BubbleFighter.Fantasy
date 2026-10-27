using BubbleFighter.Fantasy.Tests;
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
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Pins the typed round action feed over the match-connection admission: the match actions the feed binding
/// registers on the game and relay connections are layout-checked, actor-checked and relayed to the other
/// seats match links, exactly as the round flow sends them. The seats relay sessions are bound through the
/// room registry capability admission, the admission the match-connection row authenticates. Each action's
/// length and actor offsets are the client readers' own pins, recorded beside the theory rows.
/// </summary>
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
    public async Task TheTypedFeedRoutesBothListenersWithPinnedBodies(int opcode, int length, int actorOffset, bool echo)
    {
        using var h = await Harness.CreateAsync();
        var bytes = Body(length, actorOffset);
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
        _ = actorOffset;
        using var h = await Harness.CreateAsync();
        foreach (var size in new[] { length - 1, length + 1 })
            await Assert.ThrowsAsync<PacketFormatException>(() => h.DispatchAsync((ushort)opcode, new byte[size]));
        Assert.Empty(h.Guest.Sent);
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
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
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
    [InlineData("unbound")]
    [InlineData("peer-missing")]
    [InlineData("peer-closed")]
    [InlineData("result")]
    [InlineData("starting")]
    public async Task InvalidOrUninitializedTransportsNeverReceiveActions(string state)
    {
        using var h = await Harness.CreateAsync();
        var sender = h.Host;
        switch (state)
        {
            case "unbound": sender = new RecordingSession(99); break;
            case "peer-missing": h.Registry.Release(h.Guest); break;
            case "peer-closed": await h.Guest.Connection.CloseAsync("test"); break;
            case "result": h.Room.MarkResult(); break;
            case "starting": h.Room.ResetToWaiting(); h.Room.MarkStarting(); break;
        }
        await h.DispatchAsync(32925, Body(32, 0), sender);
        Assert.Empty(h.Guest.Sent);
        Assert.Empty(h.GuestGame.Sent);
    }




    [Fact]
    public async Task UnloadRemovesBothListenerRegistrationsAndUnknownPacketsStayUnhandled()
    {
        using var h = await Harness.CreateAsync();
        var events = new List<PacketReceivedEvent>();
        using var subscription = h.Events.On<PacketReceivedEvent>(events.Add);
        await h.DispatchAsync(32840, new byte[8]);
        Assert.False(Assert.Single(events).HasHandler);
        h.Binding.Unwind();
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
        internal PluginContext Binding { get; }
        internal RoomRegistry Registry { get; private set; } = null!;
        internal PlayRoom Room { get; private set; } = null!;
        internal RoomSeat HostSeat { get; private set; } = null!;
        internal RoomSeat GuestSeat { get; private set; } = null!;
        internal RecordingSession HostGame { get; } = new(10);
        internal RecordingSession GuestGame { get; } = new(11);
        internal RecordingSession Host { get; private set; } = null!;
        internal RecordingSession Guest { get; private set; } = null!;
        internal RecordingSession? Sender { get; set; }
        private readonly Dictionary<(int Room, int Slot), byte[]> _tickets = [];

        private Harness()
        {
            _routerContext = new(new PluginRow { Id = "router", Plugin = "core.packet-router", Source = "test" }, _services, Events, NullLogger.Instance);
            InGame = new(new PluginRow
            {
                Id = "ingame", Plugin = "ingame", Source = "test",
                Config = new Dictionary<string, object?> { ["native_life_mode_ids"] = new object[] { 1 } },
            }, _services, Events, NullLogger.Instance);
            Binding = new(new PluginRow { Id = "binding", Plugin = "test", Source = "test", ClientBuild = new("kr", 1256, 1304), Lineage = ["kr-1256", "kr-812"] }, _services, Events, NullLogger.Instance);
            _routerContext.Effect(_services.RegisterMany<IMessageSender<MatchSessionSignature>>(new RecordingSignatureSender()));
        }

        internal static async Task<Harness> CreateAsync()
        {
            var h = new Harness();
            try
            {
                await new PacketRouterPlugin().ActivateAsync(h._routerContext, CancellationToken.None);
                await new MessageRouterPlugin().ActivateAsync(h._routerContext, CancellationToken.None);
                await new OpcodeTableRegistryPlugin().ActivateAsync(h._routerContext, CancellationToken.None);
                var table = BubbleFighter.Fantasy.Protocol.OpcodeTableLoader.Load(
                    Path.Combine(AppContext.BaseDirectory, "opcodes.yaml"), TestWireDialects.Get("kr-1256").Profile);
                h._routerContext.Effect(h._services.Get<IOpcodeTableRegistry>().Register(table));
                var packets = BubbleFighter.Fantasy.Packets.PacketTables.Load(SharedDialectRows.PacketsDirectory());
                // The dialect row's room and round surface, mounted exactly as the row mounts it.
                new RoomCommandsBinding("kr-1256/game", table, packets, "kr-1256", NullLogger.Instance, roundsBound: true).Mount(h.Binding);
                new RoundStartBinding("kr-1256/game", table, packets, "kr-1256", [1, 0, 1], NullLogger.Instance).Mount(h.Binding);
                new RoundResultBinding("kr-1256/game", table, packets, "kr-1256", [1], NullLogger.Instance).Mount(h.Binding);
                new RoundFeedBinding("kr-1256/game", "kr-1256/relay", table, table.ForConnection(OpcodeConnection.Relay), packets, "kr-1256").Mount(h.Binding);
                await new InGamePlugin().ActivateAsync(h.InGame, CancellationToken.None);
                h.Registry = Assert.IsType<RoomRegistry>(Assert.Single(
                    h._services.GetAll<IMatchCapabilityDirectory>(), directory => directory.Protocol == "kr-1256/relay"));
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

        /// <summary>Binds a relay session through the capability admission the typed start issues.</summary>
        internal Task<RecordingSession> BindAsync(RoomSeat seat, long id) => BindAsync(Room, seat, id);

        internal async Task<RecordingSession> BindAsync(PlayRoom room, RoomSeat seat, long id)
        {
            var session = new RecordingSession(id);
            // The capability the room issued for this seat, as the offer writer sends it; a replacement
            // transport re-binds with the same capability and the registry closes the previous one.
            if (!_tickets.TryGetValue((room.Id, seat.Slot), out var ticket))
                _tickets[(room.Id, seat.Slot)] = ticket = Registry.IssueCapabilityCore(room, seat.Game, 16);
            var binding = await Registry.BindCapabilityAsync(session, new(ticket), CancellationToken.None);
            Registry.MarkInitialized(session, binding);
            return session;
        }

        internal Task DispatchAsync(ushort opcode, byte[] body, RecordingSession? sender = null, string protocol = "kr-1256/relay") =>
            _services.Get<IPacketRouter>().DispatchAsync(protocol, sender ?? Sender ?? Host, new(opcode, body), CancellationToken.None).AsTask();

        public void Dispose() { Binding.Unwind(); InGame.Unwind(); _routerContext.Unwind(); }
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
