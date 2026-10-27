using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>
/// The save's inventory fragment: bag reads plus the atomic expiry, consumption, append, equipment and
/// worn-flag operations, each a whole-snapshot mutation inside one database write transaction.
/// </summary>
internal sealed partial class SqliteRepositories : IInventoryRepository, IPlayerInventoryState
{
    public ValueTask<IReadOnlyList<InventorySlotRecord>?> RemoveExpiredItemAsync(long userNum, int itemId,
        DateTimeOffset now, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemId);
        return new(database.Write<IReadOnlyList<InventorySlotRecord>?>((db, _) =>
        {
            var state = db.RequireState(userNum);
            var removed = InventoryItemRemoval.SelectExpired(userNum, itemId, state.Slots, now);
            if (removed is { Count: > 0 })
            {
                state.Slots = state.Slots.Where(row => row.ItemId != itemId).ToList();
                db.Save(userNum, state);
            }
            return removed;
        }, ct));
    }

    private T Mutate<T>(long user, Func<SaveState, T> change, CancellationToken ct) =>
        Mutate(user, (state, _) => change(state), ct);

    // The change also sees the open transaction, for writes to tables beside the player's snapshot.
    private T Mutate<T>(long user, Func<SaveState, Microsoft.Data.Sqlite.SqliteTransaction, T> change, CancellationToken ct) =>
        database.Write((db, transaction) =>
        {
            var state = db.RequireState(user);
            var result = change(state, transaction);
            db.Save(user, state);
            return result;
        }, ct);

    private static void RequireBag(InventoryBag bag)
    {
        if (bag is not (InventoryBag.CharacterBag or InventoryBag.EquippedBag))
            throw new ArgumentOutOfRangeException(nameof(bag));
    }

    private static void RequireBound(int bound, int maximum)
    {
        if (bound < 1 || bound > maximum) throw new ArgumentOutOfRangeException(nameof(bound));
    }

    public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(long userNum, InventoryBag bag, int maxCount, CancellationToken ct)
    {
        RequireBag(bag);
        RequireBound(maxCount, InventoryLimits.MaxInventorySlotsPerBag);
        return new(database.Read<IReadOnlyList<InventorySlotRecord>>(db =>
        {
            var rows = db.State(userNum)?.Slots.Where(slot => slot.Bag == bag).ToArray() ?? [];
            if (rows.Length > maxCount) throw new InvalidOperationException("Inventory snapshot exceeds requested bound.");
            return rows;
        }, ct));
    }

    public ValueTask ReplaceSlotsAsync(long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct)
    {
        RequireBag(bag);
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count > InventoryLimits.MaxInventorySlotsPerBag || slots.Any(slot => slot.UserNum != userNum || slot.Bag != bag))
            throw new ArgumentException("Inventory snapshot exceeds its bound or targets another player or bag.", nameof(slots));
        var snapshot = slots.ToArray();
        Mutate(userNum, state => { state.Slots.RemoveAll(slot => slot.Bag == bag); state.Slots.AddRange(snapshot); return true; }, ct);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<InventorySlotRecord>> AppendMissingItemsAsync(long userNum, InventoryBag bag,
        IReadOnlyList<InventorySlotRecord> candidates, CancellationToken ct)
    {
        RequireBag(bag);
        return new(Mutate<IReadOnlyList<InventorySlotRecord>>(userNum, state =>
        {
            var merged = InventoryAppend.Merge(userNum, bag, state.Slots.Where(slot => slot.Bag == bag).ToArray(), candidates);
            state.Slots.RemoveAll(slot => slot.Bag == bag);
            state.Slots.AddRange(merged);
            return merged;
        }, ct));
    }

    public ValueTask<InventorySlotRecord?> SetSlotWornAsync(long userNum, int itemId, bool worn, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemId);
        return new(Mutate<InventorySlotRecord?>(userNum, state =>
        {
            InventorySlotRecord? last = null;
            for (var i = 0; i < state.Slots.Count; i++)
            {
                var slot = state.Slots[i];
                if (slot.Bag != InventoryBag.CharacterBag || slot.ItemId != itemId) continue;
                last = state.Slots[i] = slot with { Flags = worn ? (byte)(slot.Flags | InventorySlotRecord.WornFlagBit)
                    : (byte)(slot.Flags & ~InventorySlotRecord.WornFlagBit) };
            }
            return last;
        }, ct));
    }

    public ValueTask<int> AdvanceLifeAsync(long userNum, DateOnly today, CancellationToken ct) =>
        new(Mutate(userNum, state =>
        {
            DateOnly? last = state.LastLoginDay is { } text
                ? DateOnly.ParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null;
            var days = InventoryLife.DaysMissed(last, today);
            if (days > 0)
                state.Slots = state.Slots.Select(slot => slot.Lifetime.Kind == InventoryLifetimeKind.Life
                    ? slot with { Quantity = InventoryLife.Reduce(slot.Quantity, days) } : slot).ToList();
            state.LastLoginDay = InventoryLife.NextLoginDay(last, today).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            return days;
        }, ct));

    public ValueTask<IReadOnlyList<InventorySlotRecord>> RemoveExpiredAsync(long userNum, DateTimeOffset now, CancellationToken ct) =>
        new(Mutate<IReadOnlyList<InventorySlotRecord>>(userNum, state =>
        {
            var removed = state.Slots.Where(slot => InventoryExpiry.IsExpired(slot, now)).ToArray();
            if (removed.Length > 0) state.Slots = state.Slots.Where(slot => !InventoryExpiry.IsExpired(slot, now)).ToList();
            return removed;
        }, ct));

    public ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateWornSelectionAsync(long userNum,
        IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct) =>
        new(Mutate<IReadOnlyList<InventorySlotRecord>?>(userNum, state =>
        {
            var changed = InventoryWearSelection.Apply(state.Slots, groups, itemId, worn);
            if (changed is null) return null;
            state.Slots = changed.ToList();
            return state.Slots.Where(slot => slot.Bag == InventoryBag.CharacterBag).ToArray();
        }, ct));

    public ValueTask<int?> ConsumeItemAsync(long userNum, int itemId, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemId);
        return new(Mutate(userNum, state => Consume(state, itemId), ct));
    }

    private static int? Consume(SaveState state, int itemId)
    {
        var item = state.Slots.LastOrDefault(slot => slot.Bag == InventoryBag.CharacterBag && slot.ItemId == itemId);
        if (item is null || item.Quantity <= 0 || item.Lifetime.Kind == InventoryLifetimeKind.Life
            || item.Lifetime.ExpiresAtUnixMilliseconds is { } end
            && end <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return null;
        var remaining = item.Quantity - 1;
        state.Slots = state.Slots.Where(slot => slot.ItemId != itemId || remaining > 0)
            .Select(slot => slot.ItemId == itemId ? slot with { Quantity = remaining } : slot).ToList();
        return remaining;
    }

    public ValueTask<byte> GetActiveWeaponPresetSlotAsync(long userNum, CancellationToken ct) =>
        new(database.Read(db => db.State(userNum)?.ActivePreset ?? 0, ct));

    public ValueTask SetActiveWeaponPresetSlotAsync(long userNum, byte slot, CancellationToken ct)
    {
        if (slot > InventoryLimits.MaxWeaponPresetSlot) throw new ArgumentOutOfRangeException(nameof(slot));
        Mutate(userNum, state => state.ActivePreset = slot, ct);
        return ValueTask.CompletedTask;
    }

    public ValueTask<WeaponPresetPage[]?> GetWeaponPresetPagesAsync(long userNum, CancellationToken ct) =>
        new(database.Read(db => db.State(userNum)?.WeaponPresetPages?.ToArray(), ct));

    public ValueTask<WeaponPresetPage[]> GetOrCreateWeaponPresetPagesAsync(long userNum, WeaponPresetPage[] initialPages, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        var copy = WeaponPresetPages.CopyTable(initialPages);
        return new(Mutate(userNum, state =>
        {
            state.WeaponPresetPages ??= copy;
            return state.WeaponPresetPages.ToArray();
        }, ct));
    }

    public ValueTask SetWeaponPresetPagesAsync(long userNum, WeaponPresetPage[] pages, CancellationToken ct)
    {
        // CopyTable validates too: the snapshot check rejects anything but a full five-page table.
        var copy = WeaponPresetPages.CopyTable(pages);
        Mutate(userNum, state => state.WeaponPresetPages = copy, ct);
        return ValueTask.CompletedTask;
    }
}
