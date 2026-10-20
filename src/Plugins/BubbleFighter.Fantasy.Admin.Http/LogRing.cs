using System.Globalization;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Admin.Http;

/// <summary>One captured log line as the web interface reads it.</summary>
/// <param name="Seq">The position in the capture order, starting at 1 and never reused while the process lives.</param>
/// <param name="Utc">When the line was captured.</param>
/// <param name="Level">One of <c>debug</c>, <c>info</c>, <c>warn</c> or <c>error</c>.</param>
/// <param name="Category">The logger category or event source that produced the line.</param>
/// <param name="Message">The formatted message, cut to <see cref="LogRing.MaxMessageLength"/> characters.</param>
public sealed record LogEntry(long Seq, DateTime Utc, string Level, string Category, string Message);

/// <summary>
/// Fixed-capacity in-memory ring of recent log lines. A line that no longer fits replaces the
/// oldest one, so memory stays bounded however long the server runs; readers page forward by
/// sequence number and learn from the first returned sequence when lines were lost.
/// </summary>
public sealed class LogRing
{
    /// <summary>The longest message kept; longer ones are cut and end with an ellipsis so one line cannot dominate the ring.</summary>
    public const int MaxMessageLength = 4000;

    private readonly Lock _gate = new();
    private readonly LogEntry?[] _slots;
    private long _last;

    /// <summary>Creates a ring that keeps the newest <paramref name="capacity"/> lines.</summary>
    /// <param name="capacity">The number of lines to keep; at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the capacity is below 1.</exception>
    public LogRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _slots = new LogEntry?[capacity];
    }

    /// <summary>Gets the number of lines the ring keeps.</summary>
    public int Capacity => _slots.Length;

    /// <summary>Gets the sequence number of the newest line, or 0 when nothing was captured yet.</summary>
    public long LastSeq
    {
        get { lock (_gate) return _last; }
    }

    /// <summary>Adds a line, replacing the oldest one when the ring is full.</summary>
    /// <param name="level">The source log level; trace and debug map to <c>debug</c>, critical to <c>error</c>.</param>
    /// <param name="category">The logger category or event source.</param>
    /// <param name="message">The formatted message.</param>
    public void Append(LogLevel level, string category, string message)
    {
        var text = message.Length > MaxMessageLength ? string.Concat(message.AsSpan(0, MaxMessageLength - 1), "\u2026") : message;
        var name = level switch
        {
            LogLevel.Warning => "warn",
            LogLevel.Error or LogLevel.Critical => "error",
            LogLevel.Information => "info",
            _ => "debug",
        };
        lock (_gate)
        {
            _last++;
            _slots[(int)((_last - 1) % _slots.Length)] = new LogEntry(_last, DateTime.UtcNow, name, category, text);
        }
    }

    /// <summary>
    /// Reads the lines captured after <paramref name="after"/>, oldest first. A cursor older than
    /// the ring starts at the oldest line still kept; a cursor beyond the newest line (the
    /// server restarted since the reader last asked) starts from the beginning.
    /// </summary>
    /// <param name="after">The sequence number of the last line the reader already has; 0 reads from the start.</param>
    /// <param name="limit">The most lines to return; at least 1.</param>
    /// <returns>The lines and the cursor to pass as <paramref name="after"/> next time.</returns>
    public (IReadOnlyList<LogEntry> Entries, long Next) Read(long after, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        lock (_gate)
        {
            if (after > _last || after < 0)
                after = 0;
            var oldest = Math.Max(1, _last - _slots.Length + 1);
            var first = Math.Max(after + 1, oldest);
            var count = (int)Math.Min(limit, Math.Max(0, _last - first + 1));
            var entries = new LogEntry[count];
            for (var i = 0; i < count; i++)
                entries[i] = _slots[(int)((first + i - 1) % _slots.Length)]!;
            return (entries, count > 0 ? first + count - 1 : _last);
        }
    }
}

/// <summary>
/// Feeds a <see cref="LogRing"/> from the logging pipeline of the administration endpoint.
/// It only sees what that pipeline is given: this plugin has no hook into the host's own logger
/// factory, so lines from other rows reach the ring only through the events the admin row observes.
/// </summary>
internal sealed class RingLoggerProvider(LogRing ring) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RingLogger(ring, categoryName);

    public void Dispose() { }

    private sealed class RingLogger(LogRing ring, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var message = formatter(state, exception);
            if (exception is not null)
                message = string.Create(CultureInfo.InvariantCulture, $"{message} ({exception.GetType().Name}: {exception.Message})");
            ring.Append(logLevel, category, message);
        }
    }
}
