using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Wallet;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

internal sealed partial class SqliteRepositories : IUserHostPrizeStore
{
    public ValueTask<UserHostEscrowBeginResult> BeginEscrowAsync(UserHostEscrowBeginCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();

        return new(database.Write<UserHostEscrowBeginResult>((db, _) =>
        {
            if (db.Orm.Queryable<UserHostEscrowRow>()
                .Any(e => e.Profile == command.ProfileId && e.MatchId == command.MatchId))
            {
                var existingState = db.State(command.HostUserNum);
                return new(UserHostEscrowBeginOutcome.AlreadyCommitted, null, existingState?.Wallet?.Lucci ?? 0);
            }

            if (db.State(command.HostUserNum) is not { } state)
                return new(UserHostEscrowBeginOutcome.HostMissing, null, 0);

            var wallet = state.Wallet ?? new PlayerWallet(command.HostUserNum, 0, 0);
            if (wallet.Lucci < command.PrizeLucci)
                return new(UserHostEscrowBeginOutcome.InsufficientLucci, null, wallet.Lucci);

            var item = state.Slots.LastOrDefault(slot =>
                slot.Bag == InventoryBag.CharacterBag && slot.ItemId == command.TicketItemId);
            if (item is null || item.Quantity <= 0 || item.Lifetime.Kind == InventoryLifetimeKind.Life
                || (item.Lifetime.ExpiresAtUnixMilliseconds is { } end && end <= command.CreatedAtUnixMilliseconds))
            {
                return new(UserHostEscrowBeginOutcome.MissingTicket, null, wallet.Lucci);
            }

            var remaining = item.Quantity - 1;
            var lucciAfter = checked(wallet.Lucci - command.PrizeLucci);
            state.Slots = state.Slots
                .Where(slot => slot.SlotKey != item.SlotKey || (slot.Bag is not (InventoryBag.CharacterBag or InventoryBag.EquippedBag)) || remaining > 0)
                .Select(slot => slot.SlotKey == item.SlotKey && slot.Bag is InventoryBag.CharacterBag or InventoryBag.EquippedBag
                    ? slot with { Quantity = remaining } : slot)
                .ToList();
            state.Wallet = wallet with { Lucci = lucciAfter };
            db.Save(command.HostUserNum, state);

            db.Orm.Insertable(new UserHostEscrowRow
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
            }).ExecuteCommand();

            return new(UserHostEscrowBeginOutcome.Committed, remaining, lucciAfter);
        }, ct));
    }

    public ValueTask<UserHostSettleResult> SettleEscrowAsync(UserHostSettleCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();

        return new(database.Write<UserHostSettleResult>((db, _) =>
        {
            var escrow = db.Orm.Queryable<UserHostEscrowRow>()
                .Where(e => e.Profile == command.ProfileId && e.MatchId == command.MatchId)
                .First();
            if (escrow is null)
                return new(UserHostSettlementOutcome.NotFound, []);
            if (escrow.State != (int)UserHostEscrowState.Active)
                return new(UserHostSettlementOutcome.AlreadyFinalized, []);

            var targetState = command.IsDrawOrCancelled
                ? (int)UserHostEscrowState.Refunded
                : (int)UserHostEscrowState.Settled;

            var changed = db.Orm.Updateable<UserHostEscrowRow>()
                .SetColumns(e => new UserHostEscrowRow
                {
                    State = targetState,
                    SettledAtMs = command.SettledAtUnixMilliseconds,
                })
                .Where(e => e.Profile == command.ProfileId
                    && e.MatchId == command.MatchId
                    && e.State == (int)UserHostEscrowState.Active)
                .ExecuteCommand();
            if (changed != 1)
                throw new InvalidDataException("Sponsor escrow settlement transition affected an invalid row count.");

            var onlineSet = command.OnlineUserNums.ToHashSet();
            var payouts = new List<UserHostPayoutEntry>();

            if (command.IsDrawOrCancelled)
            {
                if (escrow.PrizeLucci > 0)
                    payouts.Add(CreditOrQueueSqlite(db, command.ProfileId, escrow.HostUserNum, command.MatchId,
                        escrow.PrizeLucci, "refund", onlineSet.Contains(escrow.HostUserNum), command.SettledAtUnixMilliseconds));
                return new(UserHostSettlementOutcome.Applied, payouts);
            }

            foreach (var winner in command.WinnerPayouts)
            {
                if (winner.AmountLucci <= 0)
                    continue;
                payouts.Add(CreditOrQueueSqlite(db, command.ProfileId, winner.UserNum, command.MatchId,
                    winner.AmountLucci, "win", onlineSet.Contains(winner.UserNum), command.SettledAtUnixMilliseconds));
            }
            return new(UserHostSettlementOutcome.Applied, payouts);
        }, ct));
    }

    private static UserHostPayoutEntry CreditOrQueueSqlite(
        SqliteDatabase db,
        string profileId,
        long userNum,
        string matchId,
        int amountLucci,
        string reason,
        bool isOnline,
        long timestampMs)
    {
        if (isOnline && db.State(userNum) is { } state)
        {
            var wallet = state.Wallet ?? new PlayerWallet(userNum, 0, 0);
            var nextLucci = checked(wallet.Lucci + amountLucci);
            state.Wallet = wallet with { Lucci = nextLucci };
            db.Save(userNum, state);
            return new(userNum, amountLucci, CreditedImmediately: true, nextLucci);
        }

        db.Orm.Insertable(new UserHostClaimRow
        {
            Profile = profileId,
            UserNum = userNum,
            MatchId = matchId,
            AmountLucci = amountLucci,
            Reason = reason,
            State = (int)UserHostClaimState.Pending,
            CreatedAtMs = timestampMs,
        }).ExecuteCommand();
        return new(userNum, amountLucci, CreditedImmediately: false, null);
    }

    public ValueTask<UserHostRecoverResult> RecoverActiveEscrowsAsync(
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

        return new(database.Write<UserHostRecoverResult>((db, _) =>
        {
            var rows = db.Orm.Queryable<UserHostEscrowRow>()
                .Where(e => e.Profile == profileId && e.NodeId == nodeId && e.State == (int)UserHostEscrowState.Active)
                .OrderBy(e => e.CreatedAtMs)
                .Take(maxBatch)
                .ToList();
            if (rows.Count == 0)
                return new(0, 0);

            long refunded = 0;
            foreach (var row in rows)
            {
                var updated = db.Orm.Updateable<UserHostEscrowRow>()
                    .SetColumns(e => new UserHostEscrowRow
                    {
                        State = (int)UserHostEscrowState.Refunded,
                        SettledAtMs = recoveredAtUnixMilliseconds,
                    })
                    .Where(e => e.Profile == profileId
                        && e.MatchId == row.MatchId
                        && e.State == (int)UserHostEscrowState.Active)
                    .ExecuteCommand();
                if (updated != 1)
                    throw new InvalidDataException("Sponsor escrow recovery transition affected an invalid row count.");

                if (row.PrizeLucci > 0)
                {
                    db.Orm.Insertable(new UserHostClaimRow
                    {
                        Profile = profileId,
                        UserNum = row.HostUserNum,
                        MatchId = row.MatchId,
                        AmountLucci = row.PrizeLucci,
                        Reason = "recovery",
                        State = (int)UserHostClaimState.Pending,
                        CreatedAtMs = recoveredAtUnixMilliseconds,
                    }).ExecuteCommand();
                    refunded = checked(refunded + row.PrizeLucci);
                }
            }
            return new(rows.Count, refunded);
        }, ct));
    }

    public ValueTask<UserHostClaimFlushResult> FlushPendingClaimsAsync(
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

        return new(database.Write<UserHostClaimFlushResult>((db, _) =>
        {
            if (db.State(userNum) is not { } state)
                return new(0, 0, null);

            var pending = db.Orm.Queryable<UserHostClaimRow>()
                .Where(c => c.Profile == profileId && c.UserNum == userNum && c.State == (int)UserHostClaimState.Pending)
                .OrderBy(c => c.ClaimId)
                .Take(maxClaims)
                .ToList();
            if (pending.Count == 0)
                return new(0, 0, null);

            long credited = 0;
            foreach (var row in pending)
            {
                var changed = db.Orm.Updateable<UserHostClaimRow>()
                    .SetColumns(c => new UserHostClaimRow
                    {
                        State = (int)UserHostClaimState.Claimed,
                        ClaimedAtMs = claimedAtUnixMilliseconds,
                    })
                    .Where(c => c.ClaimId == row.ClaimId && c.State == (int)UserHostClaimState.Pending)
                    .ExecuteCommand();
                if (changed != 1)
                    throw new InvalidDataException("Sponsor claim flush transition affected an invalid row count.");
                credited = checked(credited + row.AmountLucci);
            }

            var wallet = state.Wallet ?? new PlayerWallet(userNum, 0, 0);
            var lucciAfter = checked((int)(wallet.Lucci + credited));
            state.Wallet = wallet with { Lucci = lucciAfter };
            db.Save(userNum, state);
            return new(pending.Count, credited, state.Wallet);
        }, ct));
    }
}
