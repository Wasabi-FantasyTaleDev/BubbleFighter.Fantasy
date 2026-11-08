namespace BubbleFighter.Fantasy.Contracts.Cluster;

/// <summary>Reads the authenticated directory's current room advertisements for this node without granting admission or sharing player data.</summary>
public interface IFederatedRoomDiscovery
{
    /// <summary>
    /// Fetches a fresh, bounded room list for one configured build. Directory membership controls visibility;
    /// a returned room or its join hint is never an authentication credential or a reserved seat.
    /// Transport, authentication and response validation failures are reported to the caller, without serving an older list.
    /// </summary>
    ValueTask<IReadOnlyList<FederatedRoom>> ListAsync(string clientBuild, CancellationToken cancellationToken);
}
