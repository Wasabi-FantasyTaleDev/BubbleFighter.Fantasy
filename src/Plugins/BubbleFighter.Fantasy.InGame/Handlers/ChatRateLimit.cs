using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

// One budget covers both chat entry points before commands or audience fan-out.
internal sealed class ChatRateLimit(InGameOptions options, ILogger logger, TimeProvider? clock = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<long, Window> _connections = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private bool _disposed;

    internal PacketHandler Wrap(PacketHandler handler) => (session, packet, ct) =>
        Admit(session, ct) ? handler(session, packet, ct) : ValueTask.CompletedTask;

    internal MessageHandler<T> Wrap<T>(MessageHandler<T> handler) where T : IMessage => (session, message, ct) =>
        Admit(session, ct) ? handler(session, message, ct) : ValueTask.CompletedTask;

    internal ValueTask Filter<T>(ISession session, T message, MessageHandler<T> next, CancellationToken ct) where T : IMessage =>
        Admit(session, ct) ? next(session, message, ct) : ValueTask.CompletedTask;

    private bool Admit(ISession session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var warn = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!options.ChatRateLimitEnabled) return true;
            if (session.Connection.IsClosed) return false;
            var now = _clock.GetTimestamp();
            if (!_connections.TryGetValue(session.Connection.Id, out var window))
            {
                if (_connections.Count >= options.ChatRateLimitMaxSessions)
                    throw new InvalidOperationException("Chat rate-limit connection capacity is exhausted; raise chat_rate_limit_max_sessions.");
                window = new Window(now);
                _connections.Add(session.Connection.Id, window);
            }
            if (_clock.GetElapsedTime(window.Start, now).TotalMilliseconds >= options.ChatRateLimitWindowMs)
            {
                window.Start = now;
                window.Count = 0;
                window.Warned = false;
            }
            if (window.Count < options.ChatRateLimitMessages)
            {
                window.Count++;
                return true;
            }
            warn = !window.Warned;
            window.Warned = true;
        }
        // Rejected messages cannot amplify the log sink: report only once per connection/window.
        if (warn)
            logger.LogWarning("Session {Session} on connection {Connection} exceeded chat rate limit ({Messages} messages per {Window} ms); suppressing chat until its window resets.",
                session.Id, session.Connection.Id, options.ChatRateLimitMessages, options.ChatRateLimitWindowMs);
        return false;
    }

    internal void OnConnectionClosed(ConnectionClosedEvent closed)
    {
        lock (_gate) _connections.Remove(closed.Connection.Id);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _connections.Clear();
        }
    }

    private sealed class Window(long start)
    {
        internal long Start = start;
        internal int Count;
        internal bool Warned;
    }
}
