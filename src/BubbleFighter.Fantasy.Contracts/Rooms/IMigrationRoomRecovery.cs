using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>Recovers a promoted connection when its reserved room becomes unavailable before entry.</summary>
public interface IMigrationRoomRecovery
{
    /// <summary>Gets the client build whose scene encoders this recovery service owns.</summary>
    string ClientBuild { get; }

    /// <summary>Installs the retained main menu only when the promoted player has no room or plaza membership.</summary>
    ValueTask<bool> InstallMenuAsync(ISession session, CancellationToken cancellationToken);

    /// <summary>Applies the configured return policy after dispatch completes, cancelling if the recovered scene changes.</summary>
    ValueTask ReturnHomeAsync(ISession session, long generation, CancellationToken cancellationToken);
}
