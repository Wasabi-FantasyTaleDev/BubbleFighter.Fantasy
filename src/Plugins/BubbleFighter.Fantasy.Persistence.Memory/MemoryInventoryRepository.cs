using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// In-process store for the inventory snapshots behind S2C 802 / 803 / 810,
/// mirroring the guard behaviour of the MySQL store: the same user, bag, bound,
/// identity-match, ordinal and required-wstring checks run before any state is
/// touched, a replace stores the whole list in the given order, and the later
/// list reads it back in that order. Bag storage is keyed per player and per
/// bag, so the 802 character bag and the 803 container-1 bag never share rows.
/// </summary>
public sealed class MemoryInventoryRepository(MemoryPlayerTable table) : IInventoryRepository
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<InventorySlotRecord>?> RemoveExpiredItemAsync(long userNum, int itemId,
        DateTimeOffset now, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemId);
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(userNum);
        if (entry is null) return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>?>([]);
        lock (entry.Gate)
        {
            ct.ThrowIfCancellationRequested();
            var bags = new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag };
            var rows = bags.SelectMany(bag => entry.Bags.TryGetValue((int)bag, out var stored) ? stored : []).ToArray();
            var removed = InventoryItemRemoval.SelectExpired(userNum, itemId, rows, now);
            if (removed is { Count: > 0 })
            {
                var replacements = bags.Select(bag => rows.Where(row => row.Bag == bag && row.ItemId != itemId).ToList()).ToArray();
                ct.ThrowIfCancellationRequested();
                for (var i = 0; i < bags.Length; i++) entry.Bags[(int)bags[i]] = replacements[i];
            }
            return ValueTask.FromResult(removed);
        }
    }

    /// <inheritdoc />
    public ValueTask<int?> ConsumeItemAsync(long userNum, int itemId, CancellationToken ct)
    {
        RequireUserNum(userNum);
        if (itemId <= 0) throw new ArgumentOutOfRangeException(nameof(itemId));
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(userNum);
        if (entry is null) return ValueTask.FromResult<int?>(null);
        lock (entry.Gate)
        {
            var item = entry.Bags.GetValueOrDefault((int)InventoryBag.CharacterBag)?
                .LastOrDefault(row => row.ItemId == itemId);
            if (item is null || item.Quantity <= 0 || item.Lifetime.Kind == InventoryLifetimeKind.Life
                || item.Lifetime.ExpiresAtUnixMilliseconds is { } end && end <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                return ValueTask.FromResult<int?>(null);
            var remaining = item.Quantity - 1;
            foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
                if (entry.Bags.TryGetValue((int)bag, out var rows))
                    entry.Bags[(int)bag] = rows.Where(row => row.ItemId != itemId || remaining > 0)
                        .Select(row => row.ItemId == itemId ? row with { Quantity = remaining } : row).ToList();
            return ValueTask.FromResult<int?>(remaining);
        }
    }
    /// <inheritdoc />
    public ValueTask<int> AdvanceLifeAsync(long userNum, DateOnly today, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ct.ThrowIfCancellationRequested();
        var entry = table.GetOrAdd(userNum);
        lock (entry.Gate)
        {
            var days = InventoryLife.DaysMissed(entry.LastLoginDay, today);
            if (days > 0)
                foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
                    if (entry.Bags.TryGetValue((int)bag, out var rows))
                        entry.Bags[(int)bag] = rows.Select(row => row.Lifetime.Kind == InventoryLifetimeKind.Life
                            ? row with { Quantity = InventoryLife.Reduce(row.Quantity, days) } : row).ToList();
            entry.LastLoginDay = InventoryLife.NextLoginDay(entry.LastLoginDay, today);
            return ValueTask.FromResult(days);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<InventorySlotRecord>> RemoveExpiredAsync(long userNum, DateTimeOffset now, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(userNum);
        if (entry is null) return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>([]);
        lock (entry.Gate)
        {
            var removed = new List<InventorySlotRecord>();
            foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
            {
                if (!entry.Bags.TryGetValue((int)bag, out var rows)) continue;
                removed.AddRange(rows.Where(row => InventoryExpiry.IsExpired(row, now)));
                entry.Bags[(int)bag] = rows.Where(row => !InventoryExpiry.IsExpired(row, now)).ToList();
            }
            return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>(removed);
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateWornSelectionAsync(long userNum,
        IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ct.ThrowIfCancellationRequested();
        var entry = table.GetOrAdd(userNum);
        lock (entry.Gate)
        {
            var rows = new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag }
                .SelectMany(bag => entry.Bags.TryGetValue((int)bag, out var stored) ? stored : []).ToArray();
            var changed = InventoryWearSelection.Apply(rows, groups, itemId, worn);
            if (changed is null) return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>?>(null);
            foreach (var bag in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
                entry.Bags[(int)bag] = changed.Where(row => row.Bag == bag).ToList();
            return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>?>(changed.Where(row => row.Bag == InventoryBag.CharacterBag).ToArray());
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<InventorySlotRecord>> ListSlotsAsync(
        long userNum, InventoryBag bag, int maxCount, CancellationToken ct)
    {
        RequireUserNum(userNum);
        RequireBag(bag);
        RequireBound(maxCount, InventoryLimits.MaxInventorySlotsPerBag, nameof(maxCount));

        var entry = table.Find(userNum);
        if (entry is null)
            return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>([]);
        lock (entry.Gate)
        {
            var bag2 = entry.Bags;
            return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>(
                bag2.TryGetValue((int)bag, out var rows) ? [.. rows] : []);
        }
    }

    /// <inheritdoc />
    public ValueTask ReplaceSlotsAsync(
        long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> slots, CancellationToken ct)
    {
        RequireUserNum(userNum);
        RequireBag(bag);
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count > InventoryLimits.MaxInventorySlotsPerBag)
            throw new ArgumentOutOfRangeException(nameof(slots), slots.Count, $"inventory slot count must be 0..{InventoryLimits.MaxInventorySlotsPerBag}.");

        foreach (var slot in slots)
        {
            if (slot.UserNum != userNum)
                throw new ArgumentException($"inventory slot user_num {slot.UserNum} does not match replace target {userNum}.", nameof(slots));
            if (slot.Bag != bag)
                throw new ArgumentException($"inventory slot bag {slot.Bag} does not match replace target {bag}.", nameof(slots));
        }

        var entry = table.GetOrAdd(userNum);
        lock (entry.Gate)
            entry.Bags[(int)bag] = [.. slots];
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>Runs under the player's gate, the same lock <see cref="MemoryShopPurchaseStore"/> commits under.</remarks>
    public ValueTask<IReadOnlyList<InventorySlotRecord>> AppendMissingItemsAsync(
        long userNum, InventoryBag bag, IReadOnlyList<InventorySlotRecord> candidates, CancellationToken ct)
    {
        RequireUserNum(userNum);
        RequireBag(bag);
        ct.ThrowIfCancellationRequested();
        var entry = table.GetOrAdd(userNum);
        lock (entry.Gate)
        {
            var current = entry.Bags.TryGetValue((int)bag, out var rows) ? rows : [];
            var merged = InventoryAppend.Merge(userNum, bag, current, candidates);
            if (merged.Count != current.Count)
                entry.Bags[(int)bag] = [.. merged];
            return ValueTask.FromResult<IReadOnlyList<InventorySlotRecord>>([.. merged]);
        }
    }

    /// <inheritdoc />
    public ValueTask<InventorySlotRecord?> SetSlotWornAsync(
        long userNum, int itemId, bool worn, CancellationToken ct)
    {
        RequireUserNum(userNum);
        if (itemId <= 0)
            throw new ArgumentOutOfRangeException(nameof(itemId), itemId, "item id must be a positive item id.");

        var entry = table.Find(userNum);
        if (entry is null)
            return ValueTask.FromResult<InventorySlotRecord?>(null);
        lock (entry.Gate)
        {
            if (!entry.Bags.TryGetValue((int)InventoryBag.CharacterBag, out var rows))
                return ValueTask.FromResult<InventorySlotRecord?>(null);

            InventorySlotRecord? last = null;
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].ItemId != itemId)
                    continue;
                var flags = worn
                    ? (byte)(rows[i].Flags | InventorySlotRecord.WornFlagBit)
                    : (byte)(rows[i].Flags & ~InventorySlotRecord.WornFlagBit);
                rows[i] = rows[i] with { Flags = flags };
                last = rows[i];
            }

            return ValueTask.FromResult(last);
        }
    }

    private static void RequireUserNum(long userNum)
    {
        if (userNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(userNum), userNum, "user_num must be a positive login id.");
    }

    private static void RequireBag(InventoryBag bag)
    {
        if (bag is not InventoryBag.CharacterBag and not InventoryBag.EquippedBag)
            throw new ArgumentOutOfRangeException(nameof(bag), bag, "bag must be CharacterBag (802) or EquippedBag (803 container 1).");
    }

    private static void RequireBound(int maxCount, int cap, string name)
    {
        if (maxCount < 1 || maxCount > cap)
            throw new ArgumentOutOfRangeException(name, maxCount, $"maxCount must be 1..{cap}.");
    }
}
