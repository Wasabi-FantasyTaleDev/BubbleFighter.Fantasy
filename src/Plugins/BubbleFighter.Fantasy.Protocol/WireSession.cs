using System.Collections.Concurrent;
using System.Threading.Channels;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Protocol;

/// <summary>
/// A protocol session bound to one accepted connection. <see cref="SendAsync"/> copies the
/// frame into a bounded per-session queue and returns; one writer loop encodes, encrypts
/// with the server-to-client cipher and writes each frame in queue order, so a slow client
/// never holds up the handler or room operation that sent to it.
/// </summary>
internal sealed class WireSession : ISessionWithFeatures, ISessionOutputDrain, IDisposable
{
    private static long _nextId;

    private readonly IStreamTransform _cipher;
    private readonly IWireDialect _dialect;
    private readonly IEventBus _events;
    private readonly ILogger _logger;
    private readonly string _protocolName;
    private readonly SessionFeatures _features;
    private readonly Channel<Outbound> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _writer;
    private readonly long _sendQueueBytes;
    private readonly OutboundBudget? _budget;
    private long _retainedBytes;
    private long _peakRetainedBytes;
    private int _closed;

    // A queued frame, or a flush marker completed once every frame queued before it is written.
    private sealed record Outbound(ushort Opcode, byte[] Payload, TaskCompletionSource? Flushed = null);

    public WireSession(IConnection connection, ClientProfile profile, string protocolName, IWireDialect dialect,
        IStreamTransform cipher, IEventBus events, int maxFeatures, int sendQueueCapacity, ILogger logger)
        : this(connection, profile, protocolName, dialect, cipher, events, maxFeatures, sendQueueCapacity,
            new ProtocolOptions().SendQueueBytes, logger)
    {
    }

    public WireSession(IConnection connection, ClientProfile profile, string protocolName, IWireDialect dialect,
        IStreamTransform cipher, IEventBus events, int maxFeatures, int sendQueueCapacity, int sendQueueBytes, ILogger logger)
        : this(connection, profile, protocolName, dialect, cipher, events, maxFeatures, sendQueueCapacity, sendQueueBytes,
            null, logger)
    {
    }

    public WireSession(IConnection connection, ClientProfile profile, string protocolName, IWireDialect dialect,
        IStreamTransform cipher, IEventBus events, int maxFeatures, int sendQueueCapacity, int sendQueueBytes,
        OutboundBudget? budget, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sendQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sendQueueBytes, 1);
        Id = Interlocked.Increment(ref _nextId);
        Connection = connection;
        Profile = profile;
        _protocolName = protocolName;
        _cipher = cipher;
        _dialect = dialect;
        _events = events;
        _logger = logger;
        _features = new SessionFeatures(maxFeatures);
        _sendQueueBytes = sendQueueBytes;
        _budget = budget;
        // Admission uses TryWrite only: a full queue fails the send instead of parking the sender.
        _queue = Channel.CreateBounded<Outbound>(new BoundedChannelOptions(sendQueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _writer = Task.Run(() => WriteLoopAsync(_stop.Token));
        _budget?.Add(this);
    }

    /// <inheritdoc />
    public long Id { get; }

    /// <inheritdoc />
    public IConnection Connection { get; }

    /// <inheritdoc />
    public ClientProfile Profile { get; }

    /// <inheritdoc />
    public ConcurrentDictionary<string, object> Items { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ISessionFeatures Features => _features;

    /// <summary>Closes typed state admission without disposing caller-owned feature values.</summary>
    public void Dispose() => _features.Dispose();

    /// <inheritdoc />
    /// <remarks>
    /// Returns once the frame is queued. A full queue means the client stopped reading: the
    /// connection is closed with reason <c>send queue full</c> and this call throws. A send
    /// after the session stopped throws <see cref="IOException"/>.
    /// </remarks>
    public ValueTask SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return ValueTask.FromCanceled(ct);
        if (Volatile.Read(ref _closed) != 0)
            return ValueTask.FromException(Closed());
        if (!TryReserveBytes(payload.Length))
            return Overflow(opcode, "send queue byte limit");
        if (_budget is not null && !_budget.TryReserve(this, payload.Length))
        {
            Interlocked.Add(ref _retainedBytes, -payload.Length);
            return Overflow(opcode, "protocol send budget");
        }
        var admitted = false;
        try
        {
            admitted = _queue.Writer.TryWrite(new Outbound(opcode, payload.ToArray()));
            if (admitted) return ValueTask.CompletedTask;
        }
        finally
        {
            if (!admitted) Release(payload.Length);
        }
        if (Volatile.Read(ref _closed) != 0)
            return ValueTask.FromException(Closed());

        return Overflow(opcode, "send queue full");
    }

    private bool TryReserveBytes(int bytes)
    {
        while (true)
        {
            var retained = Interlocked.Read(ref _retainedBytes);
            if (bytes > _sendQueueBytes - retained) return false;
            if (Interlocked.CompareExchange(ref _retainedBytes, retained + bytes, retained) != retained)
                continue;
            // A lost race only under-reports the peak by one frame; it is a sizing aid, not a limit.
            if (retained + bytes > Volatile.Read(ref _peakRetainedBytes))
                Volatile.Write(ref _peakRetainedBytes, retained + bytes);
            return true;
        }
    }

    private void Release(int bytes)
    {
        Interlocked.Add(ref _retainedBytes, -bytes);
        _budget?.Release(bytes);
    }

    /// <summary>Gets the outbound payload bytes this session holds queued now.</summary>
    internal long RetainedBytes => Interlocked.Read(ref _retainedBytes);

    /// <summary>
    /// Closes the session because the protocol row's send budget is exhausted and it holds the most queued bytes. Its
    /// queued frames are dropped with the connection, which releases them.
    /// </summary>
    internal void Evict(string reason)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        _logger.LogError("Session {Id} on connection {Connection} dropped: {Reason}.", Id, Connection.Id, reason);
        StopQueue();
        _ = CloseObservedAsync(reason);
    }

    /// <summary>Gets the most outbound payload bytes this session has held at once.</summary>
    internal long PeakRetainedBytes => Volatile.Read(ref _peakRetainedBytes);

    private ValueTask Overflow(ushort opcode, string reason)
    {
        _logger.LogError(
            "Session {Id} on connection {Connection} dropped: {Reason} (opcode {Opcode}).",
            Id, Connection.Id, reason, opcode);
        StopQueue();
        _ = CloseObservedAsync(reason);
        return ValueTask.FromException(new IOException($"Session {Id} exceeded {reason}; the connection was closed."));
    }

    /// <summary>
    /// Completes once every frame queued before this call has been written, or faults when the
    /// session stops first. Test and diagnostic use; handlers never wait for the wire.
    /// </summary>
    public Task FlushAsync()
    {
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Volatile.Read(ref _closed) != 0 || !_queue.Writer.TryWrite(new Outbound(0, [], flushed)))
            flushed.TrySetException(Closed());
        return flushed.Task;
    }

    /// <inheritdoc />
    public ValueTask DrainOutputAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(FlushAsync().WaitAsync(cancellationToken));
    }

    /// <summary>
    /// Stops the writer loop and drops the frames it has not written. Called when the
    /// connection's protocol run ends; the connection is already closed or closing.
    /// </summary>
    public async Task StopAsync()
    {
        StopQueue();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _writer.ConfigureAwait(false);
        _stop.Dispose();
        _budget?.Remove(this);
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug(
                "Session {Id} on connection {Connection} queued at most {PeakBytes} outbound bytes of the {LimitBytes} allowed.",
                Id, Connection.Id, PeakRetainedBytes, _sendQueueBytes);
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var outbound in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (outbound.Flushed is { } flushed)
                {
                    flushed.TrySetResult();
                    continue;
                }
                try
                {
                    // Encryption is continuous; the single reader preserves frame and cipher order.
                    // The frame is encoded and enciphered in the pipe's own buffer instead of a new array per frame.
                    var output = Connection.Output;
                    var span = output.GetSpan(outbound.Payload.Length + _dialect.MaxFrameOverheadBytes);
                    var length = _dialect.EncodeFrame(outbound.Opcode, outbound.Payload, span);
                    _cipher.Apply(span[..length]);
                    output.Advance(length);
                    var written = await output.FlushAsync(ct).ConfigureAwait(false);
                    if (written.IsCanceled || written.IsCompleted)
                        throw new IOException("The outbound transport stopped before the frame write completed.");
                    // Without a subscriber (the packet log is off by default) no event is built per frame.
                    if (_events.HasHandlers<PacketSentEvent>())
                        _events.Emit(new PacketSentEvent(this, _protocolName, outbound.Opcode, outbound.Payload.Length)
                        {
                            Payload = outbound.Payload,
                        });
                }
                finally { Release(outbound.Payload.Length); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!Connection.IsClosed)
                _logger.LogError(ex, "Session {Id} on connection {Connection} could not write a frame; closing it.", Id, Connection.Id);
            StopQueue();
            await CloseObservedAsync("outbound write failed").ConfigureAwait(false);
        }
        finally
        {
            StopQueue();
            // Frames still queued are dropped with the connection; flush markers learn it.
            while (_queue.Reader.TryRead(out var dropped))
            {
                Release(dropped.Payload.Length);
                dropped.Flushed?.TrySetException(Closed());
            }
        }
    }

    private void StopQueue()
    {
        Volatile.Write(ref _closed, 1);
        _queue.Writer.TryComplete();
    }

    private async Task CloseObservedAsync(string reason)
    {
        try
        {
            await Connection.CloseAsync(reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Closing connection {Connection} after '{Reason}' failed.", Connection.Id, reason);
        }
    }

    private IOException Closed() => new($"Session {Id} is closed; the frame was not sent.");
}
