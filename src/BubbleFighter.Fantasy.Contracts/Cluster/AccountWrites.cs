using System.Data.Common;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Cluster;

/// <summary>Fences mutations against the original local account owner for the lifetime of a caller-owned database transaction.</summary>
public interface IAccountMutationFence
{
    /// <summary>
    /// Locks and verifies the account's mutation authority on this exact connection and transaction before any state changes.
    /// Ownership handover waits for the transaction to finish. Missing, lost or changed authority throws without reacquiring it.
    /// </summary>
    ValueTask VerifyAsync(DbConnection connection, DbTransaction transaction, long userNum, CancellationToken cancellationToken);
}

/// <summary>Binds immutable account authority to a game session and selects that exact session for asynchronous mutation work.</summary>
public interface IAccountMutationScopes
{
    /// <summary>
    /// Associates an ownership handle from this provider with its original session. The admission owner removes the
    /// returned registration before releasing the hold; later admission on another session cannot replace this association.
    /// </summary>
    IDisposable Bind(ISession session, IAccountOwnership ownership);

    /// <summary>
    /// Selects the originating session in the current asynchronous flow until disposal. Nested work for another player
    /// must enter that player's original session explicitly. Missing, closed and retired bindings cannot authorize writes.
    /// </summary>
    IDisposable Enter(ISession session);
}
