using BubbleFighter.Fantasy.Contracts.Shop;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Supplies a policy-selected conversion without choosing native gain, threshold, reward or remainder rules.</summary>
/// <param name="Count">Number of conversions explicitly planned by the policy.</param>
/// <param name="MaximumCount">Configured upper bound on conversions in this mutation.</param>
/// <param name="CostPerConversion">Positive mileage spent by each planned conversion.</param>
/// <param name="DiscardRemainder">Whether the policy explicitly discards the affordable conversion's remainder.</param>
/// <param name="BalanceAfterConversion">Expected remaining balance: zero when discarded, otherwise the exact subtraction.</param>
/// <param name="Rewards">Distinct catalog-resolved ordinary grants, already aggregated for the planned conversions.</param>
public sealed record PetMileageConversion(int Count, int MaximumCount, int CostPerConversion,
    bool DiscardRemainder, int BalanceAfterConversion, IReadOnlyList<ShopPurchaseGrant> Rewards)
{
    /// <summary>Gets an immutable copy of the bounded grant vector used by the account transaction.</summary>
    public IReadOnlyList<ShopPurchaseGrant> Rewards { get; } = CopyRewards(Rewards);

    private static IReadOnlyList<ShopPurchaseGrant> CopyRewards(IReadOnlyList<ShopPurchaseGrant> rewards)
    {
        ArgumentNullException.ThrowIfNull(rewards);
        if (rewards.Count is < 1 or > InventoryLimits.MaxInventorySlotsPerBag || rewards.Any(reward => reward is null))
            throw new ArgumentException("Mileage conversion requires a bounded nonempty reward vector.", nameof(rewards));
        return Array.AsReadOnly(rewards.ToArray());
    }
}

/// <summary>Changes one shared ordinary count item in the same transaction as pet growth and consumable use.</summary>
/// <param name="ItemId">The ordinary balance item proven and resolved by the owning build's catalog policy.</param>
/// <param name="ExpectedSlot">Exact build-projected balance row, or null when the policy observed no balance row.</param>
/// <param name="Gain">Explicit nonnegative amount added before any planned conversion.</param>
/// <param name="MaximumBalance">Configured upper bound on the committed ordinary item quantity.</param>
/// <param name="SlotKeyBase">Configured lower bound for newly allocated ordinary inventory slot keys.</param>
/// <param name="Conversion">Optional explicit conversion and its catalog-resolved rewards.</param>
public sealed record PetMileageEffect(int ItemId, InventorySlotRecord? ExpectedSlot, int Gain, int MaximumBalance,
    long SlotKeyBase, PetMileageConversion? Conversion)
{
    /// <summary>Checks catalog boundaries, disjoint item roles and ordinary conditional grants before storage access.</summary>
    public void ValidateFor(PetMutationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var initialization = command.Initialization
            ?? throw new ArgumentException("Mileage requires the selected build's complete pet catalog initialization.");
        if (ItemId <= 0 || Gain < 0 || MaximumBalance <= 0 || SlotKeyBase <= 0 || !command.Scope.Supports(ItemId)
            || initialization.IsPetItem(ItemId) || ItemId == command.ExpectedSlot.ItemId || ItemId == command.ConsumableItemId)
            throw new ArgumentException("Mileage item, amount, bound or inventory role is invalid.");
        ValidateExpected(ExpectedSlot, ItemId, command);
        if (ExpectedSlot is { } balance && (balance.Quantity < 0
            || balance.Lifetime.Kind is not (InventoryLifetimeKind.Unspecified or InventoryLifetimeKind.Permanent)))
            throw new ArgumentException("Mileage must be an ordinary nonnegative count row.");
        if (Conversion is not { } conversion) return;
        if (conversion.Count <= 0 || conversion.MaximumCount <= 0 || conversion.Count > conversion.MaximumCount
            || conversion.CostPerConversion <= 0 || conversion.BalanceAfterConversion < 0
            || conversion.BalanceAfterConversion > MaximumBalance
            || conversion.Rewards.Select(reward => reward.ItemId).Distinct().Count() != conversion.Rewards.Count)
            throw new ArgumentException("Mileage conversion amounts or distinct reward bounds are invalid.");
        foreach (var reward in conversion.Rewards)
        {
            if (reward.ItemId <= 0 || reward.Quantity <= 0 || reward.Mode != ShopGrantMode.MatchItem
                || reward.Lifetime.Kind == InventoryLifetimeKind.Unspecified || !command.Scope.Supports(reward.ItemId)
                || initialization.IsPetItem(reward.ItemId) || reward.ItemId == ItemId
                || reward.ItemId == command.ExpectedSlot.ItemId || reward.ItemId == command.ConsumableItemId)
                throw new ArgumentException("Mileage rewards must be distinct supported ordinary conditional grants.");
            ValidateExpected(reward.ExpectedSlot, reward.ItemId, command);
        }
    }

    internal PetMutationPlan? Plan(PetMutationCommand command, IReadOnlyList<InventorySlotRecord> stored,
        IReadOnlyList<InventorySlotRecord> petChanges, InventoryLoadoutState loadout, long lastSlotKey)
    {
        var projected = InventoryLoadouts.Project(stored, loadout, command.Scope);
        if (!Matches(projected, ItemId, ExpectedSlot)) return null;
        if (Conversion is { } conversion && conversion.Rewards.Any(reward => !Matches(projected, reward.ItemId, reward.ExpectedSlot)))
            return null;
        // StarterKit rows share one marker per bag; new grants still allocate above every existing key.
        if (stored.Any(row => row.SlotKey <= 0)) return null;
        try
        {
            var balance = checked((ExpectedSlot?.Quantity ?? 0) + Gain);
            if (Conversion is { } planned)
            {
                var cost = checked(planned.Count * planned.CostPerConversion);
                if (cost > balance) return null;
                var remaining = planned.DiscardRemainder ? 0 : balance - cost;
                if (remaining != planned.BalanceAfterConversion) return null;
                balance = remaining;
            }
            if (balance > MaximumBalance) return null;
            var highest = Math.Max(lastSlotKey, stored.Count == 0 ? 0 : stored.Max(row => row.SlotKey));
            var rows = petChanges.ToList();
            // kr-1256 sub_5E6C30, kr-1256 sub_EC9F30 and kr-1256 sub_ECE8D0 read the shared item balance.
            // cn-774 sub_73F4E0, cn-774 sub_7B7230 and cn-774 sub_7BC4E0 use that ordinary representation too.
            if ((ExpectedSlot is not null || balance != 0)
                && !ApplyGrant(rows, command.ExpectedSlot.UserNum, ItemId, balance, InventoryLifetime.Permanent,
                    ExpectedSlot, ref highest, balanceItem: true, command.Now)) return null;
            if (Conversion is { } rewards)
                foreach (var reward in rewards.Rewards)
                    if (!ApplyGrant(rows, command.ExpectedSlot.UserNum, reward.ItemId, reward.Quantity, reward.Lifetime,
                        reward.ExpectedSlot, ref highest, balanceItem: false, command.Now)) return null;
            if (rows.GroupBy(row => row.Bag).Any(bag => bag.Count() > InventoryLimits.MaxInventorySlotsPerBag)) return null;
            return new(rows, Conversion?.Rewards.Select(reward => reward.ItemId).ToArray() ?? [], highest);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private bool ApplyGrant(List<InventorySlotRecord> rows, long userNum, int itemId, int quantity,
        InventoryLifetime lifetime, InventorySlotRecord? expected, ref long highest, bool balanceItem, DateTimeOffset now)
    {
        var existing = rows.SingleOrDefault(row => row.Bag == InventoryBag.CharacterBag && row.ItemId == itemId);
        var mirrors = rows.Where(row => row.Bag == InventoryBag.EquippedBag && row.ItemId == itemId).ToArray();
        if (mirrors.Length > 1 || mirrors.Any(row => row.Pet is not null || row.PetIdentity is not null)
            || existing is not null && (existing.Pet is not null || existing.PetIdentity is not null)
            || !balanceItem && !CanGrant(existing, quantity, lifetime, now)) return false;
        if (expected is null)
        {
            highest = Math.Max(SlotKeyBase, checked(highest + 1));
            rows.Add(new(userNum, InventoryBag.CharacterBag, highest, itemId, quantity, 0) { Lifetime = lifetime });
        }
        for (var index = 0; index < rows.Count; index++)
            if (rows[index].ItemId == itemId)
                rows[index] = rows[index] with { Quantity = quantity, Lifetime = lifetime };
        return true;
    }

    private static bool CanGrant(InventorySlotRecord? existing, int quantity, InventoryLifetime lifetime, DateTimeOffset now)
    {
        if (existing is { Quantity: < 0 } && existing.Lifetime.Kind != InventoryLifetimeKind.Life) return false;
        return lifetime.Kind switch
        {
            InventoryLifetimeKind.Permanent => existing is null
                || (existing.Lifetime.Kind is InventoryLifetimeKind.Unspecified or InventoryLifetimeKind.Permanent) && quantity > existing.Quantity
                || existing.Lifetime.Kind == InventoryLifetimeKind.ExpiresAt && quantity == 1,
            InventoryLifetimeKind.ExpiresAt => quantity == 1 && lifetime.ExpiresAtUnixMilliseconds > now.ToUnixTimeMilliseconds()
                && (existing is null || existing.Lifetime.Kind == InventoryLifetimeKind.ExpiresAt
                    && lifetime.ExpiresAtUnixMilliseconds > existing.Lifetime.ExpiresAtUnixMilliseconds),
            InventoryLifetimeKind.Life => existing is null || existing.Lifetime.Kind == InventoryLifetimeKind.Life && existing.Quantity <= 0,
            _ => false,
        };
    }

    private static bool Matches(IReadOnlyList<InventorySlotRecord> rows, int itemId, InventorySlotRecord? expected)
    {
        var matches = rows.Where(row => row.Bag == InventoryBag.CharacterBag && row.ItemId == itemId).Take(2).ToArray();
        return expected is null ? matches.Length == 0 : matches.Length == 1 && matches[0] == expected;
    }

    private static void ValidateExpected(InventorySlotRecord? expected, int itemId, PetMutationCommand command)
    {
        if (expected is not null && (expected.UserNum != command.ExpectedSlot.UserNum || expected.ItemId != itemId
            || expected.Bag != InventoryBag.CharacterBag || expected.SlotKey <= 0
            || expected.Pet is not null || expected.PetIdentity is not null))
            throw new ArgumentException("Mileage and reward preconditions must name ordinary rows of this player and item.");
    }
}

/// <summary>Owns a staged raw inventory and bounded reward identities before a persistence transaction commits.</summary>
public sealed class PetMutationPlan
{
    internal PetMutationPlan(IReadOnlyList<InventorySlotRecord> rows, IReadOnlyList<int> rewardItemIds, long lastSlotKey)
    {
        Rows = Array.AsReadOnly(rows.ToArray());
        RewardItemIds = Array.AsReadOnly(rewardItemIds.ToArray());
        LastSlotKey = lastSlotKey;
    }

    /// <summary>Gets both raw bags, retaining shared stored flags and immutable pet metadata.</summary>
    public IReadOnlyList<InventorySlotRecord> Rows { get; }

    /// <summary>Gets one item ID for each distinct ordinary reward grant in this plan.</summary>
    public IReadOnlyList<int> RewardItemIds { get; }

    /// <summary>Gets the highest observed or newly allocated ordinary slot key under the account lock.</summary>
    public long LastSlotKey { get; }
}

/// <summary>Returns the complete committed inventory and the ordinary reward grants that actually succeeded.</summary>
public sealed class PetMutationResult
{
    /// <summary>Copies bounded distinct reward identities beside the immutable snapshot returned by the commit.</summary>
    public PetMutationResult(PetInventorySnapshot snapshot, IReadOnlyList<int> rewardItemIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rewardItemIds);
        if (rewardItemIds.Count > InventoryLimits.MaxInventorySlotsPerBag || rewardItemIds.Any(id => id <= 0)
            || rewardItemIds.Distinct().Count() != rewardItemIds.Count)
            throw new ArgumentException("Committed mileage notifications require bounded distinct positive item IDs.", nameof(rewardItemIds));
        Snapshot = snapshot;
        RewardItemIds = Array.AsReadOnly(rewardItemIds.ToArray());
    }

    /// <summary>Gets both build-projected bags from the same successful transaction.</summary>
    public PetInventorySnapshot Snapshot { get; }

    /// <summary>Gets one ordinary item ID per committed reward grant, with no IDs from refused or unplanned conversions.</summary>
    public IReadOnlyList<int> RewardItemIds { get; }
}
