namespace BubbleFighter.Fantasy.Contracts.Cluster;

/// <summary>Shares the heartbeat row's current authenticated lease with migration plugins on the same node.</summary>
public interface INodeLeaseSource
{
    /// <summary>Gets the configured node identity to which the lease belongs.</summary>
    string NodeId { get; }
    /// <summary>Gets the configured directory origin that issued the lease.</summary>
    string DirectoryUrl { get; }
    /// <summary>Gets the last successfully reported lease, or null when the heartbeat is unavailable.</summary>
    NodeLeaseCredentials? Current { get; }
    /// <summary>
    /// Gets a task that is complete once <see cref="Current"/> no longer equals <paramref name="seen"/>, so a worker
    /// that has dealt with one lease can sleep until the next instead of reading <see cref="Current"/> on a timer.
    /// The comparison and the task are taken together: a lease that changes before the caller waits is not missed.
    /// A source that cannot tell returns a completed task, and its caller keeps checking at its own period.
    /// </summary>
    /// <param name="seen">The lease the caller last read from <see cref="Current"/>, or null when it read none.</param>
    /// <returns>A task that never faults; add cancellation with <see cref="Task.WaitAsync(CancellationToken)"/>.</returns>
    Task WhenChangedFrom(NodeLeaseCredentials? seen) => Task.CompletedTask;
}

/// <summary>Identifies one authenticated directory lease; its token must never appear in diagnostics.</summary>
/// <param name="Token">The private bearer token issued to the registered node instance.</param>
/// <param name="Epoch">The generation distinguishing this node instance from a restarted one.</param>
public sealed record NodeLeaseCredentials(string Token, long Epoch)
{
    /// <summary>Describes the lease generation without exposing its bearer token.</summary>
    public override string ToString() => $"Node lease epoch {Epoch}";
}

/// <summary>Retains abandoned arrivals within their capacity until the destination releases any reserved seat.</summary>
public interface IMigrationArrivalCleanup
{
    /// <summary>Prevents further consumption of a pending arrival and returns it for idempotent seat cancellation.</summary>
    MigrationArrival? Retire(string migrationId);
    /// <summary>Returns retained expired or cancelled arrivals; drain additionally retires every pending arrival for unload.</summary>
    IReadOnlyList<MigrationArrival> Collect(bool drain = false);
    /// <summary>Releases a retired record's capacity after successful seat cleanup; requires the same arrival instance returned by this service.</summary>
    void Complete(MigrationArrival arrival);
    /// <summary>
    /// Gets a task that is complete while any arrival, pending or retired, is held and pending while there is none,
    /// so the clean-up can sleep instead of collecting from an empty store. A store that cannot tell returns a
    /// completed task, and its clean-up keeps collecting at its own period.
    /// </summary>
    /// <returns>A level-triggered task that never faults.</returns>
    Task WhenAnyTracked() => Task.CompletedTask;
}
