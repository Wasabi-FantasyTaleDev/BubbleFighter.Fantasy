using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Lobby;

/// <summary>
/// The shared expiry sweep: deletes a player's ended time-limited rows and, when the worn character was
/// one of them, puts the default character on again so a seat never has to render a missing character.
/// </summary>
/// <param name="inventory">The store the rows are deleted from.</param>
/// <param name="selection">The exclusive-position rules used to wear the replacement; null falls back to the plain worn bit.</param>
/// <param name="starter">The build's starter kit; its worn character is the default character.</param>
/// <param name="clock">The time source deadlines are compared with.</param>
public sealed class InventoryExpirySweep(IInventoryRepository inventory, IEquipmentSelection? selection,
    StarterKit starter, TimeProvider clock) : IInventoryExpiry, IInventoryItemRemoval
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<InventorySlotRecord>?> RemoveItemAsync(long userNum, int itemId, CancellationToken ct)
    {
        var removed = await inventory.RemoveExpiredItemAsync(userNum, itemId, clock.GetUtcNow(), ct).ConfigureAwait(false);
        if (removed is { Count: > 0 })
            await RepairRemovedAsync(userNum, removed, ct).ConfigureAwait(false);
        else if (removed is { Count: 0 } && itemId is >= 300001 and <= 300031)
            // A previous attempt can commit deletion before its character repair fails or is cancelled.
            await WearDefaultCharacterAsync(userNum, ct).ConfigureAwait(false);
        return removed;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<int>> SweepAsync(long userNum, CancellationToken ct)
        => await SweepCoreAsync(userNum, null, ct).ConfigureAwait(false);

    internal ValueTask<IReadOnlyList<int>> SweepForLoginAsync(long userNum,
        Data.LoginInventoryResolver resolver, CancellationToken ct) => SweepCoreAsync(userNum, resolver, ct);

    private async ValueTask<IReadOnlyList<int>> SweepCoreAsync(long userNum,
        Data.LoginInventoryResolver? resolver, CancellationToken ct)
    {
        var removed = await inventory.RemoveExpiredAsync(userNum, clock.GetUtcNow(), ct).ConfigureAwait(false);
        // Partial or interrupted starter grants must be completed before a character repair validates every required position.
        if (resolver is not null)
            await resolver.PrepareAsync(userNum, ct).ConfigureAwait(false);
        // Deletion commits before repair, so a retry must repair even when no expired rows remain.
        // Ordinary sweeps leave never-seeded bags empty; login prepares the starter rows above before selecting equipment.
        if (removed.Count > 0 || !await OwnsNothingAsync(userNum, ct).ConfigureAwait(false))
            await WearDefaultCharacterAsync(userNum, ct).ConfigureAwait(false);
        return removed.Count == 0 ? [] : removed.Select(row => row.ItemId).Distinct().ToArray();
    }

    private ValueTask RepairRemovedAsync(long userNum, IReadOnlyList<InventorySlotRecord> removed, CancellationToken ct) =>
        // Only a removed worn character leaves the player without one; worn weapons simply become unworn.
        removed.Any(IsWornCharacter) ? WearDefaultCharacterAsync(userNum, ct) : ValueTask.CompletedTask;

    // ListSlotsAsync returns a complete snapshot; maxCount is a capacity bound, not a query limit.
    private async ValueTask<bool> OwnsNothingAsync(long userNum, CancellationToken ct) =>
        (await inventory.ListSlotsAsync(userNum, InventoryBag.CharacterBag,
            InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false)).Count == 0
        && (await inventory.ListSlotsAsync(userNum, InventoryBag.EquippedBag,
            InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false)).Count == 0;

    private static bool IsWornCharacter(InventorySlotRecord row) =>
        (row.Flags & InventorySlotRecord.WornFlagBit) != 0 && row.ItemId is >= 300001 and <= 300031;

    private async ValueTask WearDefaultCharacterAsync(long userNum, CancellationToken ct)
    {
        var bag = await inventory.ListSlotsAsync(userNum, InventoryBag.CharacterBag,
            InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
        if (bag.Any(IsWornCharacter))
            return;
        var target = starter.WornItemIds.FirstOrDefault(id => id is >= 300001 and <= 300031, StarterKit.DaoCharacterItemId);
        foreach (var kind in new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag })
        {
            var current = kind == InventoryBag.CharacterBag ? bag
                : await inventory.ListSlotsAsync(userNum, kind, InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
            if (current.Any(row => row.ItemId == target))
                continue;
            // Each bag append commits separately; a retry must complete a missing mirror before selecting it.
            var grant = (starter.Slots(userNum, kind, clock.GetUtcNow()).Concat(StarterKit.Default.Slots(userNum, kind, clock.GetUtcNow())))
                .First(row => row.ItemId == target) with { Flags = 0, Lifetime = InventoryLifetime.Permanent };
            await inventory.AppendMissingItemsAsync(userNum, kind, [grant], ct).ConfigureAwait(false);
        }
        if (selection is not null)
        {
            var selected = await selection.ApplyAsync(userNum, target, true, ct).ConfigureAwait(false);
            if (selected is null || !selected.Any(IsWornCharacter))
                throw new InvalidOperationException("The default-character equipment repair was refused or selected no character.");
        }
        else
        {
            var selected = await inventory.SetSlotWornAsync(userNum, target, true, ct).ConfigureAwait(false);
            if (selected is null || !IsWornCharacter(selected))
                throw new InvalidOperationException("The default character disappeared before its equipment repair completed.");
        }
    }
}
