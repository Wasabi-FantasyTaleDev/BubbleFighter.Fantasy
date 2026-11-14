using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Serializes bounded work with authentication and scene publication on a current match lease.</summary>
public interface IMatchConnectionOperations
{
    /// <summary>Gets the relay protocol whose lifecycle owns these operations.</summary>
    string Protocol { get; }

    /// <summary>Runs one operation after authentication while the exact connection's lease remains current.</summary>
    /// <param name="session">The authenticated relay session, checked by object identity.</param>
    /// <param name="operation">Bounded work that observes cancellation and does not reenter this service.</param>
    /// <param name="ct">Cancellation linked with the owning connection's lifetime.</param>
    /// <remarks>
    /// Scene completion is not required. The operation shares the handshake gate, so it cannot interrupt
    /// an authentication or scene acknowledgement batch. Failure revokes and closes the owning transport.
    /// The callback and lease ownership remain in the native process; no remote callback adapter is provided.
    /// </remarks>
    ValueTask RunAuthenticatedAsync(ISession session, Func<CancellationToken, ValueTask> operation, CancellationToken ct);
}
