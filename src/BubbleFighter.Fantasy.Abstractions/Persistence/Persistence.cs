using System.Data.Common;

namespace BubbleFighter.Fantasy.Abstractions.Persistence;

/// <summary>Opens database connections for the server's configured store.</summary>
public interface IDbConnectionFactory
{
    /// <summary>Opens a new connection.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An open connection.</returns>
    ValueTask<DbConnection> OpenAsync(CancellationToken ct);
}

/// <summary>Applies pending schema migrations in order.</summary>
public interface IMigrationRunner
{
    /// <summary>Applies every pending migration.</summary>
    /// <param name="ct">Cancellation token.</param>
    ValueTask ApplyAsync(CancellationToken ct);
}
