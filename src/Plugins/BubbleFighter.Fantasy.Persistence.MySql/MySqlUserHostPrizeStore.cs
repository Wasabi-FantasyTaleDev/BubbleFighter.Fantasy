using BubbleFighter.Fantasy.Abstractions.Persistence;
using BubbleFighter.Fantasy.Contracts.Cluster;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Wallet;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>
/// Durable MySQL implementation of <see cref="IUserHostPrizeStore"/> backed by <c>user_host_escrows</c>
/// and <c>user_host_claims</c> (migration 0063). Lock order everywhere is ascending <c>players</c> ->
/// <c>user_host_escrows</c> -> <c>player_wallets</c> -> <c>inventory_slots</c> / <c>user_host_claims</c>.
/// </summary>
public sealed class MySqlUserHostPrizeStore(
    IDbConnectionFactory connections,
    IAccountMutationFence? authority = null) : IUserHostPrizeStore
{
    /// <inheritdoc />
    public async ValueTask<UserHostEscrowBeginResult> BeginEscrowAsync(UserHostEscrowBeginCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginWriteAsync(command.HostUserNum, authority, ct).ConfigureAwait(false);

        var playerLocked = await MySqlPlayerLock.LockQuery(db, command.HostUserNum).ToListAsync(ct).ConfigureAwait(false);
        if (playerLocked.Count == 0)
            return new(UserHostEscrowBeginOutcome.HostMissing, null, 0);

        var existing = await EscrowQuery(db, command.ProfileId, command.MatchId)
            .TranLock(DbLockType.Wait).ToListAsync(ct).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            var walletSnapshot = await WalletQuery(db, command.HostUserNum).ToListAsync(ct).ConfigureAwait(false);
            return new(UserHostEscrowBeginOutcome.AlreadyCommitted, null, walletSnapshot.FirstOrDefault()?.Lucci ?? 0);
        }

        var wallets = await WalletQuery(db, command.HostUserNum)
            .TranLock(DbLockType.Wait).ToListAsync(ct).ConfigureAwait(false);
        if (wallets.Count == 0)
            return new(UserHostEscrowBeginOutcome.HostMissing, null, 0);

        var wallet = wallets[0];
        if (wallet.Lucci < command.PrizeLucci)
            return new(UserHostEscrowBeginOutcome.InsufficientLucci, null, wallet.Lucci);

        var ticketRows = await TicketQuery(db, command.HostUserNum, command.TicketItemId)
            .TranLock(DbLockType.Wait).ToListAsync(ct).ConfigureAwait(false);
        var item = ticketRows.SingleOrDefault();
        if (item is null || item.Quantity <= 0 || item.LifetimeKind == (int)InventoryLifetimeKind.Life
            || (item.ExpiresAtUnixMilliseconds is { } end && end <= command.CreatedAtUnixMilliseconds))
        {
            return new(UserHostEscrowBeginOutcome.MissingTicket, null, wallet.Lucci);
        }

        var remaining = item.Quantity - 1;
        if (remaining == 0)
        {
            await db.Deleteable<InventorySlotRow>()
                .Where(row => row.UserNum == command.HostUserNum && row.SlotKey == item.SlotKey && (row.Bag == 0 || row.Bag == 1))
                .ExecuteCommandAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await db.Updateable<InventorySlotRow>()
                .SetColumns(row => row.Quantity == remaining)
                .SetColumns(row => row.UpdatedAt == SqlFunc.MappingColumn(default(DateTime), "UTC_TIMESTAMP()"))
                .Where(row => row.UserNum == command.HostUserNum && row.SlotKey == item.SlotKey && (row.Bag == 0 || row.Bag == 1))
                .ExecuteCommandAsync(ct).ConfigureAwait(false);
        }

        var lucciAfter = checked(wallet.Lucci - command.PrizeLucci);
        if (command.PrizeLucci > 0)
        {
            var walletUpdated = await db.Updateable<WalletRow>()
                .SetColumns(w => w.Lucci == lucciAfter)
                .SetColumns(w => w.UpdatedAt == SqlFunc.MappingColumn(default(DateTime), "UTC_TIMESTAMP()"))
                .Where(w => w.UserNum == command.HostUserNum && w.Lucci == wallet.Lucci)
                .ExecuteCommandAsync(ct).ConfigureAwait(false);
            if (walletUpdated != 1)
                throw new InvalidOperationException($"Sponsor escrow wallet debit affected {walletUpdated} row(s) for user {command.HostUserNum}.");
        }

        await db.Insertable(new UserHostEscrowRow
        {
            Profile = command.ProfileId,
            MatchId = command.MatchId,
            NodeId = command.NodeId,
            RoomId = command.RoomId,
            HostUserNum = command.HostUserNum,
            PrizeLucci = command.PrizeLucci,
            FeePercent = command.FeePercent,
            State = (int)UserHostEscrowState.Active,
            CreatedAtMs = command.CreatedAtUnixMilliseconds,
        }).ExecuteCommandAsync(ct).ConfigureAwait(false);

        await tx.CommitAsync().ConfigureAwait(false);
        return new(UserHostEscrowBeginOutcome.Committed, remaining, lucciAfter);
    }

    /// <inheritdoc />
    public async ValueTask<UserHostSettleResult> SettleEscrowAsync(UserHostSettleCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;

        var preflight = await EscrowQuery(db, command.ProfileId, command.MatchId).ToListAsync(ct).ConfigureAwait(false);
        if (preflight.Count == 0)
            return new(UserHostSettlementOutcome.NotFound, []);
        if (preflight[0].State != (int)UserHostEscrowState.Active)
            return new(UserHostSettlementOutcome.AlreadyFinalized, []);

        var onlineSet = command.OnlineUserNums.ToHashSet();
        var affectedRecipients = command.IsDrawOrCancelled
            ? new[] { preflight[0].HostUserNum }
            : command.WinnerPayouts.Where(w => w.AmountLucci > 0).Select(w => w.UserNum).Distinct().ToArray();
        var onlineMutations = affectedRecipients.Where(onlineSet.Contains).ToArray();
        var lockOrder = affectedRecipients.Append(preflight[0].HostUserNum).Distinct().OrderBy(id => id).ToArray();

        await using var tx = await session.BeginWriteManyAsync(onlineMutations, authority, ct).ConfigureAwait(false);

        foreach (var userNum in lockOrder)
            await MySqlPlayerLock.LockQuery(db, userNum).ToListAsync(ct).ConfigureAwait(false);

        var escrows = await EscrowQuery(db, command.ProfileId, command.MatchId)
            .TranLock(DbLockType.Wait).ToListAsync(ct).ConfigureAwait(false);
        if (escrows.Count == 0)
            return new(UserHostSettlementOutcome.NotFound, []);
        var escrow = escrows[0];
        if (escrow.State != (int)UserHostEscrowState.Active)
            return new(UserHostSettlementOutcome.AlreadyFinalized, []);

        var targetState = command.IsDrawOrCancelled
            ? UserHostEscrowState.Refunded
            : UserHostEscrowState.Settled;
        var updated = await EscrowFinalizeUpdate(db, command.ProfileId, command.MatchId, targetState, command.SettledAtUnixMilliseconds)
            .ExecuteCommandAsync(ct).ConfigureAwait(false);
        if (updated != 1)
            throw new InvalidOperationException($"Sponsor escrow finalize affected {updated} row(s) for '{command.MatchId}'.");

        var payouts = new List<UserHostPayoutEntry>();
        if (command.IsDrawOrCancelled)
        {
            if (escrow.PrizeLucci > 0)
            {
                var entry = await CreditOrQueueMySqlAsync(db, command.ProfileId, escrow.HostUserNum, command.MatchId,
                    escrow.PrizeLucci, "refund", onlineSet.Contains(escrow.HostUserNum), command.SettledAtUnixMilliseconds, ct).ConfigureAwait(false);
                payouts.Add(entry);
            }
        }
        else
        {
            foreach (var allocation in command.WinnerPayouts)
            {
                if (allocation.AmountLucci <= 0)
                    continue;
                var entry = await CreditOrQueueMySqlAsync(db, command.ProfileId, allocation.UserNum, command.MatchId,
                    allocation.AmountLucci, "win", onlineSet.Contains(allocation.UserNum), command.SettledAtUnixMilliseconds, ct).ConfigureAwait(false);
                payouts.Add(entry);
            }
        }

        await tx.CommitAsync().ConfigureAwait(false);
        return new(UserHostSettlementOutcome.Applied, payouts);
    }

    private static async ValueTask<UserHostPayoutEntry> CreditOrQueueMySqlAsync(
        SqlSugarClient db,
        string profileId,
        long userNum,
        string matchId,
        int amountLucci,
        string reason,
        bool isOnline,
        long timestampMs,
        CancellationToken ct)
    {
        if (isOnline)
        {
            var wallets = await WalletQuery(db, userNum).TranLock(DbLockType.Wait).ToListAsync(ct).ConfigureAwait(false);
            if (wallets.Count == 1)
            {
                var wallet = wallets[0];
                var nextLucci = checked(wallet.Lucci + amountLucci);
                var changed = await db.Updateable<WalletRow>()
                    .SetColumns(w => w.Lucci == nextLucci)
                    .SetColumns(w => w.UpdatedAt == SqlFunc.MappingColumn(default(DateTime), "UTC_TIMESTAMP()"))
                    .Where(w => w.UserNum == userNum && w.Lucci == wallet.Lucci)
                    .ExecuteCommandAsync(ct).ConfigureAwait(false);
                if (changed != 1)
                    throw new InvalidOperationException($"Sponsor payout wallet update affected {changed} row(s) for user {userNum}.");
                return new(userNum, amountLucci, CreditedImmediately: true, nextLucci);
            }
        }

        await db.Insertable(new UserHostClaimRow
        {
            Profile = profileId,
            UserNum = userNum,
            MatchId = matchId,
            AmountLucci = amountLucci,
            Reason = reason,
            State = (int)UserHostClaimState.Pending,
            CreatedAtMs = timestampMs,
        }).ExecuteCommandAsync(ct).ConfigureAwait(false);
        return new(userNum, amountLucci, CreditedImmediately: false, null);
    }

    /// <inheritdoc />
    public async ValueTask<UserHostRecoverResult> RecoverActiveEscrowsAsync(
        string profileId,
        string nodeId,
        long recoveredAtUnixMilliseconds,
        int maxBatch,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (profileId.Length > UserHostLimits.MaxProfileIdLength)
            throw new ArgumentOutOfRangeException(nameof(profileId), "Profile identifier exceeds maximum length.");
        ArgumentNullException.ThrowIfNull(nodeId);
        if (nodeId.Length > UserHostLimits.MaxNodeIdLength)
            throw new ArgumentOutOfRangeException(nameof(nodeId), "Node identifier exceeds maximum length.");
        ArgumentOutOfRangeException.ThrowIfNegative(recoveredAtUnixMilliseconds);
        if (maxBatch is < 1 or > UserHostLimits.MaxStaleEscrowsPerStartup)
            throw new ArgumentOutOfRangeException(nameof(maxBatch));
        ct.ThrowIfCancellationRequested();

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginAsync().ConfigureAwait(false);

        var rows = await ActiveEscrowsQuery(db, profileId, nodeId)
            .Take(maxBatch)
            .TranLock(DbLockType.Wait)
            .ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count == 0)
            return new(0, 0);

        long refunded = 0;
        foreach (var row in rows)
        {
            var updated = await EscrowFinalizeUpdate(db, profileId, row.MatchId, UserHostEscrowState.Refunded, recoveredAtUnixMilliseconds)
                .ExecuteCommandAsync(ct).ConfigureAwait(false);
            if (updated != 1)
                throw new InvalidOperationException($"Sponsor recovery transition affected {updated} row(s) for '{row.MatchId}'.");

            if (row.PrizeLucci > 0)
            {
                await db.Insertable(new UserHostClaimRow
                {
                    Profile = profileId,
                    UserNum = row.HostUserNum,
                    MatchId = row.MatchId,
                    AmountLucci = row.PrizeLucci,
                    Reason = "recovery",
                    State = (int)UserHostClaimState.Pending,
                    CreatedAtMs = recoveredAtUnixMilliseconds,
                }).ExecuteCommandAsync(ct).ConfigureAwait(false);
                refunded = checked(refunded + row.PrizeLucci);
            }
        }

        await tx.CommitAsync().ConfigureAwait(false);
        return new(rows.Count, refunded);
    }

    /// <inheritdoc />
    public async ValueTask<UserHostClaimFlushResult> FlushPendingClaimsAsync(
        string profileId,
        long userNum,
        int maxClaims,
        long claimedAtUnixMilliseconds,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (profileId.Length > UserHostLimits.MaxProfileIdLength)
            throw new ArgumentOutOfRangeException(nameof(profileId), "Profile identifier exceeds maximum length.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        if (maxClaims is < 1 or > UserHostLimits.MaxPendingClaimsPerLogin)
            throw new ArgumentOutOfRangeException(nameof(maxClaims));
        ArgumentOutOfRangeException.ThrowIfNegative(claimedAtUnixMilliseconds);
        ct.ThrowIfCancellationRequested();

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var tx = await session.BeginWriteAsync(userNum, authority, ct).ConfigureAwait(false);

        var playerLocked = await MySqlPlayerLock.LockQuery(db, userNum).ToListAsync(ct).ConfigureAwait(false);
        if (playerLocked.Count == 0)
            return new(0, 0, null);

        var pending = await PendingClaimsQuery(db, profileId, userNum)
            .Take(maxClaims)
            .TranLock(DbLockType.Wait)
            .ToListAsync(ct).ConfigureAwait(false);
        if (pending.Count == 0)
            return new(0, 0, null);

        var claimIds = pending.Select(c => c.ClaimId).ToArray();
        var changed = await ClaimsMarkClaimedUpdate(db, claimIds, claimedAtUnixMilliseconds)
            .ExecuteCommandAsync(ct).ConfigureAwait(false);
        if (changed != pending.Count)
            throw new InvalidOperationException($"Sponsor claim flush affected {changed} row(s), expected {pending.Count}.");

        long credited = pending.Sum(c => (long)c.AmountLucci);
        var wallets = await WalletQuery(db, userNum).TranLock(DbLockType.Wait).ToListAsync(ct).ConfigureAwait(false);
        if (wallets.Count != 1)
            throw new InvalidOperationException($"Sponsor claim flush requires player_wallets row for user {userNum}.");

        var wallet = wallets[0];
        var lucciAfter = checked((int)(wallet.Lucci + credited));
        var walletChanged = await db.Updateable<WalletRow>()
            .SetColumns(w => w.Lucci == lucciAfter)
            .SetColumns(w => w.UpdatedAt == SqlFunc.MappingColumn(default(DateTime), "UTC_TIMESTAMP()"))
            .Where(w => w.UserNum == userNum && w.Lucci == wallet.Lucci)
            .ExecuteCommandAsync(ct).ConfigureAwait(false);
        if (walletChanged != 1)
            throw new InvalidOperationException($"Sponsor claim flush wallet update affected {walletChanged} row(s) for user {userNum}.");

        await tx.CommitAsync().ConfigureAwait(false);
        return new(pending.Count, credited, new PlayerWallet(userNum, lucciAfter, wallet.Cash));
    }

    internal static ISugarQueryable<UserHostEscrowRow> EscrowQuery(SqlSugarClient db, string profileId, string matchId) =>
        db.Queryable<UserHostEscrowRow>()
            .Where(e => e.Profile == profileId && e.MatchId == matchId);

    internal static ISugarQueryable<UserHostEscrowRow> ActiveEscrowsQuery(SqlSugarClient db, string profileId, string nodeId) =>
        db.Queryable<UserHostEscrowRow>()
            .Where(e => e.Profile == profileId && e.NodeId == nodeId && e.State == (int)UserHostEscrowState.Active)
            .OrderBy(e => e.CreatedAtMs);

    internal static ISugarQueryable<WalletRow> WalletQuery(SqlSugarClient db, long userNum) =>
        db.Queryable<WalletRow>()
            .Where(w => w.UserNum == userNum);

    internal static ISugarQueryable<InventorySlotRow> TicketQuery(SqlSugarClient db, long userNum, int itemId) =>
        db.Queryable<InventorySlotRow>()
            .Where(row => row.UserNum == userNum && row.Bag == 0 && row.ItemId == itemId)
            .OrderBy(row => row.Ordinal, OrderByType.Desc)
            .Take(1);

    internal static ISugarQueryable<UserHostClaimRow> PendingClaimsQuery(SqlSugarClient db, string profileId, long userNum) =>
        db.Queryable<UserHostClaimRow>()
            .Where(c => c.Profile == profileId && c.UserNum == userNum && c.State == (int)UserHostClaimState.Pending)
            .OrderBy(c => c.ClaimId);

    internal static IUpdateable<UserHostEscrowRow> EscrowFinalizeUpdate(
        SqlSugarClient db,
        string profileId,
        string matchId,
        UserHostEscrowState targetState,
        long settledAtMs) =>
        db.Updateable<UserHostEscrowRow>()
            .SetColumns(e => e.State == (int)targetState)
            .SetColumns(e => e.SettledAtMs == settledAtMs)
            .Where(e => e.Profile == profileId && e.MatchId == matchId && e.State == (int)UserHostEscrowState.Active);

    internal static IUpdateable<UserHostClaimRow> ClaimsMarkClaimedUpdate(
        SqlSugarClient db,
        long[] claimIds,
        long claimedAtMs) =>
        db.Updateable<UserHostClaimRow>()
            .SetColumns(c => c.State == (int)UserHostClaimState.Claimed)
            .SetColumns(c => c.ClaimedAtMs == claimedAtMs)
            .Where(c => claimIds.Contains(c.ClaimId) && c.State == (int)UserHostClaimState.Pending);
}
