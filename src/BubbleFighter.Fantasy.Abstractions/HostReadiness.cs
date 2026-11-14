namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>
/// Whether the host has finished starting every configured row. Listeners accept connections as soon as their own
/// row is active, so a feature that admits new players (for example a launcher sign-in) can refuse them until the
/// rest of the server is running.
/// </summary>
public interface IHostReadiness : IServerMaintenance
{
    /// <summary>True after startup convergence and while maintenance mode is off.</summary>
    bool IsReady { get; }
}

/// <summary>
/// Raised once when every row of the startup configuration is active, before the host compacts its heap and announces
/// readiness. A service that keeps data only to make the start cheaper (a parsed file several rows read) drops it
/// here, so the data is not part of what the server keeps while it runs.
/// </summary>
public sealed record StartupConvergedEvent : IEvent;
