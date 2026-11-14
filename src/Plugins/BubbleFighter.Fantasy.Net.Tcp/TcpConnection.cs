using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using BubbleFighter.Fantasy.Abstractions.Net;

namespace BubbleFighter.Fantasy.Net.Tcp;

/// <summary>
/// An accepted TCP connection exposed through <see cref="System.IO.Pipelines"/>. The inbound side reads from the
/// socket only when the protocol asks for more: bytes the protocol has not asked for stay in the operating
/// system's socket buffer, where the peer's send window bounds them, and the connection holds no buffer of its own
/// while it waits. The protocol's own buffer is therefore the only inbound memory a connection can pin.
/// </summary>
public sealed class TcpConnection : IConnection
{
    /// <summary>
    /// The size of one socket read. It only shapes how many reads a burst takes; what a connection may hold is
    /// decided by the protocol that consumes <see cref="Input"/>.
    /// </summary>
    private const int ReadBufferBytes = 4096;

    private readonly Socket _socket;
    private readonly Stream _stream;
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
        // A zero-byte read waits for data without a buffer, so an idle connection rents nothing.
        Input = PipeReader.Create(_stream, new StreamPipeReaderOptions(
            bufferSize: ReadBufferBytes, minimumReadSize: ReadBufferBytes / 4, leaveOpen: true, useZeroByteReads: true));
        Output = PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true));
        Touch();
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

    private void Touch() => Interlocked.Exchange(ref _lastActivityUtcTicks, DateTime.UtcNow.Ticks);
}
