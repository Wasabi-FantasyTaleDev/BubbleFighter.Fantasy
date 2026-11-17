using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Wallet;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// Thread-safe in-process implementation of <see cref="IUserHostPrizeStore"/>. Lock acquisition
/// always acquires player <c>Gate</c> instances in ascending <c>UserNum</c> order before <c>_gate</c>
/// so concurrent escrows, settlements, bag writes, and login claim flushes cannot deadlock.
/// </summary>
public sealed class MemoryUserHostPrizeStore(MemoryPlayerTable table) : IUserHostPrizeStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string ProfileId, string MatchId), EscrowEntry> _escrows = new();
    private readonly List<ClaimEntry> _claims = [];
    private long _lastClaimId;

    private sealed record EscrowEntry(
        string ProfileId,
        string MatchId,
        string NodeId,
        int RoomId,
        long HostUserNum,
        int PrizeLucci,
        int FeePercent,
        long CreatedAtMs)
    {
        public UserHostEscrowState State { get; set; } = UserHostEscrowState.Active;
        public long? SettledAtMs { get; set; }
    }

    private sealed record ClaimEntry(
        long ClaimId,
        string ProfileId,
        long UserNum,
        string MatchId,
        int AmountLucci,
        string Reason,
        long CreatedAtMs)
    {
        public UserHostClaimState State { get; set; } = UserHostClaimState.Pending;
        public long? ClaimedAtMs { get; set; }
    }

    /// <inheritdoc />
    public ValueTask<UserHostEscrowBeginResult> BeginEscrowAsync(UserHostEscrowBeginCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();

        var owner = table.Find(command.HostUserNum);
        if (owner is null)
        {
            lock (_gate)
            {
                if (_escrows.ContainsKey((command.ProfileId, command.MatchId)))
                    return ValueTask.FromResult(new UserHostEscrowBeginResult(UserHostEscrowBeginOutcome.AlreadyCommitted, null, 0));
            }
            return ValueTask.FromResult(new UserHostEscrowBeginResult(UserHostEscrowBeginOutcome.HostMissing, null, 0));
        }

        lock (owner.Gate)
        {
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                var money = owner.Money ?? new PlayerWallet(command.HostUserNum, 0, 0);
                var key = (command.ProfileId, command.MatchId);
                if (_escrows.ContainsKey(key))
                    return ValueTask.FromResult(new UserHostEscrowBeginResult(
                        UserHostEscrowBeginOutcome.AlreadyCommitted, null, money.Lucci));

                if (money.Lucci < command.PrizeLucci)
                    return ValueTask.FromResult(new UserHostEscrowBeginResult(
                        UserHostEscrowBeginOutcome.InsufficientLucci, null, money.Lucci));

                var bag = owner.Bags.GetValueOrDefault((int)InventoryBag.CharacterBag);
                var item = bag?.LastOrDefault(row => row.ItemId == command.TicketItemId);
                if (item is null || item.Quantity <= 0 || item.Lifetime.Kind == InventoryLifetimeKind.Life
                    || (item.Lifetime.ExpiresAtUnixMilliseconds is { } end && end <= command.CreatedAtUnixMilliseconds))
                {
                    return ValueTask.FromResult(new UserHostEscrowBeginResult(
                        UserHostEscrowBeginOutcome.MissingTicket, null, money.Lucci));
                }

                var remaining = item.Quantity - 1;
                foreach (var bagKind in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
                {
                    if (owner.Bags.TryGetValue((int)bagKind, out var rows))
                    {
                        owner.Bags[(int)bagKind] = rows
                            .Where(row => row.SlotKey != item.SlotKey || remaining > 0)
                            .Select(row => row.SlotKey == item.SlotKey ? row with { Quantity = remaining } : row)
                            .ToList();
                    }
                }

                var lucciAfter = checked(money.Lucci - command.PrizeLucci);
                owner.Money = money with { Lucci = lucciAfter };
                owner.Progress ??= new PlayerProgress(command.HostUserNum, 1, 0);
                _escrows[key] = new EscrowEntry(
                    command.ProfileId,
                    command.MatchId,
                    command.NodeId,
                    command.RoomId,
                    command.HostUserNum,
                    command.PrizeLucci,
                    command.FeePercent,
                    command.CreatedAtUnixMilliseconds);

                return ValueTask.FromResult(new UserHostEscrowBeginResult(
                    UserHostEscrowBeginOutcome.Committed, remaining, lucciAfter));
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<UserHostSettleResult> SettleEscrowAsync(UserHostSettleCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();

        var candidateUserNums = new SortedSet<long>();
        foreach (var winner in command.WinnerPayouts)
            candidateUserNums.Add(winner.UserNum);
        EscrowEntry? snapshotEscrow;
        lock (_gate)
        {
            if (!_escrows.TryGetValue((command.ProfileId, command.MatchId), out snapshotEscrow))
                return ValueTask.FromResult(new UserHostSettleResult(UserHostSettlementOutcome.NotFound, []));
            if (snapshotEscrow.State != UserHostEscrowState.Active)
                return ValueTask.FromResult(new UserHostSettleResult(UserHostSettlementOutcome.AlreadyFinalized, []));
            candidateUserNums.Add(snapshotEscrow.HostUserNum);
        }

        var lockedOwners = new List<(long UserNum, PlayerEntry Entry)>();
        foreach (var userNum in candidateUserNums)
        {
            if (table.Find(userNum) is { } entry)
                lockedOwners.Add((userNum, entry));
        }

        return ValueTask.FromResult(AcquireOrderedAndSettle(lockedOwners, 0, command, ct));
    }

    private UserHostSettleResult AcquireOrderedAndSettle(
        List<(long UserNum, PlayerEntry Entry)> owners,
        int index,
        UserHostSettleCommand command,
        CancellationToken ct)
    {
        if (index < owners.Count)
        {
            lock (owners[index].Entry.Gate)
                return AcquireOrderedAndSettle(owners, index + 1, command, ct);
        }

        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (!_escrows.TryGetValue((command.ProfileId, command.MatchId), out var escrow))
                return new(UserHostSettlementOutcome.NotFound, []);
            if (escrow.State != UserHostEscrowState.Active)
                return new(UserHostSettlementOutcome.AlreadyFinalized, []);

            var onlineSet = command.OnlineUserNums.ToHashSet();
            var ownerMap = owners.ToDictionary(pair => pair.UserNum, pair => pair.Entry);
            var payouts = new List<UserHostPayoutEntry>();

            if (command.IsDrawOrCancelled)
            {
                if (escrow.PrizeLucci > 0)
                    payouts.Add(CreditOrQueue(command.ProfileId, escrow.HostUserNum, command.MatchId, escrow.PrizeLucci,
                        "refund", onlineSet.Contains(escrow.HostUserNum), ownerMap, command.SettledAtUnixMilliseconds));
                escrow.State = UserHostEscrowState.Refunded;
                escrow.SettledAtMs = command.SettledAtUnixMilliseconds;
                return new(UserHostSettlementOutcome.Applied, payouts);
            }

            foreach (var allocation in command.WinnerPayouts)
            {
                if (allocation.AmountLucci <= 0)
                    continue;
                payouts.Add(CreditOrQueue(command.ProfileId, allocation.UserNum, command.MatchId, allocation.AmountLucci,
                    "win", onlineSet.Contains(allocation.UserNum), ownerMap, command.SettledAtUnixMilliseconds));
            }
            escrow.State = UserHostEscrowState.Settled;
            escrow.SettledAtMs = command.SettledAtUnixMilliseconds;
            return new(UserHostSettlementOutcome.Applied, payouts);
        }
    }

    private UserHostPayoutEntry CreditOrQueue(
        string profileId,
        long userNum,
        string matchId,
        int amountLucci,
        string reason,
        bool isOnline,
        Dictionary<long, PlayerEntry> ownerMap,
        long timestampMs)
    {
        if (isOnline && ownerMap.TryGetValue(userNum, out var owner))
        {
            var money = owner.Money ?? new PlayerWallet(userNum, 0, 0);
            var nextLucci = checked(money.Lucci + amountLucci);
            owner.Money = money with { Lucci = nextLucci };
            return new(userNum, amountLucci, CreditedImmediately: true, nextLucci);
        }

        var claimId = checked(++_lastClaimId);
        _claims.Add(new ClaimEntry(claimId, profileId, userNum, matchId, amountLucci, reason, timestampMs));
        return new(userNum, amountLucci, CreditedImmediately: false, null);
    }

    /// <inheritdoc />
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
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            var active = _escrows.Values
                .Where(e => string.Equals(e.ProfileId, profileId, StringComparison.Ordinal)
                    && string.Equals(e.NodeId, nodeId, StringComparison.Ordinal)
                    && e.State == UserHostEscrowState.Active)
                .OrderBy(e => e.CreatedAtMs)
                .ThenBy(e => e.MatchId, StringComparer.Ordinal)
                .Take(maxBatch)
                .ToArray();

            long refundedLucci = 0;
            foreach (var escrow in active)
            {
                escrow.State = UserHostEscrowState.Refunded;
                escrow.SettledAtMs = recoveredAtUnixMilliseconds;
                if (escrow.PrizeLucci > 0)
                {
                    var claimId = checked(++_lastClaimId);
                    _claims.Add(new ClaimEntry(
                        claimId, profileId, escrow.HostUserNum, escrow.MatchId,
                        escrow.PrizeLucci, "recovery", recoveredAtUnixMilliseconds));
                    refundedLucci = checked(refundedLucci + escrow.PrizeLucci);
                }
            }
            return ValueTask.FromResult(new UserHostRecoverResult(active.Length, refundedLucci));
        }
    }

    /// <inheritdoc />
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
        ct.ThrowIfCancellationRequested();

        var owner = table.Find(userNum);
        if (owner is null)
            return ValueTask.FromResult(new UserHostClaimFlushResult(0, 0, null));

        lock (owner.Gate)
        {
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                var pending = _claims
                    .Where(c => string.Equals(c.ProfileId, profileId, StringComparison.Ordinal)
                        && c.UserNum == userNum
                        && c.State == UserHostClaimState.Pending)
                    .OrderBy(c => c.ClaimId)
                    .Take(maxClaims)
                    .ToArray();
                if (pending.Length == 0)
                    return ValueTask.FromResult(new UserHostClaimFlushResult(0, 0, null));

                long credited = 0;
                foreach (var claim in pending)
                {
                    claim.State = UserHostClaimState.Claimed;
                    claim.ClaimedAtMs = claimedAtUnixMilliseconds;
                    credited = checked(credited + claim.AmountLucci);
                }
                var money = owner.Money ?? new PlayerWallet(userNum, 0, 0);
                var lucciAfter = checked((int)(money.Lucci + credited));
                owner.Money = money with { Lucci = lucciAfter };
                return ValueTask.FromResult(new UserHostClaimFlushResult(pending.Length, credited, owner.Money));
            }
        }
    }
}
