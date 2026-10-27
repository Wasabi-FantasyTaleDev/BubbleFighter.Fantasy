namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>
/// Whether the host has finished starting every configured row. Listeners accept connections as soon as their own
/// row is active, so a feature that admits new players (for example a launcher sign-in) can refuse them until the
/// rest of the server is running.
/// </summary>
public interface IHostReadiness
{
    /// <summary>True once the host's startup configuration converged with every enabled row active; it stays true.</summary>
    bool IsReady { get; }
}
