using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Diagnostics;

// Copy borrowed payloads on the observer; hexadecimal encoding and disk I/O stay on the worker.
internal sealed class PacketCapture : IAsyncDisposable
{
    private readonly AsyncLogQueue<CapturedPacket> _queue;
    private readonly int _byteLimit;
    private long _pendingBytes;
    private long _sequence;

    internal PacketCapture(PacketLogOptions options, Action<Exception> reportFailure)
    {
        _byteLimit = options.CaptureMaxQueuedBytes;
        var sink = new PacketLogPlugin.RollingFileSink(options.CaptureFile, options.FileTimeZone,
            options.CaptureMaxFileBytes, options.MaxLogFiles, prefixTimestamp: false);
        _queue = new AsyncLogQueue<CapturedPacket>(options.CaptureQueueCapacity,
            TimeSpan.FromMilliseconds(options.ShutdownFlushTimeoutMs), async packet =>
            {
                try
                {
                    await sink.WriteLineAsync(packet.Timestamp, Serialize(packet)).ConfigureAwait(false);
                }
                finally { Interlocked.Add(ref _pendingBytes, -packet.Payload.Length); }
            }, lost => sink.WriteLineAsync(DateTime.UtcNow, JsonSerializer.Serialize(new
            {
                version = 1, type = "capture_overflow", timestamp = DateTime.UtcNow,
                dropped = lost, reason = "Capture queue or payload memory bound reached; gameplay packets were not dropped.",
            })), () => new ValueTask(sink.FlushAsync()), reportFailure, sink.Dispose,
            discard: packet => Interlocked.Add(ref _pendingBytes, -packet.Payload.Length));
    }

    internal void Record(ISession session, string protocol, string direction, ushort opcode, string name,
        ReadOnlyMemory<byte> payload, bool? handled)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        if (!Reserve(payload.Length))
        {
            _queue.RecordDrop();
            return;
        }
        var accepted = false;
        try
        {
            var packet = new CapturedPacket(sequence, DateTime.UtcNow, protocol, direction,
                session.Id, session.Connection.Id, session.Connection.RemoteEndPoint.ToString() ?? "",
                opcode, name, payload.ToArray(), handled);
            accepted = _queue.Enqueue(packet);
        }
        finally
        {
            if (!accepted) Interlocked.Add(ref _pendingBytes, -payload.Length);
        }
    }

    private bool Reserve(int bytes)
    {
        while (true)
        {
            var pending = Interlocked.Read(ref _pendingBytes);
            if (bytes > _byteLimit - pending) return false;
            if (Interlocked.CompareExchange(ref _pendingBytes, pending + bytes, pending) == pending) return true;
        }
    }

    internal static string Serialize(CapturedPacket packet) => JsonSerializer.Serialize(new
    {
        version = 1, type = "packet", sequence = packet.Sequence, timestamp = packet.Timestamp,
        protocol = packet.Protocol, direction = packet.Direction, session = packet.Session,
        connection = packet.Connection, remote = packet.Remote, opcode = packet.Opcode,
        name = packet.Name, length = packet.Payload.Length, handled = packet.Handled,
        payload_hex = Convert.ToHexString(packet.Payload),
    });

    public ValueTask DisposeAsync() => _queue.DisposeAsync();

    internal sealed record CapturedPacket(long Sequence, DateTime Timestamp, string Protocol, string Direction,
        long Session, long Connection, string Remote, ushort Opcode, string Name, byte[] Payload, bool? Handled);
}
