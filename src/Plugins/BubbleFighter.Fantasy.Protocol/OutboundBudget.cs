using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Protocol;

/// <summary>
/// The outbound bytes all sessions of one protocol row may hold queued at once. Each session already stops at its own
/// <c>send_queue_bytes</c>, but many clients that stop reading together could still hold that many times over; this is
/// the row-wide ceiling. A sender is never made to wait: when a frame would cross the ceiling, the session holding the
/// most queued bytes, which is the one that stopped reading, is closed and the frame is admitted while that session's
/// bytes are released.
/// </summary>
internal sealed class OutboundBudget(long limitBytes, ILogger logger)
{
    private readonly ConcurrentDictionary<long, WireSession> _sessions = new();
    private long _used;
    private long _lastReportTicks = long.MinValue;

    /// <summary>Gets the bytes queued by every session of the row.</summary>
    internal long UsedBytes => Interlocked.Read(ref _used);

    internal void Add(WireSession session) => _sessions[session.Id] = session;

    internal void Remove(WireSession session) => _sessions.TryRemove(session.Id, out _);

    /// <summary>
    /// Reserves <paramref name="bytes"/> for <paramref name="sender"/>. Returns false only when the sender itself holds
    /// the most queued bytes; otherwise the largest holder is closed and the bytes are reserved.
    /// </summary>
    internal bool TryReserve(WireSession sender, int bytes)
    {
        while (true)
        {
            var used = Interlocked.Read(ref _used);
            if (used + bytes <= limitBytes)
            {
                if (Interlocked.CompareExchange(ref _used, used + bytes, used) == used) return true;
                continue;
            }
            var largest = _sessions.Values.MaxBy(session => session.RetainedBytes);
            if (largest is null || ReferenceEquals(largest, sender)) return false;
            Report(largest);
            largest.Evict("protocol send budget");
            // The evicted session's bytes leave as its writer drops them; until then the row may exceed the ceiling by
            // the frames admitted meanwhile, each of which also closes the next largest holder.
            Interlocked.Add(ref _used, bytes);
            return true;
        }
    }

    internal void Release(int bytes) => Interlocked.Add(ref _used, -bytes);

    private void Report(WireSession victim)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastReportTicks);
        if (now - last < 1000 || Interlocked.CompareExchange(ref _lastReportTicks, now, last) != last) return;
        logger.LogWarning(
            "Protocol send budget of {Limit} bytes reached with {Used} queued; closing session {Id} on connection {Connection}, which held {Held} bytes.",
            limitBytes, UsedBytes, victim.Id, victim.Connection.Id, victim.RetainedBytes);
    }
}
