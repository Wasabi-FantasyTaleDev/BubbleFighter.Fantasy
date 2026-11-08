using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Cluster;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>Provides bounded, connection-bound account holds in an explicitly pinned MySQL authority.</summary>
[Plugin("persistence.mysql-account-ownership", Description = "Connection-bound account occupancy in a pinned MySQL data domain.",
    Provides = [typeof(IAccountOwnershipStore), typeof(IAccountMutationFence), typeof(IAccountMutationScopes)],
    ConditionalProvides = [typeof(IAccountHandoffStore)])]
public sealed class MySqlAccountOwnershipPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MySqlAccountOwnershipOptions>();
        var builder = options.CreateBuilder();
        // A pooled connection can keep a named lock after a failed reset. Each owner gets a dedicated socket.
        builder.Pooling = false;
        var factory = new MySqlConnectionFactory(builder.ConnectionString);
        MySqlConnectionFactory.WarnIfUnencryptedRemote(builder, context.Logger);
        var actual = await new MySqlPersistentDataDomainIdentity(factory).ReadAsync(cancellationToken).ConfigureAwait(false);
        if (!actual.Equals(options.PersistentDomainId, StringComparison.OrdinalIgnoreCase))
            throw new ConfigException("The account-ownership database does not match persistent_domain_id.");
        MySqlAccountOwnershipStore? acquired = null;
        await context.AcquireEffectAsync(_ => ValueTask.FromResult<IAsyncDisposable>(
            acquired = new MySqlAccountOwnershipStore(factory, options.PersistentDomainId, options.MaxOwners, options.CommandTimeoutSeconds, options.NodeId)), cancellationToken).ConfigureAwait(false);
        var store = acquired ?? throw new InvalidOperationException("Account-ownership acquisition returned no resource.");
        var mutations = new MySqlAccountMutationScopes(store.Read);
        context.Effect(context.Services.Register<IAccountOwnershipStore>(store));
        if (options.NodeId.Length != 0) context.Effect(context.Services.Register<IAccountHandoffStore>(store));
        context.Effect(context.Services.Register<IAccountMutationScopes>(mutations));
        context.Effect(context.Services.Register<IAccountMutationFence>(new MySqlAccountWriteFence(mutations.Read)));
    }
}

internal sealed class MySqlAccountOwnershipStore(IDbConnectionFactory connections, string domainId, int maxOwners, int commandTimeoutSeconds,
    string nodeId = "")
    : IAccountOwnershipStore, IAccountHandoffStore, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly HashSet<Ownership> _owners = [];
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _reserved;
    private bool _disposed;

    internal AccountWriteProof? Read(IAccountOwnership ownership)
    {
        lock (_gate)
            return !_disposed && ownership is Ownership owner && _owners.Contains(owner) && owner.Valid ? owner.Proof : null;
    }

    internal AccountWriteProof? Read(long userNum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        lock (_gate)
            return _disposed ? null : _owners.FirstOrDefault(owner => owner.Proof.UserNum == userNum && owner.Valid)?.Proof;
    }

    internal static string LockName(string domainId, long userNum) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes("bff-account:" + domainId.ToLowerInvariant() + ":" + userNum.ToString(CultureInfo.InvariantCulture))));

    public ValueTask<IAccountOwnership?> TryAcquireAsync(long userNum, CancellationToken cancellationToken) =>
        AcquireAsync(userNum, HandoffMode.Ordinary, null, cancellationToken);

    public ValueTask<IAccountOwnership?> TryClaimAsync(long userNum, string migrationId, CancellationToken cancellationToken) =>
        AccountHandoff.IsMigrationId(migrationId) ? AcquireAsync(userNum, HandoffMode.Claim, migrationId, cancellationToken)
            : throw new ArgumentException("The migration identity is malformed.", nameof(migrationId));

    public ValueTask<IAccountOwnership?> TryWithdrawAsync(long userNum, string migrationId, CancellationToken cancellationToken) =>
        AccountHandoff.IsMigrationId(migrationId) ? AcquireAsync(userNum, HandoffMode.Withdraw, migrationId, cancellationToken)
            : throw new ArgumentException("The migration identity is malformed.", nameof(migrationId));

    public async ValueTask HandOffAsync(IAccountOwnership ownership, AccountHandoff handoff, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(handoff);
        if (!handoff.IsValid()) throw new ArgumentException("The account handoff is malformed.", nameof(handoff));
        if (nodeId.Length == 0 || handoff.SourceNode != nodeId)
            throw new InvalidOperationException("Account handoff requires the configured source node identity.");
        if (ownership is not Ownership owner || !ReferenceEquals(owner.Store, this))
            throw new InvalidOperationException("The account hold does not belong to this authority provider.");
        if (owner.Proof.UserNum != handoff.UserNum) throw new InvalidOperationException("The handoff names a different account.");
        try { await owner.RecordHandoffAsync(handoff, cancellationToken).ConfigureAwait(false); }
        finally { await owner.DisposeAsync().ConfigureAwait(false); }
    }

    private async ValueTask<IAccountOwnership?> AcquireAsync(long userNum, HandoffMode mode, string? migrationId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        cancellationToken.ThrowIfCancellationRequested();
        if (mode != HandoffMode.Ordinary && nodeId.Length == 0)
            throw new InvalidOperationException("Account handoff requires a configured node identity.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_reserved >= maxOwners) throw new InvalidOperationException("The configured account-ownership capacity is exhausted.");
            ++_reserved;
        }
        DbConnection? connection = null;
        Ownership? owner = null;
        try
        {
            connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var domain = connection.CreateCommand())
            {
                domain.CommandTimeout = commandTimeoutSeconds;
                domain.CommandText = "SELECT REPLACE(domain_id, '-', '') FROM cluster_data_domain WHERE singleton_id = 1";
                var actual = await domain.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (actual is not string text || !text.Equals(domainId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The account-ownership database no longer matches its pinned persistent domain.");
            }

            var name = LockName(domainId, userNum);
            var result = await ExecuteAsync(connection, "SELECT GET_LOCK(@name, 0)", name, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
            if (result is null or DBNull) throw new InvalidOperationException("The authority could not evaluate the account hold.");
            if (Convert.ToInt32(result, CultureInfo.InvariantCulture) == 0) return null;
            if (Convert.ToInt32(result, CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("The authority returned an invalid account-hold result.");
            var proof = new AccountWriteProof(userNum, domainId, Guid.NewGuid().ToString("N"));
            // Ownership changes queue behind every old mutation holding this row until its transaction ends.
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                // The account lock serializes every acquisition, so the handoff row cannot change between check and claim.
                if (!await SettleHandoffAsync(connection, transaction, userNum, mode, migrationId, cancellationToken).ConfigureAwait(false))
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return null;
                }
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandTimeout = commandTimeoutSeconds;
                command.CommandText = "INSERT INTO account_write_authorities (user_num, token, lock_name, owner_connection_id) "
                    + "VALUES (@user, @token, @name, CONNECTION_ID()) ON DUPLICATE KEY UPDATE "
                    + "token = VALUES(token), lock_name = VALUES(lock_name), owner_connection_id = VALUES(owner_connection_id)";
                foreach (var (key, value) in new (string Key, object Value)[] { ("@user", userNum), ("@token", proof.Token), ("@name", name) })
                {
                    var parameter = command.CreateParameter(); parameter.ParameterName = key; parameter.Value = value;
                    command.Parameters.Add(parameter);
                }
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            owner = new Ownership(this, connection, name, domainId, commandTimeoutSeconds, proof);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _owners.Add(owner);
                connection = null;
            }
            return owner;
        }
        finally
        {
            if (connection is not null)
            {
                try { await connection.DisposeAsync().ConfigureAwait(false); }
                finally { ReleaseReservation(); }
            }
            else if (owner is null)
                ReleaseReservation();
        }
    }

    private enum HandoffMode { Ordinary, Claim, Withdraw }

    // Ordinary admission refuses a pending, unexpired handoff and retires an expired one; claim and withdrawal settle
    // only the named pending migration. Deadlines are compared with the database clock.
    private async ValueTask<bool> SettleHandoffAsync(DbConnection connection, DbTransaction transaction, long userNum,
        HandoffMode mode, string? migrationId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = mode switch
        {
            HandoffMode.Ordinary => "UPDATE account_handoffs SET state = 'expired', settled_at = NOW(3) "
                + "WHERE user_num = @user AND state = 'pending' AND deadline_at <= NOW(3); "
                + "SELECT COUNT(*) FROM account_handoffs WHERE user_num = @user AND state = 'pending'",
            HandoffMode.Claim => "UPDATE account_handoffs SET state = 'claimed', settled_at = NOW(3) "
                + "WHERE user_num = @user AND migration_id = @migration AND destination_node = @node AND state = 'pending' AND deadline_at > NOW(3); "
                + "SELECT ROW_COUNT()",
            _ => "UPDATE account_handoffs SET state = 'withdrawn', settled_at = NOW(3) "
                + "WHERE user_num = @user AND migration_id = @migration AND source_node = @node AND state = 'pending'; SELECT ROW_COUNT()",
        };
        Add(command, "@user", userNum);
        if (migrationId is not null)
        {
            Add(command, "@migration", migrationId);
            Add(command, "@node", nodeId);
        }
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return mode == HandoffMode.Ordinary ? count == 0 : count == 1;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    internal static async ValueTask<object?> ExecuteAsync(DbConnection connection, string sql, string name, int timeout, CancellationToken ct, string? domainId = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeout;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = name;
        command.Parameters.Add(parameter);
        if (domainId is not null)
        {
            var domain = command.CreateParameter();
            domain.ParameterName = "@domain";
            domain.Value = domainId.ToLowerInvariant();
            command.Parameters.Add(domain);
        }
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Ownership[] owners;
        lock (_gate)
        {
            _disposed = true;
            owners = [.. _owners];
            if (_reserved == 0) _drained.TrySetResult();
        }
        List<Exception>? failures = null;
        foreach (var owner in owners)
        {
            try { await owner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        await _drained.Task.ConfigureAwait(false);
        if (failures is not null) throw new AggregateException("Account holds could not all close cleanly.", failures);
    }

    private void ReleaseReservation()
    {
        lock (_gate)
        {
            --_reserved;
            if (_disposed && _reserved == 0) _drained.TrySetResult();
        }
    }

    private sealed class Ownership(MySqlAccountOwnershipStore store, DbConnection connection, string name, string domainId, int timeout,
        AccountWriteProof proof) : IAccountOwnership
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private volatile bool _disposed;
        private volatile bool _lost;
        internal AccountWriteProof Proof { get; } = proof;
        internal MySqlAccountOwnershipStore Store => store;
        internal bool Valid => !_disposed && !_lost;

        // Written on the lock's own connection and only while it still holds the lock, so no other node can acquire
        // the account between this row and the release that follows.
        internal async ValueTask RecordHandoffAsync(AccountHandoff handoff, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed || _lost) throw new InvalidOperationException("The account hold is no longer valid.");
                await using var command = connection.CreateCommand();
                command.CommandTimeout = timeout;
                command.CommandText = "INSERT INTO account_handoffs "
                    + "(user_num, migration_id, source_node, destination_node, state, created_at, deadline_at, settled_at) "
                    + "SELECT @user, @migration, @source, @destination, 'pending', NOW(3), NOW(3) + INTERVAL @micros MICROSECOND, NULL "
                    + "FROM DUAL WHERE IS_USED_LOCK(@name) = CONNECTION_ID() "
                    + "ON DUPLICATE KEY UPDATE migration_id = VALUES(migration_id), source_node = VALUES(source_node), "
                    + "destination_node = VALUES(destination_node), state = 'pending', created_at = VALUES(created_at), "
                    + "deadline_at = VALUES(deadline_at), settled_at = NULL";
                Add(command, "@user", handoff.UserNum);
                Add(command, "@migration", handoff.MigrationId);
                Add(command, "@source", handoff.SourceNode);
                Add(command, "@destination", handoff.DestinationNode);
                Add(command, "@micros", checked((long)(handoff.Lifetime.TotalMilliseconds * 1000)));
                Add(command, "@name", name);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
                {
                    _lost = true;
                    throw new InvalidOperationException("The account hold was lost before its handoff was recorded.");
                }
            }
            catch { _lost = true; throw; }
            finally { _gate.Release(); }
        }

        public async ValueTask<bool> IsHeldAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed || _lost) return false;
                var result = await ExecuteAsync(connection,
                    "SELECT IS_USED_LOCK(@name) = CONNECTION_ID() AND EXISTS (SELECT 1 FROM cluster_data_domain WHERE singleton_id = 1 AND LOWER(REPLACE(domain_id, '-', '')) = @domain)",
                    name, timeout, cancellationToken, domainId).ConfigureAwait(false);
                var held = result is not null and not DBNull && Convert.ToInt32(result, CultureInfo.InvariantCulture) == 1;
                if (!held) _lost = true;
                return held;
            }
            catch { _lost = true; throw; }
            finally { _gate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                _disposed = true;
                // Closing the unpooled socket releases only this connection's locks, including after cancellation.
                try { await connection.DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    lock (store._gate)
                        store._owners.Remove(this);
                    store.ReleaseReservation();
                }
            }
            finally { _gate.Release(); }
        }
    }
}
