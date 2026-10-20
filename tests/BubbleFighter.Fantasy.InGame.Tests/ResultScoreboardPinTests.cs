using System.Buffers.Binary;
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
using Microsoft.Extensions.Logging.Abstractions;
using BubbleFighter.Fantasy.Tests.Shared;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>
/// Byte pins for S2C 614 records and lifecycle tests for the timed result sequence.
/// The optional scoreboard never synthesizes a banner or copies unproven C2S 103 statistics.
/// </summary>
public sealed class ResultScoreboardPinTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 17)]
    public async Task TimedScoreResultIncludesTeamTotalsWithoutExperimentalRowsOption(int blue, int red)
    {
        var options = FastOptions();
        var (handle, host, guest) = TwoSeatRoom(options);
        handle.Room.ModeId = 1;
        handle.Room.MarkStarting();
        handle.Room.MarkPlaying();
        handle.Room.BlueScore = (ushort)blue;
        handle.Room.RedScore = (ushort)red;
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance);
        // The round clock runs out on its own; no client frame ends the round.
        loop.Result.ArmRoundClock(handle.Room, 1);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!host.Opcodes.Contains((ushort)620) && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(new ushort[] { 612, 614, 615, 576, 620 }, host.Opcodes);
        Assert.Equal(host.Body(614), guest.Body(614));
        var body = host.Body(614);
        Assert.Equal(2, body[0]);
        Assert.Equal(255, body[1]);
        Assert.Equal(blue, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(6)));
        Assert.Equal(red, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(18)));
        Assert.Equal(3UL, BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(30)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(38)));
        Assert.Equal(handle.Room.Seats[0].UserNum, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(42)));
        Assert.Equal(handle.Room.Seats[0].Team, body[50 + "host".Length * 2]);
        Assert.Equal(55 + 2 * (242 + 8) + 2 * ("host".Length + "guest".Length), body.Length);
        Assert.Equal(RoomPhase.Waiting, handle.Room.Phase);
    }

    [Fact]
    public void TeamScoreCodecMatchesReferenceSeventeenByteUpdate()
    {
        Assert.Equal(Convert.FromHexString("020100FFFF000000000000000000000000"), MatchLife1256.TeamScore(2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MatchLife1256.TeamScore(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MatchLife1256.TeamScore(3, 1));
        var body = WireLengthSupport.Written(MatchLife1256.Scoreboard(3, 17, []).Write);
        Assert.Equal(55, body.Length);
        Assert.Equal(Convert.FromHexString("02FF00000000030000000000000000000000110000000000000000000000"), body[..30]);
    }

    [Fact]
    public void EmptyRecordPinsThirtyOneZeroBytes()
    {
        // The doc's arithmetic: 1 (head count) + 1 (winner slot) + 4 (banner
        // gate) + 8 (mask) + 4 + 4 (tail i32 pair) + 4 (container count)
        // + 1 + 1 (GameInfo+308/+312 bytes) + 2 (flags) + 1 (absent tag).
        var body = WireLengthSupport.Written(new ResultScoreboard().Write);
        Assert.Equal(31, body.Length);
        Assert.Equal(new byte[31], body);

        // Walk the reader the way sub_429620 plus the section-2 tail read it,
        // so a width regression anywhere shifts a later assert, not a cursor
        // on a live client.
        var r = new PacketReader(body);
        Assert.Equal(0, r.ReadByte());                       // head count (ScoreData+4)
        Assert.Equal(0, r.ReadByte());                       // winner slot (ScoreData+452)
        Assert.Equal(0, r.ReadInt32());                      // banner gate (ScoreData+456)
        Assert.Equal(0L, r.ReadInt64());                     // presence mask (u64, LE)
        Assert.Equal(0, r.ReadInt32());                      // +19180
        Assert.Equal(0, r.ReadInt32());                      // +19184
        Assert.Equal(0u, r.ReadUInt32());                    // container count (+19188)
        Assert.Equal(0, r.ReadByte());                       // -> GameInfo+308
        Assert.Equal(0, r.ReadByte());                       // -> GameInfo+312
        Assert.Equal(0, r.ReadUInt16());                     // flags (+19272)
        Assert.Equal(0, r.ReadByte());                       // optional tag (not 0x3A)
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void TwoRowEmitPinsTheMaskRowOrderAndFraming()
    {
        // Rows are given out of slot order on purpose: FromRows must emit them
        // in ascending mask-bit order (sub_429620 walks set bits ascending), not
        // list order.
        var record = ResultScoreboard.FromRows(
        [
            ResultScoreboardRow.ForSeat(2, string.Empty, null) with { UserNumber = 72 },
            ResultScoreboardRow.ForSeat(0, "ab", [7, 8, 9]),
        ]);

        var body = WireLengthSupport.Written(record.Write);

        // 14-byte header + (242 + 2*2) + 242 row bytes + 8*2 mask-gated bytes
        // + 17-byte tail - the doc's 31 + 12H + sum(242+2a) + 8P + 12C with
        // H = 0 and C = 0.
        Assert.Equal(31 + (ResultScoreboardRow.EmptyRowByteLength + 4)
            + ResultScoreboardRow.EmptyRowByteLength + 16, body.Length);
        Assert.Equal(535, body.Length);

        // Header: head count 0, winner slot 0, gate 0, then the u64 mask with
        // bits 0 and 2 set - little-endian 05 followed by zeros.
        Assert.Equal(0, body[0]);
        Assert.Equal(0, body[1]);
        Assert.Equal(0, BitConverter.ToInt32(body, 2));
        Assert.Equal(0x05UL, BitConverter.ToUInt64(body, 6));
        Assert.Equal(new byte[] { 0x05, 0, 0, 0, 0, 0, 0, 0 }, body[6..14]);

        // The first row block is slot 0's, starting right after the mask:
        // i32 +500, i32 slot 0, wstring "ab".
        Assert.Equal(
            "00000000" + "00000000" + "02000000" + "61006200",
            Convert.ToHexString(body, 14, 16));
        var r = new PacketReader(body[14..]);
        ReadRow(ref r, expectedSlot: 0, expectedName: "ab", expectedStatPrefix: [7, 8, 9]);
        ReadRow(ref r, expectedSlot: 72, expectedName: string.Empty, expectedStatPrefix: []);

        // One zero i32 per set bit in each of the two mask-gated arrays
        // (+18892 block first, then +19036), then the 17-byte tail.
        for (var i = 0; i < 4; i++)
            Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());                      // +19180
        Assert.Equal(0, r.ReadInt32());                      // +19184
        Assert.Equal(0u, r.ReadUInt32());                    // container count
        Assert.Equal(0, r.ReadByte());                       // -> GameInfo+308
        Assert.Equal(0, r.ReadByte());                       // -> GameInfo+312
        Assert.Equal(0, r.ReadUInt16());                     // flags
        Assert.Equal(0, r.ReadByte());                       // absent optional tag
        Assert.Equal(0, r.Remaining);
    }

    /// <summary>Reads one row block in wire order and pins every group width.</summary>
    private static void ReadRow(ref PacketReader r, int expectedSlot, string expectedName, int[] expectedStatPrefix)
    {
        Assert.Equal(0, r.ReadInt32());                      // +500
        Assert.Equal(expectedSlot, r.ReadInt32());           // +0
        Assert.Equal(expectedName, r.ReadWideStringPrefixed());
        Assert.Equal(0, r.ReadByte());                       // +52
        for (var i = 0; i < ResultScoreboardRow.StatSlotCount; i++)
        {
            var expected = i < expectedStatPrefix.Length ? expectedStatPrefix[i] : 0;
            Assert.Equal(expected, r.ReadInt32());
        }

        Assert.Equal(0, r.ReadInt32());                      // +388 bool
        Assert.Equal(0, r.ReadInt32());                      // +436
        Assert.Equal(0u, r.ReadUInt32());                    // vector count (+464)
        Assert.Equal(string.Empty, r.ReadWideStringPrefixed());   // +440
        for (var i = 0; i < 3; i++)
            Assert.Equal(0, r.ReadInt32());                  // +236,+476,+480
        Assert.Equal(-1, r.ReadInt32());                     // +392 no grade
        for (var i = 0; i < 3; i++)
            Assert.Equal(0, r.ReadInt32());                  // +144,+148,+408
        Assert.Equal(0, r.ReadUInt16());                     // +412
        for (var i = 0; i < 5; i++)
            Assert.Equal(0, r.ReadInt32());                  // +416,+420,+160,+164,+168
        Assert.Equal(0, r.ReadByte());                       // +172
        Assert.Equal(0, r.ReadByte());                       // +173
        Assert.Equal(0, r.ReadUInt16());                     // +184
        Assert.Equal(0, r.ReadUInt16());                     // +76
        Assert.Equal(0, r.ReadUInt16());                     // +186
        Assert.Equal(0, r.ReadInt32());                      // +508
        Assert.Equal(0, r.ReadUInt16());                     // +188
        Assert.Equal(0, r.ReadByte());                       // +432
        for (var i = 0; i < ResultScoreboardRow.Block192Count; i++)
            Assert.Equal(0, r.ReadInt32());                  // +192..+220
        Assert.Equal(0, r.ReadInt32());                      // +424
        Assert.Equal(0, r.ReadInt32());                      // +428
    }

    [Fact]
    public async Task ResultCloseSendsNoScoreboardRowsWhileTheOptionIsOff()
    {
        Assert.False(new InGameOptions().SendScoreboardRows);
        var options = FastOptions();
        var (handle, host, _) = TwoSeatRoom(options);
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance);
        await loop.Result.HandleGameOverAsync(host, new(InGameOpcodes.C2SGameOver, Array.Empty<byte>()), CancellationToken.None);
        await SendStatsAckAsync(loop, host, Enumerable.Range(1, 30).ToArray());
        await handle.Room.ResultTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new ushort[] { 612, 615, 576, 620 }, host.Opcodes);
        Assert.Equal(new byte[] { 0 }, host.Body(615));
        Assert.Equal(RoomPhase.Waiting, handle.Room.Phase);
    }

    [Fact]
    public async Task ScoreboardPrecedesRewardAndStatsDoNotSkipTheDisplayStages()
    {
        var options = FastOptions();
        options.SendScoreboardRows = true;
        var (handle, host, guest) = TwoSeatRoom(options);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeSend = async (opcode, ct) =>
        {
            if (opcode == 614)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
        };
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance);
        await loop.Result.HandleGameOverAsync(host, new(InGameOpcodes.C2SGameOver, Array.Empty<byte>()), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await SendStatsAckAsync(loop, host, Enumerable.Range(1, 30).ToArray());
        Assert.Equal(new ushort[] { 612 }, host.Opcodes);
        Assert.False(handle.Room.ResultTask.IsCompleted);
        var expected = GameToClient.ScoreboardData(ResultScoreboard.FromRows(
            handle.Room.Seats.Select(seat => ResultScoreboardRow.ForSeat(seat.Slot, seat.Nickname, null) with
            {
                UserNumber = seat.UserNum, Unknown52 = checked((byte)seat.Team), Unknown500 = 1,
                UnknownStatBlock = [seat.Level, .. new int[ResultScoreboardRow.StatSlotCount - 1]],
            })));
        release.SetResult();
        await handle.Room.ResultTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new ushort[] { 612, 614, 615, 576, 620 }, host.Opcodes);
        Assert.Equal(expected, host.Body(614));
        Assert.Equal(expected, guest.Body(614));
        Assert.Equal(new byte[] { 0 }, host.Body(615));
    }

    [Fact]
    public async Task StaleResultGenerationDoesNotSendIntoANewRound()
    {
        var options = FastOptions();
        options.ResultScoreboardDelayMs = 600_000;
        options.SendScoreboardRows = true;
        var (handle, host, _) = TwoSeatRoom(options);
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance);
        await loop.Result.HandleGameOverAsync(host, new(InGameOpcodes.C2SGameOver, Array.Empty<byte>()), CancellationToken.None);
        var previous = handle.Room.ResultTask;
        handle.Room.ResetToWaiting();
        handle.Room.MarkStarting();
        await previous.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new ushort[] { 612 }, host.Opcodes);
        Assert.Equal(RoomPhase.Starting, handle.Room.Phase);
    }

    [Fact]
    public async Task UnloadCancelsResultDelaysWithoutReturningClientsToTheRoom()
    {
        var options = FastOptions();
        options.ResultScoreboardDelayMs = 10_000;
        var (handle, host, _) = TwoSeatRoom(options);
        using var lifetime = new CancellationTokenSource();
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance, lifetime: lifetime.Token);
        await loop.Result.HandleGameOverAsync(host, new(InGameOpcodes.C2SGameOver, Array.Empty<byte>()), CancellationToken.None);
        lifetime.Cancel();
        await handle.Room.ResultTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new ushort[] { 612 }, host.Opcodes);
        Assert.Equal(RoomPhase.Result, handle.Room.Phase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DepartingSeatSendFailureDoesNotStrandRemainingPlayers(bool detach)
    {
        var options = FastOptions();
        options.SendScoreboardRows = true;
        var (handle, host, guest) = TwoSeatRoom(options);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeSend = async (opcode, ct) =>
        {
            if (opcode != 614)
                return;
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            throw new IOException("The peer disconnected during the result send.");
        };
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance);
        await loop.Result.HandleGameOverAsync(host, new(InGameOpcodes.C2SGameOver, Array.Empty<byte>()), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (detach)
            handle.Registry.Detach(host.Id);
        else
            await host.Connection.CloseAsync("test departure before registry notification");
        release.SetResult();
        await handle.Room.ResultTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new ushort[] { 612, 614, 615, 576, 620 }, guest.Opcodes);
        Assert.Equal(RoomPhase.Waiting, handle.Room.Phase);
    }

    [Fact]
    public async Task LiveTransportFailureRemainsObservable()
    {
        var options = FastOptions();
        var (handle, host, _) = TwoSeatRoom(options);
        host.BeforeSend = (opcode, _) => opcode == 615
            ? ValueTask.FromException(new IOException("Live transport failed.")) : ValueTask.CompletedTask;
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance);
        await loop.Result.HandleGameOverAsync(host, new(InGameOpcodes.C2SGameOver, Array.Empty<byte>()), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => handle.Room.ResultTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(handle.Room.ResultTask.IsFaulted);
    }

    [Fact]
    public async Task ClosingTheRoomCancelsItsLongResultDelayPromptly()
    {
        var options = FastOptions();
        options.ResultScoreboardDelayMs = 600_000;
        var (handle, host, guest) = TwoSeatRoom(options);
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance);
        await loop.Result.HandleGameOverAsync(host, new(InGameOpcodes.C2SGameOver, Array.Empty<byte>()), CancellationToken.None);
        handle.Registry.Detach(host.Id);
        handle.Registry.Detach(guest.Id);
        await handle.Room.ResultTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RoomPhase.Closed, handle.Room.Phase);
        Assert.Equal(new ushort[] { 612 }, host.Opcodes);
        Assert.Equal(new ushort[] { 612 }, guest.Opcodes);
    }

    [Fact]
    public async Task DepartureBeforeResultOpenDoesNotCancelTheInitiatingRequest()
    {
        var options = FastOptions();
        var (handle, host, guest) = TwoSeatRoom(options);
        host.BeforeSend = (opcode, _) =>
        {
            if (opcode != 612)
                return ValueTask.CompletedTask;
            handle.Registry.Detach(host.Id);
            handle.Registry.Detach(guest.Id);
            return ValueTask.FromException(new IOException("The room closed before its first result frame."));
        };
        var loop = new InGameFlows(options, handle.Registry, NullLogger.Instance);
        await loop.Result.HandleGameOverAsync(host, new(InGameOpcodes.C2SGameOver, Array.Empty<byte>()), CancellationToken.None);
        await handle.Room.ResultTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RoomPhase.Closed, handle.Room.Phase);
        Assert.Empty(host.Opcodes);
        Assert.Empty(guest.Opcodes);
    }

    [Theory]
    [InlineData(-1, 1, 1, 0)]
    [InlineData(1, -1, 1, 0)]
    [InlineData(1, 1, -1, 0)]
    [InlineData(1, 1, 1, 1)]
    [InlineData(600001, 1, 1, 0)]
    public void ResultOptionsRejectUnsupportedFlagAndUnboundedDelays(int scoreboard, int reward, int close, byte flag)
    {
        var options = new InGameOptions
        {
            ResultScoreboardDelayMs = scoreboard, ResultRewardDelayMs = reward,
            ResultCloseGraceMs = close, ResultClose = flag
        };
        var errors = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        Assert.False(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(options,
            new System.ComponentModel.DataAnnotations.ValidationContext(options), errors, true));
    }

    private static InGameOptions FastOptions() => new()
    {
        ResultScoreboardDelayMs = 10, ResultRewardDelayMs = 10, ResultCloseGraceMs = 10
    };

    private static async Task SendStatsAckAsync(InGameFlows loop, RecordingSession session, int[] values)
    {
        var body = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(i * 4), values[i]);
        await loop.Result.HandleResultStatsAsync(
            session, new InboundPacket(InGameOpcodes.C2SGameResultStats, body), CancellationToken.None);
    }

    private static (RoomHandle Room, RecordingSession Host, RecordingSession Guest) TwoSeatRoom(InGameOptions options)
    {
        var registry = new RoomRegistry(options);
        var host = new RecordingSession(10);
        var (room, _) = registry.Create(host, RoomWireSupport.WaitingSettingWithMap(), 1, "host");
        var guest = new RecordingSession(11);
        registry.Join(guest, room.Id, 2, "guest");
        return (new RoomHandle(registry, room), host, guest);
    }

    private sealed record RoomHandle(RoomRegistry Registry, PlayRoom Room);

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

        public Func<ushort, CancellationToken, ValueTask>? BeforeSend { get; set; }

        public async ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            if (Connection.IsClosed)
                throw new IOException("Connection is closed.");
            if (BeforeSend is not null)
                await BeforeSend(opcode, ct);
            ct.ThrowIfCancellationRequested();
            lock (_sent)
                _sent.Add((opcode, payload.ToArray()));
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
        public ValueTask CloseAsync(string reason)
        {
            IsClosed = true;
            return ValueTask.CompletedTask;
        }
    }
}
