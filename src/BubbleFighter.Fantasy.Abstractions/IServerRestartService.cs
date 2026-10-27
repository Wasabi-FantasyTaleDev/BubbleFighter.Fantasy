namespace BubbleFighter.Fantasy.Abstractions;

/// <summary>Requests an orderly restart of the owning server process.</summary>
public interface IServerRestartService
{
    /// <summary>Schedules process restart after the current administration response completes.</summary>
    void RequestRestart();
}
