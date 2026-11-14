namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>A catalog-resolved pet change committed with its consumable under the account inventory lock.</summary>
/// <param name="ExpectedSlot">The exact build-projected pet row used to plan this mutation.</param>
/// <param name="Scope">The client catalog and build selection used when checking ownership and equipment.</param>
/// <param name="Progression">The complete immutable state to save only under the selected client build.</param>
/// <param name="ConsumableItemId">One item to consume, or zero for an experience-funded level upgrade.</param>
/// <param name="RequireEquipped">Whether the native action requires the pet to be selected in this build.</param>
/// <param name="Now">Authoritative planning time used to reject expired ownership and consumables.</param>
public sealed record PetMutationCommand(InventorySlotRecord ExpectedSlot, InventoryLoadoutScope Scope,
    PetProgression Progression, int ConsumableItemId, bool RequireEquipped, DateTimeOffset Now)
{
    /// <summary>Gets ordinary Article initialization applied to the final rows, including newly granted mileage rewards.</summary>
    public ArticleInitializationCommand? ArticleInitialization { get; init; }

    /// <summary>Gets optional metadata initialization committed atomically with this pet and its consumable.</summary>
    public PetInitializationCommand? Initialization { get; init; }

    /// <summary>Gets an optional shared ordinary mileage balance and conversion effect committed with this mutation.</summary>
    public PetMileageEffect? Mileage { get; init; }

    /// <summary>Rejects invalid command shapes before acquiring a persistence transaction.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(ExpectedSlot);
        ArgumentNullException.ThrowIfNull(Scope);
        ArgumentNullException.ThrowIfNull(Progression);
        if (ExpectedSlot.UserNum <= 0 || ExpectedSlot.Bag != InventoryBag.CharacterBag || ExpectedSlot.SlotKey <= 0
            || ExpectedSlot.ItemId <= 0 || ConsumableItemId < 0 || ConsumableItemId == ExpectedSlot.ItemId
            || !Scope.Supports(ExpectedSlot.ItemId))
            throw new ArgumentException("Pet mutation identities or inventory scope are invalid.");
        ExpectedSlot.Pet?.Validate();
        ExpectedSlot.PetIdentity?.Validate();
        Progression.Validate();
        Initialization?.ValidateFor(ExpectedSlot.UserNum, Scope);
        ArticleInitialization?.ValidateFor(ExpectedSlot.UserNum, Scope);
        Mileage?.ValidateFor(this);
    }

    /// <summary>Plans all changed rows from one locked ownership and loadout snapshot without mutating the input.</summary>
    /// <param name="stored">Complete shared inventory under the account lock.</param>
    /// <param name="loadout">Current selection read under the same lock.</param>
    /// <returns>Changed shared rows, or null when ownership, food or expected pet state no longer matches.</returns>
    public IReadOnlyList<InventorySlotRecord>? Apply(IReadOnlyList<InventorySlotRecord> stored, InventoryLoadoutState loadout) =>
        Plan(stored, loadout)?.Rows;

    /// <summary>Stages pet, consumable, mileage and reward changes using one locked raw inventory and ordinary-key watermark.</summary>
    public PetMutationPlan? Plan(IReadOnlyList<InventorySlotRecord> stored, InventoryLoadoutState loadout, long lastSlotKey = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lastSlotKey);
        var changed = ApplyPetAndFood(stored, loadout);
        if (changed is null) return null;
        return Mileage is null ? new(changed, [], lastSlotKey) : Mileage.Plan(this, stored, changed, loadout, lastSlotKey);
    }

    private IReadOnlyList<InventorySlotRecord>? ApplyPetAndFood(IReadOnlyList<InventorySlotRecord> stored, InventoryLoadoutState loadout)
    {
        Validate();
        ArgumentNullException.ThrowIfNull(stored);
        PetEntitlementInventory.ValidateRows(ExpectedSlot.UserNum, stored);
        var projected = InventoryLoadouts.Project(stored, loadout, Scope);
        var targets = projected.Where(row => row.Bag == InventoryBag.CharacterBag && row.ItemId == ExpectedSlot.ItemId).Take(2).ToArray();
        if (targets.Length != 1 || targets[0] != ExpectedSlot || targets[0].Quantity <= 0
            || InventoryExpiry.IsExpired(targets[0], Now)
            || RequireEquipped && (targets[0].Flags & InventorySlotRecord.WornFlagBit) == 0)
            return null;
        PetEntitlementInventory.RequireMirrors(targets[0], projected.Where(row => row.Bag == InventoryBag.EquippedBag
            && row.ItemId == ExpectedSlot.ItemId).ToArray());
        InventorySlotRecord? food = null;
        if (ConsumableItemId != 0)
        {
            var foods = projected.Where(row => row.Bag == InventoryBag.CharacterBag && row.ItemId == ConsumableItemId).Take(2).ToArray();
            if (foods.Length != 1 || foods[0].Quantity <= 0 || foods[0].Lifetime.Kind == InventoryLifetimeKind.Life
                || InventoryExpiry.IsExpired(foods[0], Now)) return null;
            food = foods[0];
        }
        // Keep the shared worn flags intact; the expected row carries this build's projected selection.
        return stored.Select(row => row.ItemId == ExpectedSlot.ItemId
                ? row with { Pet = (row.Pet ?? PetProgressions.Empty).With(Scope.Key, Progression) }
                : food is not null && row.ItemId == food.ItemId ? row with { Quantity = food.Quantity - 1 } : row)
            .Where(row => row.Quantity > 0 || food is null || row.ItemId != food.ItemId).ToArray();
    }
}

/// <summary>Serializes pet progression, food consumption and build selection checks with purchases and inventory changes.</summary>
public interface IPetMutationStore
{
    /// <summary>Returns committed build-projected inventory, or null for a stale or unavailable plan; failures commit nothing.</summary>
    ValueTask<IReadOnlyList<InventorySlotRecord>?> MutatePetAsync(PetMutationCommand command, CancellationToken ct);

    /// <summary>Returns both build-projected bags from the mutation commit, or null when its expected ownership is stale.</summary>
    ValueTask<PetInventorySnapshot?> MutatePetSnapshotAsync(PetMutationCommand command, CancellationToken ct);

    /// <summary>Returns both committed bags and actual mileage reward identities, or null when any atomic precondition is refused.</summary>
    ValueTask<PetMutationResult?> MutatePetWithResultAsync(PetMutationCommand command, CancellationToken ct) =>
        throw new NotSupportedException("This pet store does not return committed mileage reward identities.");
}
