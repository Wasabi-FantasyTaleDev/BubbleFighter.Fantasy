using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Cluster;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>Reads the identity initialized in the authoritative MySQL database.</summary>
internal sealed class MySqlPersistentDataDomainIdentity(IDbConnectionFactory connections) : IPersistentDataDomainIdentity
{
    /// <inheritdoc />
    public async ValueTask<string> ReadAsync(CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT domain_id FROM cluster_data_domain WHERE singleton_id = 1";
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        // The connector can materialize CHAR(36) as Guid or string, depending on GuidFormat.
        var id = value is Guid typed ? typed : value is string text && Guid.TryParseExact(text, "D", out var parsed) ? parsed : Guid.Empty;
        if (id == Guid.Empty)
            throw new InvalidOperationException("The persistent database has no valid cluster data-domain identity.");
        return id.ToString("N");
    }
}
