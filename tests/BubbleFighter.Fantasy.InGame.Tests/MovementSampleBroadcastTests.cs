using BubbleFighter.Fantasy.MatchRelay;
using BubbleFighter.Fantasy.Contracts.Movement;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.InGame.Wire;
using BubbleFighter.Fantasy.Persistence.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Pins for the others-only movement sample broadcast: the mask-gated C2S 32923 parse, the
/// per-room-seat merge, the others-only delivery, and the off-by-default gate.
/// </summary>
public sealed class MovementSampleBroadcastTests
{
    [Fact]
    public void ParsePinsPartialMasksToTheirComponentsAndLeavesTheRestAtDefaults()
    {
        // Only the mask-selected fields are on the wire; everything the mask
        // leaves out parses as the zero default, never as garbage.
        var xOnly = MovementSample.Parse(SampleBody(1000000, 5, MovementSample.MaskX, x: 1400.5f));
        Assert.Equal(1000000, xOnly.Actor);
        Assert.Equal((ushort)5, xOnly.Tick);
        Assert.Equal(1400.5f, xOnly.X);
        Assert.Equal(0f, xOnly.Y);
        Assert.Equal(0f, xOnly.Z);
        Assert.Equal(0, xOnly.Yaw);
        Assert.Equal(0, xOnly.Pitch);
        Assert.Equal(0, xOnly.State);

        // 0x2A = y, yaw, state: the fields ride in bit order 2, 8, 0x20.
        var spread = MovementSample.Parse(SampleBody(
            1100000, 6, 0x2A, y: 170.25f, yaw: 0x1F, state: 0x03));
        Assert.Equal(0f, spread.X);
        Assert.Equal(170.25f, spread.Y);
        Assert.Equal(0f, spread.Z);
        Assert.Equal((byte)0x1F, spread.Yaw);
        Assert.Equal(0, spread.Pitch);
        Assert.Equal((byte)0x03, spread.State);

        // 0x3F is everything: 22 bytes, all six fields.
        var full = MovementSample.Parse(SampleBody(
            1000000, 7, PlayerMovementState.FullMask,
            x: 1f, y: 2f, z: 3f, yaw: 4, pitch: 5, state: 6));
        Assert.Equal((byte)(MovementSample.MaskX | MovementSample.MaskY | MovementSample.MaskZ
            | MovementSample.MaskYaw | MovementSample.MaskPitch | MovementSample.MaskState), full.Mask);
        Assert.Equal(1f, full.X);
        Assert.Equal(2f, full.Y);
        Assert.Equal(3f, full.Z);
        Assert.Equal((byte)4, full.Yaw);
        Assert.Equal((byte)5, full.Pitch);
        Assert.Equal((byte)6, full.State);

        // The body length is determined by the mask; any other length is a
        // format error, not a shorter read.
        var wrong = SampleBody(1000000, 5, MovementSample.MaskX, x: 1f);
        Assert.Equal(11, wrong.Length);
        Assert.Throws<PacketFormatException>(() => MovementSample.Parse([.. wrong, 0x00]));
    }

    [Fact]
    public async Task BroadcastIsOffByDefaultAndNothingMoves()
    {
        // Negative control: without broadcast_movement_samples neither the
        // sample path nor the report path sends anything, and the store is
        // not even read. The broadcast remains an explicit deployment setting.
        var options = new InGameOptions();
        Assert.False(new MatchRelayOptions().BroadcastMovementSamples);
        var (registry, host, guest) = TwoSeatRoom(options);
        var movement = new MemoryPlayerMovementState(64);
                var relay = new TestRelay(registry);

        await relay.Flows.HandleMovementSampleAsync(
            host, new InboundPacket(InGameOpcodes.C2SWanderReport, SampleBody(1000000, 1, PlayerMovementState.FullMask, x: 1f, y: 2f, z: 3f, yaw: 4, pitch: 5, state: 6)), CancellationToken.None);
        await relay.Flows.HandleMovementReportAsync(
            host,
            new InboundPacket(InGameOpcodes.C2SMovementReport, WireLengthSupport.Written(new MovementReport(
                1000000, 1f, 2f, 3f, 4, 5, 6, 1).Write)),
            CancellationToken.None);

        Assert.Empty(host.Opcodes);
        Assert.Empty(guest.Opcodes);
        Assert.Null(relay.MovementOf(registry.RequireGame(host).Room, registry.RequireGame(host).Seat));
    }

    [Fact]
    public async Task OrphanPartialIsDroppedWithoutFabricatingStateAndAFullSampleRecovers()
    {
        // A partial for an actor with no stored state is refused, never merged over fabricated zeros - the
        // client always sends a FULL first, so an orphan partial means the FULL was missed. No frame goes out
        // and the actor stays untracked.
        var options = new InGameOptions();
        var (registry, host, guest) = TwoSeatRoom(options);
        var movement = new MemoryPlayerMovementState(64);
                var relay = new TestRelay(registry, broadcastMovementSamples: true);

        await relay.Flows.HandleMovementSampleAsync(
            host,
            new InboundPacket(InGameOpcodes.C2SWanderReport, SampleBody(1000000, 1, MovementSample.MaskX, x: 9f)),
            CancellationToken.None);

        Assert.Empty(guest.Opcodes);
        Assert.Null(relay.MovementOf(registry.RequireGame(host).Room, registry.RequireGame(host).Seat));

        // The next FULL rebuilds the state and the broadcast resumes.
        await relay.Flows.HandleMovementSampleAsync(
            host,
            new InboundPacket(InGameOpcodes.C2SWanderReport, SampleBody(
                1000000, 2, PlayerMovementState.FullMask, x: 1f, y: 2f, z: 3f, yaw: 4, pitch: 5, state: 6)),
            CancellationToken.None);
        Assert.Equal(
            new PlayerMovementState(1f, 2f, 3f, 4, 5, 6),
            relay.MovementOf(registry.RequireGame(host).Room, registry.RequireGame(host).Seat));
        Assert.Equal(21, guest.Body(InGameOpcodes.S2CMovementReport).Length);
    }

    [Fact]
    public async Task AnotherRoomAndANewRoundCannotReuseAnEarlierSeatsCoordinates()
    {
        var options = new InGameOptions();
        var (registry, host, _) = TwoSeatRoom(options);
                var relay = new TestRelay(registry, broadcastMovementSamples: true);
        await relay.Flows.HandleMovementSampleAsync(host, new(32923, SampleBody(1000000, 1, 63, x: 10, y: 20, z: 30)), CancellationToken.None);
        var (secondRegistry, secondHost, secondGuest) = TwoSeatRoom(options);
                var secondRelay = new TestRelay(secondRegistry, broadcastMovementSamples: true);
        await secondRelay.Flows.HandleMovementSampleAsync(secondHost, new(32923, SampleBody(1000000, 2, 1, x: 99)), CancellationToken.None);
        Assert.Empty(secondGuest.Opcodes);
        Assert.Null(secondRelay.MovementOf(secondRegistry.RequireGame(secondHost).Room, secondRegistry.RequireGame(secondHost).Seat));
        registry.RequireGame(host).Room.MarkStarting();
        await relay.Flows.HandleMovementSampleAsync(host, new(32923, SampleBody(1000000, 2, 1, x: 99)), CancellationToken.None);
        Assert.Null(relay.MovementOf(registry.RequireGame(host).Room, registry.RequireGame(host).Seat));
    }

    [Fact]
    public async Task ForgedEntityIsRejectedAndPeersReceiveMovementOnlyOnTheirRelay()
    {
        var options = new InGameOptions();
        var (registry, host, guest) = TwoSeatRoom(options);
        var (room, seat) = registry.RequireGame(guest);
        var relay = new RecordingSession(99);
        seat.Relay = relay;
        seat.RelayInitialized = true;
                var matchRelay = new TestRelay(registry, broadcastMovementSamples: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => matchRelay.Flows.HandleMovementSampleAsync(host,
            new(32923, SampleBody(1100000, 1, 63, x: 1, y: 2, z: 3)), CancellationToken.None).AsTask());
        Assert.Null(matchRelay.MovementOf(room, seat));
        var body = WireLengthSupport.Written(new MovementReport(1000000, 1, 2, 3, 4, 5, 6, 65535).Write);
        await matchRelay.Flows.HandleMovementReportAsync(host, new(32885, body), CancellationToken.None);
        Assert.Empty(guest.Opcodes);
        Assert.Empty(host.Opcodes);
        Assert.Equal(body, relay.Body(32885));
    }

    [Fact]
    public async Task MemoryStoreRefusesOrphanPartialsHonoursTheActorCapAndValidatesIds()
    {
        var movement = new MemoryPlayerMovementState(1);

        // A partial for an unknown actor is refused and tracks nothing.
        Assert.Null(await movement.MergeAsync(7, MovementSample.MaskY, 0f, 5f, 0f, 0, 0, 0, CancellationToken.None));
        Assert.Null(await movement.FindAsync(7, CancellationToken.None));

        // A full merge initializes the actor; a second actor past the cap
        // fails the merge loudly instead of evicting or growing.
        Assert.Equal(
            new PlayerMovementState(1f, 2f, 3f, 4, 5, 6),
            await movement.MergeAsync(1, PlayerMovementState.FullMask, 1f, 2f, 3f, 4, 5, 6, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => movement
            .MergeAsync(2, PlayerMovementState.FullMask, 0f, 0f, 0f, 0, 0, 0, CancellationToken.None)
            .AsTask());
        // The tracked actor keeps merging at the cap.
        Assert.Equal(
            new PlayerMovementState(1f, 9f, 3f, 4, 5, 6),
            await movement.MergeAsync(1, MovementSample.MaskY, 0f, 9f, 0f, 0, 0, 0, CancellationToken.None));
        Assert.Equal(1, movement.MaxActors);

        // Actor ids are positive client-computed ids; zero and negative fail.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => movement
            .MergeAsync(0, PlayerMovementState.FullMask, 0f, 0f, 0f, 0, 0, 0, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => movement.FindAsync(-1, CancellationToken.None).AsTask());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryPlayerMovementState(0));
    }

    /// <summary>Builds a C2S 32923 body: actor, tick, mask, then the
    /// mask-selected fields in bit order (x/y/z floats, yaw/pitch/state
    /// bytes), exactly as the length check in <see cref="MovementSample.Parse"/>
    /// demands.</summary>
    private static byte[] SampleBody(
        int actor,
        ushort tick,
        byte mask,
        float? x = null,
        float? y = null,
        float? z = null,
        byte? yaw = null,
        byte? pitch = null,
        byte? state = null)
    {
        var body = new List<byte>(22)
        {
            (byte)actor,
            (byte)(actor >> 8),
            (byte)(actor >> 16),
            (byte)(actor >> 24),
            (byte)tick,
            (byte)(tick >> 8),
            mask,
        };
        if ((mask & MovementSample.MaskX) != 0)
            body.AddRange(BitConverter.GetBytes(x ?? 0f));
        if ((mask & MovementSample.MaskY) != 0)
            body.AddRange(BitConverter.GetBytes(y ?? 0f));
        if ((mask & MovementSample.MaskZ) != 0)
            body.AddRange(BitConverter.GetBytes(z ?? 0f));
        if ((mask & MovementSample.MaskYaw) != 0)
            body.Add(yaw ?? 0);
        if ((mask & MovementSample.MaskPitch) != 0)
            body.Add(pitch ?? 0);
        if ((mask & MovementSample.MaskState) != 0)
            body.Add(state ?? 0);
        return [.. body];
    }

    private static (RoomRegistry Registry, RecordingSession Host, RecordingSession Guest) TwoSeatRoom(
        InGameOptions options)
    {
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(10);
        var (room, _) = registry.Create(host, RoomWireSupport.WaitingSettingWithMap(), 1, "host");
        var guest = new RecordingSession(11);
        registry.Join(guest, room.Id, 2, "guest");
        foreach (var seat in room.Seats) { seat.Relay = seat.Game; seat.RelayInitialized = true; }
        return (registry, host, guest);
    }

    private sealed class RecordingSession(long id) : ISessionWithFeatures
    {
        /// <inheritdoc />
        public ISessionFeatures Features { get; } = new TestSessionFeatures();

        private readonly List<(ushort Opcode, byte[] Body)> _sent = [];

        public long Id { get; } = id;
        public IConnection Connection { get; } = new FakeConnection();
        public ClientProfile Profile { get; } = new("test", "kr", 1256, 0, 0, "test");
        public ConcurrentDictionary<string, object> Items { get; } = new();

        public IEnumerable<ushort> Opcodes
        {
            get
            {
                lock (_sent)
                    return _sent.Select(s => s.Opcode).ToArray();
            }
        }

        public byte[] Body(ushort opcode)
        {
            lock (_sent)
                return _sent.Last(s => s.Opcode == opcode).Body;
        }

        public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            lock (_sent)
                _sent.Add((opcode, payload.ToArray()));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeConnection : IConnection
    {
        public long Id => 0;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => throw new NotSupportedException();
        public ConcurrentDictionary<string, object> Items { get; } = new();
        public bool IsClosed => false;
        public ValueTask CloseAsync(string reason) => ValueTask.CompletedTask;
    }
}
