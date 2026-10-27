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
