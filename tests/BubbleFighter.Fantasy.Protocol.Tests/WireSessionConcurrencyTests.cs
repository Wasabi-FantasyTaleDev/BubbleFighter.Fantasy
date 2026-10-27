using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Wire;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>
/// Concurrency pin for the session send path. Concurrent <see cref="ISession.SendAsync"/>
/// calls on one session must encode, encrypt with the continuous server-to-client cipher,
/// and write to the connection while holding the send gate, so the byte stream the client
/// reads is always in encryption order.
/// </summary>
public sealed class WireSessionConcurrencyTests
{
    /// <summary>
    /// Fires many concurrent sends on one session, captures the connection output, replays
    /// the server-to-client cipher over it in wire order, and requires every frame to
    /// decode intact and the whole set of frames to come back exactly as sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deterministic failure shape of the defect this pins: the send gate used to be
    /// acquired only around the connection write, so <see cref="PacketFrame_8Bytes.Encode"/> and the
    /// continuous cipher's <see cref="SimpleCipher.EncryptInPlace"/> ran before it. Two
    /// concurrent sends could then encrypt in a different order than they wrote to the
    /// connection, and because the server-to-client cipher state is continuous, the
    /// client's decryption desyncs at the first inverted frame and every later frame
    /// decrypts to garbage (observed live as a frame header integrity failure right after
    /// two handlers broadcast to one session at once).
    /// </para>
    /// <para>
    /// The corruption is order- and scheduling-dependent - which sends invert, and whether
    /// they invert at all, varies run to run - so this pin does not assert a specific
    /// global frame order. It asserts per-frame integrity against the captured stream
    /// instead: each decrypted frame must carry a valid header and payload integrity byte,
    /// the expected opcode and payload length, and the frames must form exactly a
    /// permutation of the set that was sent, with the stream consumed down to the last
    /// byte so no interleaved or dropped data can hide.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ConcurrentSendsReachTheWireIntactInEncryptionOrder()
    {
        const int frameCount = 50;
        const int payloadSize = sizeof(int);
        var connection = new OutboundCaptureConnection();
        var session = NewWireSession(connection, TestWireDialects.Get("kr-1256").CreateEncryptor(PacketDirection.ServerToClient), new EventBus(NullLogger.Instance));

        // Distinct 4-byte payloads, i32 1..frameCount, so every frame is uniquely
        // identifiable after decryption and the sent set forms an exact multiset pin.
        var sent = Enumerable.Range(1, frameCount)
            .Select(value =>
            {
                var payload = new byte[payloadSize];
                BinaryPrimitives.WriteInt32LittleEndian(payload, value);
                return payload;
            })
            .ToArray();

        await Task.WhenAll(sent
            .Select(payload => Task.Run(() => session.SendAsync(WirePins.PatchReplyOpcode, payload, CancellationToken.None).AsTask()))
            .ToArray());
        await FlushAsync(session);

        var wire = await connection.ReadAllOutputAsync();
        Assert.Equal(frameCount * (PacketFrame_8Bytes.HeaderSize + payloadSize), wire.Length);

        // Replay the continuous cipher the way the client does: over the captured byte
        // stream, in wire order, from the server-to-client seed. A fresh instance is
        // required because the session's own cipher state has already advanced.
        var client = SimpleCipher.ServerToClient();
        client.DecryptInPlace(wire);

        var expected = sent.Select(Convert.ToHexString).Order().ToArray();
        var decoded = new List<string>(frameCount);
        var offset = 0;
        while (offset < wire.Length)
        {
            Assert.True(
                wire.Length - offset >= PacketFrame_8Bytes.HeaderSize,
                $"frame {decoded.Count}: stream ends mid-header after {offset} bytes; the frames on the wire no longer tile the stream");
            Assert.True(
                PacketFrame_8Bytes.TryReadHeader(wire.AsSpan(offset, PacketFrame_8Bytes.HeaderSize), out var header),
                $"frame {decoded.Count}: header integrity failure after cipher replay; the wire order diverged from the encryption order");
            Assert.Equal(WirePins.PatchReplyOpcode, header.Opcode);
            Assert.Equal(payloadSize, header.PayloadLength);
            Assert.True(
                wire.Length - offset >= PacketFrame_8Bytes.HeaderSize + header.PayloadLength,
                $"frame {decoded.Count}: declared payload of {header.PayloadLength} bytes runs past the end of the stream");
            var payload = wire.AsSpan(offset + PacketFrame_8Bytes.HeaderSize, header.PayloadLength).ToArray();
            Assert.True(
                PacketFrame_8Bytes.ValidatePayload(header, payload),
                $"frame {decoded.Count}: payload integrity failure; frame bytes were interleaved with another send");
            decoded.Add(Convert.ToHexString(payload));
            offset += PacketFrame_8Bytes.HeaderSize + header.PayloadLength;
        }

        Assert.Equal(frameCount, decoded.Count);
        Assert.Equal(expected, decoded.Order().ToArray());
    }

    /// <summary>
    /// Builds the session under test. <c>WireSession</c> is internal to the protocol
    /// assembly and that assembly grants no test visibility, so construction goes through
    /// reflection and the session is then driven through the public <see cref="ISession"/>
    /// surface only.
    /// </summary>
    private static ISession NewWireSession(IConnection connection, IStreamTransform cipher, IEventBus events,
        int sendQueueCapacity = 4096, int sendQueueBytes = 16 * 1024 * 1024)
    {
        var type = typeof(ProtocolPlugin).Assembly.GetType("BubbleFighter.Fantasy.Protocol.WireSession")
            ?? throw new InvalidOperationException("WireSession was not found in the protocol assembly.");
        var profile = new ClientProfile("kr-test", "kr", 1256, 0, 0, "WireSession concurrency pin");
        return (ISession)(Activator.CreateInstance(type, connection, profile, ProtocolPlugin.GameProtocolName,
                TestWireDialects.Get("kr-1256"), cipher, events, 64, sendQueueCapacity, sendQueueBytes,
                (Microsoft.Extensions.Logging.ILogger)NullLogger.Instance)
            ?? throw new InvalidOperationException("WireSession construction returned null."));
    }

    // Waits until every frame queued so far is written; sends only queue.
    private static Task FlushAsync(ISession session) =>
        (Task)session.GetType().GetMethod("FlushAsync")!.Invoke(session, null)!;

    private static Task StopAsync(ISession session) =>
        (Task)session.GetType().GetMethod("StopAsync")!.Invoke(session, null)!;

    /// <summary>
    /// Frames reach the wire in the order one sender queued them, and the payload is copied
    /// at queue time so a caller reusing its buffer cannot change a queued frame.
    /// </summary>
    [Fact]
    public async Task QueuedFramesAreWrittenInSendOrderFromACopiedPayload()
    {
        var connection = new OutboundCaptureConnection();
        var session = NewWireSession(connection, TestWireDialects.Get("kr-1256").CreateEncryptor(PacketDirection.ServerToClient),
            new EventBus(NullLogger.Instance));
        var buffer = new byte[4];
        for (var value = 1; value <= 20; value++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
            await session.SendAsync(WirePins.PatchReplyOpcode, buffer, CancellationToken.None);
        }
        await FlushAsync(session);

        var wire = await connection.ReadAllOutputAsync();
        SimpleCipher.ServerToClient().DecryptInPlace(wire);
        var values = new List<int>();
        for (var offset = 0; offset < wire.Length; offset += PacketFrame_8Bytes.HeaderSize + 4)
            values.Add(BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(offset + PacketFrame_8Bytes.HeaderSize, 4)));
        Assert.Equal(Enumerable.Range(1, 20), values);
    }

    /// <summary>
    /// A client that stops reading fills its queue; the next send fails and closes that
    /// connection instead of waiting, and later sends fail at once.
    /// </summary>
    [Fact]
    public async Task AFullSendQueueClosesTheConnectionAndLaterSendsFail()
    {
        var connection = new StalledConnection();
        var session = NewWireSession(connection, TestWireDialects.Get("kr-1256").CreateEncryptor(PacketDirection.ServerToClient),
            new EventBus(NullLogger.Instance), sendQueueCapacity: 2);

        // The writer takes the first frame and blocks on the stalled output; two more fill the queue.
        await session.SendAsync(1, new byte[4], CancellationToken.None);
        await connection.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Queued sends complete at once although nothing reaches the stalled client.
        Assert.True(session.SendAsync(2, new byte[4], CancellationToken.None).IsCompletedSuccessfully);
        Assert.True(session.SendAsync(3, new byte[4], CancellationToken.None).IsCompletedSuccessfully);

        await Assert.ThrowsAsync<IOException>(() => session.SendAsync(4, new byte[4], CancellationToken.None).AsTask());
        Assert.Equal("send queue full", await connection.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<IOException>(() => session.SendAsync(5, new byte[4], CancellationToken.None).AsTask());
        await StopAsync(session).WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>The active write still consumes the byte budget even after it leaves the frame queue.</summary>
    [Fact]
    public async Task InFlightPayloadCountsAgainstByteBudget()
    {
        var connection = new StalledConnection();
        var session = NewWireSession(connection, TestWireDialects.Get("kr-1256").CreateEncryptor(PacketDirection.ServerToClient),
            new EventBus(NullLogger.Instance), sendQueueCapacity: 128, sendQueueBytes: 8);
        try
        {
            await session.SendAsync(1, new byte[4], default);
            await connection.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.SendAsync(2, new byte[4], default);
            await Assert.ThrowsAsync<IOException>(() => session.SendAsync(3, new byte[1], default).AsTask());
            Assert.Equal("send queue byte limit", await connection.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAsync<IOException>(() => session.SendAsync(4, ReadOnlyMemory<byte>.Empty, default).AsTask());
        }
        finally { await StopAsync(session).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    /// <summary>A payload larger than the byte budget is refused before any output is started.</summary>
    [Fact]
    public async Task OversizedPayloadClosesWithoutStartingAWrite()
    {
        var connection = new StalledConnection();
        var session = NewWireSession(connection, TestWireDialects.Get("kr-1256").CreateEncryptor(PacketDirection.ServerToClient),
            new EventBus(NullLogger.Instance), sendQueueBytes: 3);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => session.SendAsync(1, new byte[4], default).AsTask());
            Assert.Equal("send queue byte limit", await connection.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(connection.WriteStarted.Task.IsCompleted);
        }
        finally { await StopAsync(session).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    /// <summary>Successful writes release their budget, and an already canceled send reserves nothing.</summary>
    [Fact]
    public async Task CompletedAndCanceledSendsDoNotExhaustByteBudget()
    {
        var session = NewWireSession(new OutboundCaptureConnection(),
            TestWireDialects.Get("kr-1256").CreateEncryptor(PacketDirection.ServerToClient),
            new EventBus(NullLogger.Instance), sendQueueBytes: 4);
        try
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendAsync(1, new byte[4], canceled.Token).AsTask());
            for (var i = 0; i < 3; i++)
            {
                await session.SendAsync(1, new byte[4], default);
                await FlushAsync(session).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally { await StopAsync(session).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    /// <summary>Concurrent producers share one exact budget, and stopping releases both queued and active payloads.</summary>
    [Fact]
    public async Task ConcurrentAdmissionAndStopKeepTheByteBudgetBalanced()
    {
        const int producers = 64;
        const int payloadSize = 4;
        var connection = new StalledConnection();
        var session = NewWireSession(connection,
            TestWireDialects.Get("kr-1256").CreateEncryptor(PacketDirection.ServerToClient),
            new EventBus(NullLogger.Instance), sendQueueCapacity: 128,
            sendQueueBytes: producers * payloadSize);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, producers).Select(_ =>
                Task.Run(() => session.SendAsync(1, new byte[payloadSize], default).AsTask())));
            await connection.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(producers * payloadSize, RetainedBytes(session));
            await Assert.ThrowsAsync<IOException>(() => session.SendAsync(2, new byte[1], default).AsTask());
        }
        finally { await StopAsync(session).WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(0, RetainedBytes(session));
    }

    /// <summary>Frame-count overflow refunds its refused payload reservation before the stopped writer drains.</summary>
    [Fact]
    public async Task FrameCountOverflowReleasesEveryPayloadReservation()
    {
        var connection = new StalledConnection();
        var session = NewWireSession(connection,
            TestWireDialects.Get("kr-1256").CreateEncryptor(PacketDirection.ServerToClient),
            new EventBus(NullLogger.Instance), sendQueueCapacity: 1, sendQueueBytes: 128);
        try
        {
            await session.SendAsync(1, new byte[4], default);
            await connection.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await session.SendAsync(2, new byte[4], default);
            await Assert.ThrowsAsync<IOException>(() => session.SendAsync(3, new byte[4], default).AsTask());
            Assert.Equal("send queue full", await connection.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(8, RetainedBytes(session));
        }
        finally { await StopAsync(session).WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(0, RetainedBytes(session));
    }

    private static long RetainedBytes(ISession session) =>
        (long)session.GetType().GetField("_retainedBytes",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(session)!;

    /// <summary>A send after the session stopped fails instead of disappearing.</summary>
    [Fact]
    public async Task SendAfterTheSessionStoppedFails()
    {
        var session = NewWireSession(new OutboundCaptureConnection(),
            TestWireDialects.Get("kr-1256").CreateEncryptor(PacketDirection.ServerToClient), new EventBus(NullLogger.Instance));
        await StopAsync(session);
        await Assert.ThrowsAsync<IOException>(() => session.SendAsync(1, new byte[4], CancellationToken.None).AsTask());
    }

    /// <summary>A public fence completes after the session has written its preceding frames.</summary>
    [Fact]
    public async Task PublicOutputDrainCompletesAfterPreviouslyQueuedFrames()
    {
        var connection = new OutboundCaptureConnection();
        var session = NewWireSession(connection, TestWireDialects.Get("kr-1256").CreateEncryptor(PacketDirection.ServerToClient),
            new EventBus(NullLogger.Instance));
        try
        {
            await session.SendAsync(1, new byte[] { 1, 2, 3 }, default);
            await Assert.IsAssignableFrom<ISessionOutputDrain>(session).DrainOutputAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await StopAsync(session).WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.NotEmpty(await connection.ReadAllOutputAsync());
    }

    [Fact]
    public async Task CancellingOutputDrainDoesNotCloseOrRemoveAlreadyQueuedFrames()
    {
        var connection = new StalledConnection();
        var session = NewWireSession(connection, TestWireDialects.Get("kr-1256").CreateEncryptor(PacketDirection.ServerToClient),
            new EventBus(NullLogger.Instance));
        try
        {
            await session.SendAsync(1, new byte[] { 1 }, default);
            await connection.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var waiting = Assert.IsAssignableFrom<ISessionOutputDrain>(session).DrainOutputAsync(cancellation.Token).AsTask();
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.False(connection.IsClosed);
            Assert.Equal(1, RetainedBytes(session));
        }
        finally { await StopAsync(session).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task OutputDrainFailsWhenSessionStopsBeforeItsFence()
    {
        var connection = new StalledConnection();
        var session = NewWireSession(connection, TestWireDialects.Get("kr-1256").CreateEncryptor(PacketDirection.ServerToClient),
            new EventBus(NullLogger.Instance));
        await session.SendAsync(1, new byte[] { 1 }, default);
        await connection.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = Assert.IsAssignableFrom<ISessionOutputDrain>(session).DrainOutputAsync(default).AsTask();
        await StopAsync(session).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => waiting);
        await Assert.ThrowsAsync<IOException>(() => ((ISessionOutputDrain)session).DrainOutputAsync(default).AsTask());
    }

    [Fact]
    public async Task OutputDrainCannotExceedTheExistingOutboundQueueBudget()
    {
        var connection = new StalledConnection();
        var session = NewWireSession(connection, TestWireDialects.Get("kr-1256").CreateEncryptor(PacketDirection.ServerToClient),
            new EventBus(NullLogger.Instance), sendQueueCapacity: 1);
        Task? first = null;
        try
        {
            await session.SendAsync(1, new byte[] { 1 }, default);
            await connection.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var drain = Assert.IsAssignableFrom<ISessionOutputDrain>(session);
            first = drain.DrainOutputAsync(default).AsTask();
            await Assert.ThrowsAsync<IOException>(() => drain.DrainOutputAsync(default).AsTask());
            Assert.False(first.IsCompleted);
        }
        finally { await StopAsync(session).WaitAsync(TimeSpan.FromSeconds(5)); }
        await Assert.ThrowsAsync<IOException>(() => first!);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RejectedTransportFlushCannotReportSuccessfulDrain(bool cancelled, bool completed)
    {
        var connection = new StalledConnection { RejectedFlush = new(cancelled, completed) };
        var session = NewWireSession(connection, TestWireDialects.Get("kr-1256").CreateEncryptor(PacketDirection.ServerToClient),
            new EventBus(NullLogger.Instance));
        try
        {
            await session.SendAsync(1, new byte[] { 1 }, default);
            await connection.Closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<IOException>(() => Assert.IsAssignableFrom<ISessionOutputDrain>(session).DrainOutputAsync(default).AsTask());
        }
        finally { await StopAsync(session).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    /// <summary>A connection whose output never completes a write until it is closed.</summary>
    private sealed class StalledConnection : IConnection
    {
        private readonly StalledWriter _writer;

        public StalledConnection() => _writer = new StalledWriter(this);

        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FlushResult? RejectedFlush { get; init; }
        public long Id { get; } = 2;
        public EndPoint RemoteEndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 1);
        public PipeReader Input => throw new NotSupportedException();
        public PipeWriter Output => _writer;
        public ConcurrentDictionary<string, object> Items { get; } = new(StringComparer.Ordinal);
        public bool IsClosed => Closed.Task.IsCompleted;

        public ValueTask CloseAsync(string reason)
        {
            Closed.TrySetResult(reason);
            return ValueTask.CompletedTask;
        }

        private sealed class StalledWriter(StalledConnection owner) : PipeWriter
        {
            private readonly byte[] _buffer = new byte[4096];
            public override void Advance(int bytes) { }
            public override Memory<byte> GetMemory(int sizeHint = 0) => _buffer;
            public override Span<byte> GetSpan(int sizeHint = 0) => _buffer;
            public override void CancelPendingFlush() { }
            public override void Complete(Exception? exception = null) { }

            public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
            {
                owner.WriteStarted.TrySetResult();
                if (owner.RejectedFlush is { } result) return result;
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new FlushResult(false, true);
            }
        }
    }

    /// <summary>
    /// Minimal <see cref="IConnection"/> standing in for an accepted connection: the
    /// session writes into an outbound pipe this test drains afterwards. Mirrors the
    /// loopback pipe pattern used by the handshake tests.
    /// </summary>
    internal sealed class OutboundCaptureConnection : IConnection
    {
        private readonly Pipe _inbound = new(new PipeOptions(useSynchronizationContext: false));
        private readonly Pipe _outbound = new(new PipeOptions(useSynchronizationContext: false));

        /// <summary>Initializes the connection with a loopback endpoint.</summary>
        public OutboundCaptureConnection() => RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 1);

        /// <inheritdoc />
        public long Id { get; } = 1;

        /// <inheritdoc />
        public EndPoint RemoteEndPoint { get; }

        /// <inheritdoc />
        public PipeReader Input => _inbound.Reader;

        /// <inheritdoc />
        public PipeWriter Output => _outbound.Writer;

        /// <inheritdoc />
        public ConcurrentDictionary<string, object> Items { get; } = new(StringComparer.Ordinal);

        /// <inheritdoc />
        public bool IsClosed => false;

        /// <inheritdoc />
        public async ValueTask CloseAsync(string reason)
        {
            await _inbound.Writer.CompleteAsync().ConfigureAwait(false);
            await _outbound.Writer.CompleteAsync().ConfigureAwait(false);
        }

        /// <summary>Completes the outbound pipe and returns every byte written to it.</summary>
        public async Task<byte[]> ReadAllOutputAsync()
        {
            await _outbound.Writer.CompleteAsync().ConfigureAwait(false);
            using var captured = new MemoryStream();
            while (true)
            {
                var result = await _outbound.Reader.ReadAsync().ConfigureAwait(false);
                var done = result.Buffer.IsEmpty && result.IsCompleted;
                foreach (var segment in result.Buffer)
                    captured.Write(segment.Span);
                _outbound.Reader.AdvanceTo(result.Buffer.End);
                if (done)
                    break;
            }
            _outbound.Reader.Complete();
            return captured.ToArray();
        }
    }
}
