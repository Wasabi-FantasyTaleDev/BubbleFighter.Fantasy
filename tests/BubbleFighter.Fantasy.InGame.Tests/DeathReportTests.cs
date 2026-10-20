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
/// Pins for the C2S 624 death report: the body is exactly 8 bytes {i32, i32}, the death form is {1, 0}, and
/// the handler relays NOTHING - the S2C census has no 624 row, so no
/// others-facing death surface is client-proven and none is invented. A death
/// report also mutates no round phase: only the round clock ends a round.
///
/// </summary>
public sealed class DeathReportTests
{
    /// <summary>The death form sub_B38850 sends back-to-back with C2S 625.</summary>
    private static readonly byte[] DeathForm = [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>The timer-path form.</summary>
    private static readonly byte[] TimerForm = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    [Fact]
    public void BodyLengthIsPinnedAtEightBytesAndBothLiveFormsParse()
    {
        // sub_708B60 writes two 4-byte fields 4 + 4 = 8.
        Assert.Equal(8, DeathReport.BodyLength);
        Assert.Equal(8, DeathForm.Length);
        var death = DeathReport.Parse(DeathForm);
        Assert.Equal(1, death.Unknown0);
        Assert.Equal(0, death.Unknown1);
        var timer = DeathReport.Parse(TimerForm);
        Assert.Equal(0, timer.Unknown0);
        Assert.Equal(0, timer.Unknown1);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    public void ShortOrLongBodiesAreRejected(int length)
    {
        var body = new byte[length];
        DeathForm.AsSpan(0, Math.Min(length, DeathForm.Length)).CopyTo(body);
        Assert.Throws<PacketFormatException>(() => DeathReport.Parse(body));
    }

    [Fact]
    public async Task ADeathReportFromSeatOneSendsNothingToSeatTwoAndMutatesNoRoundPhase()
    {
        var options = new InGameOptions();
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(10);
        var (room, _) = registry.Create(host, RoomWireSupport.WaitingSettingWithMap(), 1, "host");
        var guest = new RecordingSession(11);
        registry.Join(guest, room.Id, 2, "guest");
        var hostRelay = new RecordingSession(20);
        var guestRelay = new RecordingSession(21);
        registry.BindRelay(hostRelay, room.Seats[0].Key);
        registry.BindRelay(guestRelay, room.Seats[1].Key);
                var relay = new TestRelay(registry);
        lock (room.Gate)
            room.MarkPlaying();

        await relay.Flows.HandleDeathReportAsync(
            host, new InboundPacket(InGameOpcodes.C2SDeathReport, DeathForm), CancellationToken.None);

        // No others-facing death surface is proven, so seat two receives
        // nothing on either of its connections - and the sender is not
        // answered either.
        Assert.Empty(guest.Opcodes);
        Assert.Empty(guestRelay.Opcodes);
        Assert.Empty(host.Opcodes);
        Assert.Empty(hostRelay.Opcodes);

        // The death did not end the round: the phase is still playing, the
        // result pair never opened and the round clock was not armed.
        lock (room.Gate)
            Assert.Equal(RoomPhase.Playing, room.Phase);
        Assert.False(room.ResultOpenSent);
        Assert.False(room.ResultCloseSent);
        Assert.Null(room.RoundEndsAtUtc);
    }

    [Fact]
    public async Task AReportFromASessionWithNoRoomIsDroppedWithoutASend()
    {
        var options = new InGameOptions();
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(10);
        var (room, _) = registry.Create(host, RoomWireSupport.WaitingSettingWithMap(), 1, "host");
                var relay = new TestRelay(registry);
        var outsider = new RecordingSession(30);

        // A session bound to no room is an ordinary outcome - logged, not
        // thrown, and nothing is sent to the seated player.
        await relay.Flows.HandleDeathReportAsync(
            outsider, new InboundPacket(InGameOpcodes.C2SDeathReport, DeathForm), CancellationToken.None);

        Assert.Empty(host.Opcodes);
        Assert.Empty(outsider.Opcodes);
        lock (room.Gate)
            Assert.Equal(RoomPhase.Waiting, room.Phase);
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
