using System.Data.Common;
using BubbleFighter.Fantasy.Contracts.Cluster;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>Serializes ownership handover with a player's transaction before checking the original live hold.</summary>
internal sealed class MySqlAccountWriteFence(Func<long, AccountWriteProof?> read) : IAccountMutationFence
{
    public async ValueTask VerifyAsync(DbConnection connection, DbTransaction transaction, long userNum, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (!ReferenceEquals(transaction.Connection, connection)) throw new InvalidOperationException("The mutation transaction belongs to another connection.");
        var proof = read(userNum);
        if (proof is null || proof.UserNum != userNum)
            throw new InvalidOperationException("This node does not own the account required for this mutation.");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT token = @token AND IS_USED_LOCK(lock_name) = owner_connection_id "
            + "AND EXISTS (SELECT 1 FROM cluster_data_domain WHERE singleton_id = 1 AND LOWER(REPLACE(domain_id, '-', '')) = @domain) "
            + "FROM account_write_authorities WHERE user_num = @user FOR UPDATE";
        foreach (var (name, value) in new (string Name, object Value)[]
        {
            ("@token", proof.Token), ("@domain", proof.PersistentDomainId.ToLowerInvariant()), ("@user", userNum),
        })
        {
            var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        var valid = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (valid is null or DBNull || Convert.ToInt32(valid, System.Globalization.CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("The account's original write authority is no longer valid.");
    }
}

internal sealed record AccountWriteProof(long UserNum, string PersistentDomainId, string Token);
