using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Sessions;
using BubbleFighter.Fantasy.InGame.Wire;
using Microsoft.Extensions.Logging.Abstractions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Pins for the C2S/S2C 38283 combat hit relay: the body is exactly 19 bytes, the relay is
/// byte-for-byte, every other seat receives the sender's frame, including the originating initialized match
/// transport.
/// </summary>
public sealed class CombatHitRelayTests
{
    /// <summary>
    /// A reference frame: damager key 2, target 1000012,
    /// hit position (1117.0, 278.25, 1090.0), mode 0, collapsed-bool tail zero.
    /// </summary>
    private static readonly byte[] ReferenceFrame =
    [
        0x02, 0x00, 0x00, 0x00,             // damager key 2 (client-allocated)
        0x4C, 0x42, 0x0F, 0x00,             // target object id 1000012
        0x5D, 0x64, 0x59, 0x5C, 0x42, 0x64, // hit position, three binary16
        0x00,                               // mode 0
        0x00, 0x00, 0x00, 0x00,             // i32 collapsed to bool, zero
    ];

    [Fact]
    public void BodyLengthIsPinnedAtNineteenBytes()
    {
        // The dispatcher case reads i32, i32, u16, u16, u16, u8, i32 through
        // seven stream reads: 4 + 4 + 2 + 2 + 2 + 1 + 4 = 19.
        Assert.Equal(19, CombatHitReport.BodyBytes);
        Assert.Equal(CombatHitReport.BodyBytes, ReferenceFrame.Length);
        CombatHitReport.Validate(ReferenceFrame, "C2S 38283");
    }

    [Theory]
    [InlineData(18)]
    [InlineData(20)]
    public void ShortOrLongBodiesAreRejected(int length)
    {
        // The relay is byte-for-byte, so the only check is the length pin: a
        // body the dispatcher case would not consume exactly is malformed.
        var body = new byte[length];
        ReferenceFrame.AsSpan(0, Math.Min(length, ReferenceFrame.Length)).CopyTo(body);
        Assert.Throws<PacketFormatException>(() => CombatHitReport.Validate(body, "C2S 38283"));
    }

    [Fact]
    public async Task EveryInitializedSeatReceivesTheFrameIncludingTheSender()
    {
        var options = new InGameOptions();
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(10);
        var (room, hostSeat) = registry.Create(host, RoomWireSupport.WaitingSettingWithMap(), 1, "host");
        var guest = new RecordingSession(11);
        var (_, guestSeat) = registry.Join(guest, room.Id, 2, "guest");
        var third = new RecordingSession(12);
        var (_, thirdSeat) = registry.Join(third, room.Id, 3, "third");
        var hostRelay = new RecordingSession(20);
        var guestRelay = new RecordingSession(21);
        var thirdRelay = new RecordingSession(22);
        registry.BindRelay(hostRelay, hostSeat.Key);
        registry.BindRelay(guestRelay, guestSeat.Key);
        registry.BindRelay(thirdRelay, thirdSeat.Key);
        room.MarkPlaying();
        hostSeat.RelayInitialized = guestSeat.RelayInitialized = thirdSeat.RelayInitialized = true;
        var loop = new TestRelay(registry).Actions;

        await loop.HandleAsync(
            hostRelay, new InboundPacket(InGameOpcodes.C2SCombatHitReport, ReferenceFrame), CancellationToken.None);

        // Each peer receives the frame under S2C 38283, byte for byte.
        foreach (var peer in new[] { hostRelay, guestRelay, thirdRelay })
        {
            Assert.Equal([InGameOpcodes.S2CCombatHitReport], peer.Opcodes);
            Assert.Equal(
                Convert.ToHexString(ReferenceFrame),
                Convert.ToHexString(peer.Body(InGameOpcodes.S2CCombatHitReport)));
        }

        // The main connection remains outside match delivery.
        Assert.Empty(host.Opcodes);

        // The peers' game sessions stay silent too: the relayed frame rides
        // the bound match connection.
        Assert.Empty(guest.Opcodes);
        Assert.Empty(third.Opcodes);
    }

    [Fact]
    public async Task ASeatWithoutAMatchConnectionDoesNotReceiveCombatOnItsGameSession()
    {
        var options = new InGameOptions();
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(10);
        var (room, _) = registry.Create(host, RoomWireSupport.WaitingSettingWithMap(), 1, "host");
        var guest = new RecordingSession(11);
        registry.Join(guest, room.Id, 2, "guest");
        var loop = new TestRelay(registry).Actions;

        // Uninitialized main connections cannot inject or receive match hits.
        await loop.HandleAsync(
            host, new InboundPacket(InGameOpcodes.C2SCombatHitReport, ReferenceFrame), CancellationToken.None);

        Assert.Empty(guest.Opcodes);
        Assert.Empty(host.Opcodes);
    }

    [Fact]
    public async Task AFrameFromASessionWithNoRoomIsDroppedWithoutASend()
    {
        var options = new InGameOptions();
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(10);
        var (room, hostSeat) = registry.Create(host, RoomWireSupport.WaitingSettingWithMap(), 1, "host");
        var hostRelay = new RecordingSession(20);
        registry.BindRelay(hostRelay, hostSeat.Key);
        var loop = new TestRelay(registry).Actions;
        var outsider = new RecordingSession(30);

        // A solo room relays nothing anyway, and a session bound to no room at
        // all is an ordinary outcome before the seat resolves - logged, not thrown.
        await loop.HandleAsync(
            outsider, new InboundPacket(InGameOpcodes.C2SCombatHitReport, ReferenceFrame), CancellationToken.None);

        Assert.Empty(host.Opcodes);
        Assert.Empty(hostRelay.Opcodes);
        Assert.Empty(outsider.Opcodes);
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
