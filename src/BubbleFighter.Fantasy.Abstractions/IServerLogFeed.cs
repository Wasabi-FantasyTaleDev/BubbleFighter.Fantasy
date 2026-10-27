using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>
/// Delivers every log entry the server process writes, from every row, so an administration view can show the whole
/// server instead of only its own requests. The host keeps no history; each receiver bounds what it retains.
/// </summary>
public interface IServerLogFeed
{
    /// <summary>Adds a receiver of entries written from now on; disposing the result removes it.</summary>
    /// <param name="receiver">Called with the level, category and formatted message on the writing thread. It must be quick and must not log.</param>
    IDisposable Subscribe(Action<LogLevel, string, string> receiver);
}
