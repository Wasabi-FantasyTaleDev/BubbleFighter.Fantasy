namespace BubbleFighter.Fantasy.Contracts.Cluster;

/// <summary>Arbitrates account occupancy against one authoritative persistent data domain.</summary>
public interface IAccountOwnershipStore
{
    /// <summary>
    /// Attempts to hold the account without waiting for its current owner. Returns null only when another
    /// owner holds it; capacity, connectivity and data-domain errors propagate to the caller.
    /// The caller must retain the handle for the entire authenticated session and release it after draining its work.
    /// </summary>
    ValueTask<IAccountOwnership?> TryAcquireAsync(long userNum, CancellationToken cancellationToken);
}

/// <summary>
/// A connection-bound account hold. This arbitrates occupancy, not individual repository writes;
/// consumers must stop the authenticated session when verification fails and fence in-flight mutations separately.
/// </summary>
public interface IAccountOwnership : IAsyncDisposable
{
    /// <summary>Checks that the original authority connection still holds the account; it never reacquires a lost hold.</summary>
    ValueTask<bool> IsHeldAsync(CancellationToken cancellationToken);
}
