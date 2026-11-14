using System.Collections.Frozen;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Pets.Data;

namespace BubbleFighter.Fantasy.Pets.Mileage;

/// <summary>Plans explicit mileage gains and catalog rewards against the same bag used for pet mutation.</summary>
internal sealed class PetMileagePolicy
{
    // kr-1256 sub_EC9F30 and cn-774 sub_7B7230 read this ordinary inventory balance for every pet.
    internal const int BalanceItemId = 103385;
    private readonly FrozenDictionary<int, int> _gains;
    private readonly InventoryGrantTerm[] _rewards;
    private readonly int _maximum;
    private readonly long _slotKeyBase;
    private readonly int _threshold;
    private readonly int _maximumConversions;
    private readonly PetMileageConversionMode? _mode;
    private readonly PetMileageRemainder? _remainder;

    private PetMileagePolicy(PetMileageOptions options, PetCatalog catalog, InventoryLoadoutScope scope)
    {
        var errors = options.Validate().ToArray();
        if (errors.Length != 0) throw new ConfigException(string.Join(" ", errors.Select(error => error.ErrorMessage)));
        if (!catalog.Items.TryGetValue(BalanceItemId, out var balance) || balance.Function != 1114
            || balance.DurationType != "count" || balance.MaximumDuration is not > 0 || !scope.Supports(BalanceItemId))
            throw new ConfigException("Pet mileage requires this client's native ordinary count balance item and quantity limit.");
        _maximum = balance.MaximumDuration.Value;
        DisplayMaximum = options.DisplayMaximum!.Value;
        if (DisplayMaximum > _maximum)
            throw new ConfigException("Pet mileage display maximum exceeds the native count limit.");
        _slotKeyBase = options.SlotKeyBase;
        foreach (var grant in options.Grants)
            if (!scope.Supports(grant.ConsumableItemId)
                || !catalog.Items.TryGetValue(grant.ConsumableItemId, out var consumable)
                || consumable.Function is not (21 or 39) || grant.Quantity!.Value > _maximum)
                throw new ConfigException("Pet mileage grants must identify available reset consumables and fit the native balance limit.");
        _gains = options.Grants.ToFrozenDictionary(grant => grant.ConsumableItemId, grant => grant.Quantity!.Value);
        if (catalog.Items.Values.Any(item => scope.Supports(item.ItemId) && item.Function is 21 or 39 && !_gains.ContainsKey(item.ItemId)))
            throw new ConfigException("Every native pet reset consumable requires an explicit mileage gain, including a deliberate zero.");
        if (options.Conversion is not { } conversion)
        {
            _rewards = [];
            return;
        }
        _threshold = conversion.Threshold;
        _maximumConversions = conversion.MaximumPerOperation;
        _mode = conversion.Mode;
        _remainder = conversion.Remainder;
        if (_threshold > _maximum || conversion.RequireDisplayMaximumMatch && _threshold != DisplayMaximum)
            throw new ConfigException("Pet mileage threshold exceeds its balance bound or differs from the required display maximum.");
        _rewards = conversion.Rewards.Select(reward =>
        {
            if (!scope.Supports(reward.ItemId) || !catalog.Items.TryGetValue(reward.ItemId, out var item)
                || item.Function == PetCatalog.PetFunction || item.ItemId == BalanceItemId || _gains.ContainsKey(item.ItemId)
                || item.MaximumDuration is not > 0)
                throw new ConfigException("Mileage rewards must be ordinary catalog items distinct from mileage and reset consumables.");
            var compatible = reward.Kind switch
            {
                InventoryGrantKind.Count => item.DurationType == "count",
                InventoryGrantKind.Days or InventoryGrantKind.Permanent => item.DurationType == "day",
                InventoryGrantKind.Life => item.DurationType == "endurance",
                _ => false,
            };
            if (!compatible || reward.Amount > item.MaximumDuration || reward.Kind == InventoryGrantKind.Permanent && reward.Amount != 1)
                throw new ConfigException("Mileage reward amount or ownership lifetime disagrees with its native catalog terms.");
            return new InventoryGrantTerm(item.ItemId, reward.Kind!.Value, reward.Amount, item.MaximumDuration.Value, false);
        }).ToArray();
    }

    internal int DisplayMaximum { get; }

    internal static PetMileagePolicy? Create(PetsOptions options, PetCatalog catalog, InventoryLoadoutScope scope)
    {
        if (options.Mileage.Enabled) return new(options.Mileage, catalog, scope);
        if (options.SkillResetsEnabled && catalog.Items.TryGetValue(BalanceItemId, out var balance) && balance.Function == 1114)
            throw new ConfigException("Skill resets in a mileage-capable client require an enabled, complete mileage policy.");
        return null;
    }

    internal PetMileageEffect Plan(long userNum, int consumableItemId,
        IReadOnlyList<InventorySlotRecord> rows, DateTimeOffset now)
    {
        if (!_gains.TryGetValue(consumableItemId, out var gain))
            throw new InvalidOperationException("The reset consumable has no configured mileage gain.");
        var matches = rows.Where(row => row.ItemId == BalanceItemId).Take(2).ToArray();
        if (matches.Length > 1) throw new InventoryGrantRejectedException("Mileage balance has duplicate rows.");
        var existing = matches.SingleOrDefault();
        if (existing is not null && (existing.Quantity < 0 || existing.Quantity > _maximum
            || existing.Lifetime.Kind is not (InventoryLifetimeKind.Unspecified or InventoryLifetimeKind.Permanent)
            || existing.Pet is not null || existing.PetIdentity is not null))
            throw new InventoryGrantRejectedException("Mileage balance has incompatible ownership metadata.");
        var total = checked((long)(existing?.Quantity ?? 0) + gain);
        if (total > int.MaxValue)
            throw new InventoryGrantRejectedException("Mileage would overflow its native quantity before conversion.");
        PetMileageConversion? conversion = null;
        if (_mode is not null && total >= _threshold)
        {
            var count = _mode == PetMileageConversionMode.Single ? 1L : total / _threshold;
            if (count > _maximumConversions)
                throw new InventoryGrantRejectedException("Mileage conversion exceeds the configured operation budget.");
            var remaining = _remainder == PetMileageRemainder.Discard ? 0 : total - checked(count * _threshold);
            if (remaining > _maximum)
                throw new InventoryGrantRejectedException("Mileage would exceed its native balance limit.");
            if (_rewards.Any(reward => reward.Amount * count > int.MaxValue))
                throw new InventoryGrantRejectedException("Mileage rewards would overflow their native quantity.");
            var terms = _rewards.Select(reward => reward with { Amount = checked((int)(reward.Amount * count)) }).ToArray();
            var grants = InventoryGrantPlanner.Plan(userNum, terms, rows, now);
            conversion = new(checked((int)count), _maximumConversions, _threshold,
                _remainder == PetMileageRemainder.Discard, checked((int)remaining), grants);
        }
        else if (total > _maximum)
            throw new InventoryGrantRejectedException("Mileage would exceed its native balance limit.");
        return new(BalanceItemId, existing, gain, _maximum, _slotKeyBase, conversion);
    }
}
