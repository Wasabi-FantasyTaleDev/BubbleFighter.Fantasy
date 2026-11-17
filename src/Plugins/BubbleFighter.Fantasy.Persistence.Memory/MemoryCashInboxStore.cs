using BubbleFighter.Fantasy.Contracts.CashInbox;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Shop;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// Thread-safe in-process implementation of <see cref="ICashInboxStore"/>. Slot identifiers increase
/// monotonically across the store lifetime and are never reused after pickup or expiry. Lock acquisition
/// always follows <c>PlayerEntry.Gate -&gt; _gate</c> so concurrent bag updates and inbox claims cannot deadlock.
/// </summary>
public sealed class MemoryCashInboxStore(MemoryPlayerTable table) : ICashInboxStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string ProfileId, long CashSlotId), CashInboxEntryRecord> _slots = new();
    private readonly Dictionary<(string ProfileId, long UserNum), List<long>> _ownerSlots = new();
    private readonly Dictionary<(string ProfileId, long UserNum, string ClaimKey), long> _claims = new();
    private long _lastSlotId;

    /// <inheritdoc />
    public ValueTask<CashInboxGrantResult> GrantAsync(CashInboxGrantCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();

        if (table.Find(command.UserNum) is null)
            return ValueTask.FromResult(new CashInboxGrantResult(CashInboxGrantOutcome.OwnerMissing, null));

        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (command.ClaimKey is not null
                && _claims.ContainsKey((command.ProfileId, command.UserNum, command.ClaimKey)))
            {
                return ValueTask.FromResult(new CashInboxGrantResult(CashInboxGrantOutcome.AlreadyClaimed, null));
            }

            var ownerKey = (command.ProfileId, command.UserNum);
            if (!_ownerSlots.TryGetValue(ownerKey, out var ownedIds))
            {
                ownedIds = [];
                _ownerSlots[ownerKey] = ownedIds;
            }

            var activeCount = 0;
            foreach (var existingId in ownedIds)
            {
                if (_slots.TryGetValue((command.ProfileId, existingId), out var existing)
                    && existing.IsActiveAt(command.CreatedAtUnixMilliseconds))
                {
                    activeCount++;
                }
            }

            if (!command.IgnoreInboxLimit && activeCount >= command.MaxEntriesPerPlayer)
                return ValueTask.FromResult(new CashInboxGrantResult(CashInboxGrantOutcome.BoxFull, null));

            if (_lastSlotId >= CashInboxLimits.MaxCashSlotId)
                throw new InvalidOperationException("Cash inbox slot identifiers are exhausted.");

            var slotId = checked(++_lastSlotId);
            var entry = new CashInboxEntryRecord(
                slotId,
                command.ProfileId,
                command.UserNum,
                command.StockKey,
                command.ItemKey,
                command.Quantity,
                command.Days,
                command.Note,
                command.CreatedAtUnixMilliseconds,
                command.ExpiresAtUnixMilliseconds,
                CashInboxEntryState.Active)
            {
                SecondaryId = command.SecondaryId,
            };

            _slots[(command.ProfileId, slotId)] = entry;
            ownedIds.Add(slotId);
            if (command.ClaimKey is not null)
                _claims[(command.ProfileId, command.UserNum, command.ClaimKey)] = slotId;

            return ValueTask.FromResult(new CashInboxGrantResult(CashInboxGrantOutcome.Granted, entry));
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CashInboxEntryRecord>> ListActiveAsync(
        string profileId,
        long userNum,
        int maxEntries,
        long nowUnixMilliseconds,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (profileId.Length > 32)
            throw new ArgumentOutOfRangeException(nameof(profileId), "Profile identifier exceeds maximum length.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        if (maxEntries is < 1 or > InventoryLimits.MaxCashInventorySlots)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        ArgumentOutOfRangeException.ThrowIfNegative(nowUnixMilliseconds);
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (!_ownerSlots.TryGetValue((profileId, userNum), out var ownedIds) || ownedIds.Count == 0)
                return ValueTask.FromResult<IReadOnlyList<CashInboxEntryRecord>>([]);

            var result = new List<CashInboxEntryRecord>(Math.Min(maxEntries, ownedIds.Count));
            foreach (var slotId in ownedIds)
            {
                if (_slots.TryGetValue((profileId, slotId), out var entry) && entry.IsActiveAt(nowUnixMilliseconds))
                {
                    result.Add(entry);
                    if (result.Count >= maxEntries)
                        break;
                }
            }

            return ValueTask.FromResult<IReadOnlyList<CashInboxEntryRecord>>(result);
        }
    }

    /// <inheritdoc />
    public ValueTask<CashInboxEntryRecord?> FindBySlotIdAsync(string profileId, long cashSlotId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (profileId.Length > 32)
            throw new ArgumentOutOfRangeException(nameof(profileId), "Profile identifier exceeds maximum length.");
        if (cashSlotId is <= 0 or > CashInboxLimits.MaxCashSlotId)
            throw new ArgumentOutOfRangeException(nameof(cashSlotId));
        ct.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_slots.GetValueOrDefault((profileId, cashSlotId)));
        }
    }

    /// <inheritdoc />
    public ValueTask<CashInboxPickupResult> PickUpAsync(CashInboxPickupCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();

        var owner = table.Find(command.UserNum);
        if (owner is null)
        {
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                return ValueTask.FromResult(ResolveNonOwnerOutcome(command));
            }
        }

        lock (owner.Gate)
        {
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                var key = (command.ProfileId, command.CashSlotId);
                if (!_slots.TryGetValue(key, out var entry) || entry.State != CashInboxEntryState.Active)
                    return ValueTask.FromResult(new CashInboxPickupResult(CashInboxPickupOutcome.NotFound, entry, null));

                if (entry.ExpiresAtUnixMilliseconds > 0
                    && entry.ExpiresAtUnixMilliseconds <= command.PickedUpAtUnixMilliseconds)
                {
                    var expired = entry with { State = CashInboxEntryState.Expired };
                    _slots[key] = expired;
                    return ValueTask.FromResult(new CashInboxPickupResult(CashInboxPickupOutcome.NotFound, expired, null));
                }

                if (entry.UserNum != command.UserNum)
                    return ValueTask.FromResult(new CashInboxPickupResult(CashInboxPickupOutcome.OwnerMismatch, entry, null));

                var currentBag = owner.Bags.TryGetValue((int)InventoryBag.CharacterBag, out var existingBag)
                    ? existingBag
                    : [];
                var plan = command.PlanCharacterBagGrants(currentBag, owner.LastPurchaseSlotKey);
                if (plan.Outcome != CashInboxPickupOutcome.PickedUp)
                    return ValueTask.FromResult(new CashInboxPickupResult(plan.Outcome, entry, null));

                var walletAfter = command.ResolveWalletAfter(owner.Money);
                if (walletAfter is null)
                    return ValueTask.FromResult(new CashInboxPickupResult(CashInboxPickupOutcome.Failed, entry, null));

                ct.ThrowIfCancellationRequested();
                owner.Bags[(int)InventoryBag.CharacterBag] = plan.BagAfter.ToList();
                owner.LastPurchaseSlotKey = plan.HighestSlotKey;
                owner.Money = walletAfter;
                owner.Progress ??= new PlayerProgress(command.UserNum, 1, 0);

                var pickedUp = entry with { State = CashInboxEntryState.PickedUp };
                _slots[key] = pickedUp;
                return ValueTask.FromResult(new CashInboxPickupResult(
                    CashInboxPickupOutcome.PickedUp,
                    pickedUp,
                    new ShopPurchaseResult(walletAfter, plan.AppliedSlots)));
            }
        }
    }

    private CashInboxPickupResult ResolveNonOwnerOutcome(CashInboxPickupCommand command)
    {
        var key = (command.ProfileId, command.CashSlotId);
        if (!_slots.TryGetValue(key, out var entry) || entry.State != CashInboxEntryState.Active)
            return new(CashInboxPickupOutcome.NotFound, entry, null);

        if (entry.ExpiresAtUnixMilliseconds > 0
            && entry.ExpiresAtUnixMilliseconds <= command.PickedUpAtUnixMilliseconds)
        {
            var expired = entry with { State = CashInboxEntryState.Expired };
            _slots[key] = expired;
            return new(CashInboxPickupOutcome.NotFound, expired, null);
        }

        return entry.UserNum != command.UserNum
            ? new(CashInboxPickupOutcome.OwnerMismatch, entry, null)
            : new(CashInboxPickupOutcome.Failed, entry, null);
    }
}
