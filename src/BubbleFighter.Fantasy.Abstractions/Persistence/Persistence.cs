using System.Data.Common;

namespace BubbleFighter.Fantasy.Abstractions.Persistence;

/// <summary>Opens database connections for the server's configured store.</summary>
public interface IDbConnectionFactory
{
    /// <summary>
    /// Opens a new, already-open connection that the caller owns and must dispose when its unit of
    /// work ends; every call returns an independent connection. Provider failures (unreachable
    /// server, rejected credentials) propagate to the caller.
    /// </summary>
    /// <param name="ct">Cancels the open attempt.</param>
    /// <returns>An open connection the caller owns until it disposes it.</returns>
    ValueTask<DbConnection> OpenAsync(CancellationToken ct);
}

/// <summary>Applies pending schema migrations in order.</summary>
public interface IMigrationRunner
{
    /// <summary>
    /// Applies every migration the store has not recorded yet. Each migration runs in its own
    /// transaction together with its bookkeeping row, so a failed run leaves earlier migrations
    /// applied and a rerun applies only the remainder. Runners do not coordinate with each other;
    /// one host migrates a database at a time.
    /// </summary>
    /// <param name="ct">Cancels the current migration; already committed migrations stay applied.</param>
    ValueTask ApplyAsync(CancellationToken ct);
}
