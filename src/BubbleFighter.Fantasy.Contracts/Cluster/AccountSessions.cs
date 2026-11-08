using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Cluster;

/// <summary>Owns authoritative account holds for authenticated game connections on one node.</summary>
public interface IAccountSessionAdmission
{
    /// <summary>
    /// Acquires the account before local player state is installed, retaining one hold for the connection.
    /// Returns false for an occupied account or a closed connection; authority and capacity errors propagate.
    /// Repeated admission of the same account on the same connection rechecks the retained hold.
    /// </summary>
    ValueTask<bool> TryAdmitAsync(ISession session, long userNum, CancellationToken cancellationToken);

    /// <summary>Drains pending admission and releases the connection's hold after a refused or ended login.</summary>
    ValueTask ReleaseAsync(ISession session);
}

/// <summary>A session feature exposing the account's retained authority without transferring its lifetime.</summary>
public interface IAccountSession
{
    /// <summary>Gets the account admitted on the owning game connection.</summary>
    long UserNum { get; }

    /// <summary>Rechecks the original hold; a closed or lost session cannot regain authority.</summary>
    ValueTask<bool> IsHeldAsync(CancellationToken cancellationToken);
}
