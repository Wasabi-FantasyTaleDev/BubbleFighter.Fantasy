using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Host;

/// <summary>A logging provider of the host's factory that forwards each entry to the current receivers.</summary>
internal sealed class ServerLogFeed : IServerLogFeed, ILoggerProvider
{
    private Action<LogLevel, string, string>[] _receivers = [];
    private readonly object _gate = new();

    public IDisposable Subscribe(Action<LogLevel, string, string> receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        lock (_gate) _receivers = [.. _receivers, receiver];
        return new Subscription(this, receiver);
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { lock (_gate) _receivers = []; }

    private void Remove(Action<LogLevel, string, string> receiver)
    {
        lock (_gate) _receivers = _receivers.Where(item => item != receiver).ToArray();
    }

    private sealed class Subscription(ServerLogFeed feed, Action<LogLevel, string, string> receiver) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) feed.Remove(receiver); }
    }

    private sealed class Logger(ServerLogFeed feed, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Without receivers nothing is formatted, so the feed costs nothing until an administration view listens.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && Volatile.Read(ref feed._receivers).Length != 0;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var receivers = Volatile.Read(ref feed._receivers);
            if (logLevel == LogLevel.None || receivers.Length == 0) return;
            var message = formatter(state, exception);
            if (exception is not null)
                message = string.Create(CultureInfo.InvariantCulture, $"{message} ({exception.GetType().Name}: {exception.Message})");
            foreach (var receiver in receivers) receiver(logLevel, category, message);
        }
    }
}
