using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions.Net;

namespace BubbleFighter.Fantasy.Net.Tcp;

/// <summary>
/// An accepted TCP connection exposed through <see cref="System.IO.Pipelines"/>. The
/// inbound side is a bounded <see cref="Pipe"/>: a pump copies the stream into it and the
/// pause/resume thresholds stop the producer when the consumer falls behind, so a slow
/// protocol cannot grow memory without limit.
/// </summary>
public sealed class TcpConnection : IConnection
{
    private const long PauseWriterThreshold = 1 << 20;
    private const long ResumeWriterThreshold = 512 << 10;

    private readonly Socket _socket;
    private readonly Stream _stream;
    private readonly Pipe _inbound;
    private int _closed;
    private long _lastActivityUtcTicks;

    /// <summary>Creates the connection around an accepted socket.</summary>
    /// <param name="id">The kernel-assigned connection id.</param>
    /// <param name="socket">The accepted socket.</param>
    /// <param name="noDelay">Whether to disable Nagle's algorithm.</param>
    public TcpConnection(long id, Socket socket, bool noDelay)
        : this(id, socket, new TcpListenerOptions { NoDelay = noDelay, KeepAlive = false })
    {
    }

    /// <summary>Creates a connection with the listener's TCP activity and failure-detection policy.</summary>
    /// <param name="id">The kernel-assigned connection id.</param>
    /// <param name="socket">The accepted socket.</param>
    /// <param name="options">The validated listener settings applied to the accepted socket.</param>
    public TcpConnection(long id, Socket socket, TcpListenerOptions options)
    {
        Id = id;
        _socket = socket;
        ConfigureSocket(socket, options);
        RemoteEndPoint = socket.RemoteEndPoint ?? new IPEndPoint(IPAddress.None, 0);
        _stream = new ActivityStream(new NetworkStream(socket, ownsSocket: true), Touch);
        _inbound = new Pipe(new PipeOptions(
            pauseWriterThreshold: PauseWriterThreshold,
            resumeWriterThreshold: ResumeWriterThreshold,
            useSynchronizationContext: false));
        Input = _inbound.Reader;
        Output = PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true));
        Touch();
        _ = Task.Run(PumpAsync);
    }

    internal static void ConfigureSocket(Socket socket, TcpListenerOptions options)
    {
        socket.NoDelay = options.NoDelay;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, options.KeepAlive);
        if (!options.KeepAlive) return;
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, options.KeepAliveTimeSeconds);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, options.KeepAliveIntervalSeconds);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, options.KeepAliveRetryCount);
    }

    /// <inheritdoc />
    public long Id { get; }

    /// <inheritdoc />
    public EndPoint RemoteEndPoint { get; }

    /// <inheritdoc />
    public PipeReader Input { get; }

    /// <inheritdoc />
    public PipeWriter Output { get; }

    /// <inheritdoc />
    public ConcurrentDictionary<string, object> Items { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool IsClosed => _closed != 0;

    /// <summary>Gets the reason passed to the first <see cref="CloseAsync"/> call, when any.</summary>
    public string? CloseReason { get; private set; }

    /// <summary>Gets the UTC time of the last successful read or write.</summary>
    public DateTime LastActivityUtc => new(Interlocked.Read(ref _lastActivityUtcTicks), DateTimeKind.Utc);

    /// <inheritdoc />
    public async ValueTask CloseAsync(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        CloseReason ??= reason;
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        try
        {
            Input.CancelPendingRead();
        }
        catch (ObjectDisposedException) { }
        _stream.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        var writer = _inbound.Writer;
        try
        {
            while (true)
            {
                var memory = writer.GetMemory(8192);
                var read = await _stream.ReadAsync(memory).ConfigureAwait(false);
                if (read == 0)
                    break;
                writer.Advance(read);
                var flush = await writer.FlushAsync().ConfigureAwait(false);
                if (flush.IsCompleted || flush.IsCanceled)
                    break;
            }
            await writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await writer.CompleteAsync(ex).ConfigureAwait(false);
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityUtcTicks, DateTime.UtcNow.Ticks);
}
