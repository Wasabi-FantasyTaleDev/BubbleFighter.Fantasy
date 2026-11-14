using BubbleFighter.Fantasy.Contracts.Wallet;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Plans one exact equipment modification and its material and Lucci debits under the account transaction.</summary>
/// <param name="ExpectedTarget">Owned, unequipped permanent Article row that must still match at commit.</param>
/// <param name="Scope">Catalog and loadout scope owning the modification selectors.</param>
/// <param name="MaterialDebits">Aggregated exact consumable rows and quantities.</param>
/// <param name="AddedAttribute">Resolved attribute to install, or null when deleting an installed group.</param>
/// <param name="DeletedGroup">Installed group to delete, or null when adding an attribute.</param>
/// <param name="LucciCost">Explicit nonnegative operation price; persistence does not infer it.</param>
/// <param name="Now">Authoritative time for ownership expiry checks.</param>
public sealed record ItemModificationCommand(InventorySlotRecord ExpectedTarget, InventoryLoadoutScope Scope,
    IReadOnlyList<ItemEnhancementDebit> MaterialDebits, ItemModificationAttribute? AddedAttribute,
    int? DeletedGroup, int LucciCost, DateTimeOffset Now)
{
    /// <summary>Gets an immutable copy of all planned material debits.</summary>
    public IReadOnlyList<ItemEnhancementDebit> MaterialDebits { get; } = Array.AsReadOnly(MaterialDebits.ToArray());

    /// <summary>Gets metadata preparation to commit with the final complete equipment inventory.</summary>
    public ArticleInitializationCommand? ArticleInitialization { get; init; }

    /// <summary>Gets pet preparation to commit with the same inventory transaction.</summary>
    public PetInitializationCommand? PetInitialization { get; init; }

    /// <summary>
    /// Gets the Favorite weapon points added to the target when an attribute is installed; zero awards nothing, and a deletion never
    /// awards them. The points commit in the same row mutation as the installed group.
    /// </summary>
    public int FavoritePoints { get; init; }

    /// <summary>Gets the authenticated account that owns the exact target.</summary>
    public long UserNum => ExpectedTarget.UserNum;

    /// <summary>Rejects unsupported operations, duplicate debits and ownership overlap before opening a transaction.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(ExpectedTarget);
        ArgumentNullException.ThrowIfNull(Scope);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(UserNum);
        if ((AddedAttribute is null) == (DeletedGroup is null) || DeletedGroup is < 0 or > 2 || LucciCost < 0
            || MaterialDebits.Count > InventoryLimits.MaxInventorySlotsPerBag)
            throw new ArgumentException("Modification plan has an invalid operation, cost or bound.");
        if (ExpectedTarget.Bag != InventoryBag.CharacterBag || ExpectedTarget.SlotKey <= 0 || ExpectedTarget.ItemId <= 0
            || !Scope.Supports(ExpectedTarget.ItemId) || ExpectedTarget.PetIdentity is not null
            || InventoryArticleIdentityResolver.Of(ExpectedTarget) is null)
            throw new ArgumentException("Modification requires an exact ordinary equipment Article.");
        ArgumentOutOfRangeException.ThrowIfNegative(FavoritePoints);
        if (FavoritePoints > 0 && AddedAttribute is null) throw new ArgumentException("Only an installed modification can award Favorite points.");
        AddedAttribute?.Validate();
        ExpectedTarget.Modifications?.Validate();
        ArticleInitialization?.ValidateFor(UserNum, Scope);
        PetInitialization?.ValidateFor(UserNum, Scope);
        if (MaterialDebits.Any(debit => debit is null || debit.ExpectedSlot is null || debit.Amount <= 0
                || debit.ExpectedSlot.UserNum != UserNum || debit.ExpectedSlot.Bag != InventoryBag.CharacterBag
                || debit.ExpectedSlot.SlotKey <= 0 || !Scope.Supports(debit.ExpectedSlot.ItemId)
                || InventoryArticleIdentityResolver.Of(debit.ExpectedSlot) is not null
                || debit.ExpectedSlot.ItemId == ExpectedTarget.ItemId)
            || MaterialDebits.Select(debit => debit.ExpectedSlot.ItemId).Distinct().Count() != MaterialDebits.Count)
            throw new ArgumentException("Modification material ownership is invalid, duplicated or overlaps the target.");
    }

    /// <summary>Calculates the complete new inventory or refuses a stale plan without changing any input.</summary>
    public IReadOnlyList<InventorySlotRecord>? Apply(IReadOnlyList<InventorySlotRecord> stored, InventoryLoadoutState loadout)
    {
        Validate();
        InventoryArticles.ValidateRows(UserNum, stored);
        var projected = InventoryLoadouts.Project(stored, loadout, Scope);
        var targets = projected.Where(row => row.Bag == InventoryBag.CharacterBag && row.ItemId == ExpectedTarget.ItemId).Take(2).ToArray();
        if (targets.Length != 1 || targets[0] != ExpectedTarget || ExpectedTarget.Quantity <= 0
            || ExpectedTarget.Lifetime.Kind != InventoryLifetimeKind.Permanent
            || (ExpectedTarget.Flags & InventorySlotRecord.WornFlagBit) != 0) return null;
        InventoryArticles.RequireMirrors(ExpectedTarget,
            projected.Where(row => row.Bag == InventoryBag.EquippedBag && row.ItemId == ExpectedTarget.ItemId).ToArray());
        var prior = ExpectedTarget.Modifications?.Find(Scope.Key);
        var attributes = prior?.Attributes.ToList() ?? [];
        if (AddedAttribute is { } addition)
        {
            if (attributes.Count >= 3 || attributes.Any(attribute => attribute.Group == addition.Group)) return null;
            attributes.Add(addition);
        }
        else if (attributes.RemoveAll(attribute => attribute.Group == DeletedGroup) != 1) return null;
        foreach (var debit in MaterialDebits)
        {
            var matches = projected.Where(row => row.Bag == InventoryBag.CharacterBag && row.ItemId == debit.ExpectedSlot.ItemId).Take(2).ToArray();
            if (matches.Length != 1 || matches[0] != debit.ExpectedSlot || matches[0].Quantity < debit.Amount
                || matches[0].Lifetime.Kind == InventoryLifetimeKind.Life || InventoryExpiry.IsExpired(matches[0], Now)) return null;
            InventoryArticles.RequireMirrors(matches[0],
                projected.Where(row => row.Bag == InventoryBag.EquippedBag && row.ItemId == matches[0].ItemId).ToArray());
        }
        var state = new ItemModificationState(checked((prior?.Revision ?? 0) + 1), attributes);
        var debits = MaterialDebits.ToDictionary(debit => debit.ExpectedSlot.ItemId);
        var changed = new List<InventorySlotRecord>(stored.Count);
        foreach (var row in stored)
        {
            if (row.ItemId == ExpectedTarget.ItemId)
            {
                var modified = row with { Modifications = (row.Modifications ?? ItemModifications.Empty).With(Scope.Key, state) };
                // kr-1256 sub_5EE200 shows the points kept in the slot's attribute vector, so they commit with the same row.
                if (FavoritePoints > 0)
                    modified = modified with { Favorites = (row.Favorites ?? ItemFavorites.Empty).With(Scope.Key,
                        checked((row.Favorites?.Find(Scope.Key) ?? 0) + FavoritePoints)) };
                changed.Add(modified);
            }
            else if (debits.TryGetValue(row.ItemId, out var debit))
            {
                var remaining = debit.ExpectedSlot.Quantity - debit.Amount;
                if (remaining > 0) changed.Add(row with { Quantity = remaining });
            }
            else changed.Add(row);
        }
        InventoryArticles.ValidateRows(UserNum, changed);
        return changed;
    }
}

/// <summary>Returns the inventory and wallet from the same committed modification transaction.</summary>
/// <param name="Inventory">Both complete build-projected bags including all initialized metadata.</param>
/// <param name="Wallet">The committed wallet after the operation's Lucci debit.</param>
public sealed record ItemModificationResult(InventorySnapshot Inventory, PlayerWallet Wallet);

/// <summary>Serializes modification state, material consumption and Lucci costs with other account writes.</summary>
public interface IItemModificationStore
{
    /// <summary>Commits the exact plan once, or returns null for stale ownership or insufficient materials or Lucci.</summary>
    ValueTask<ItemModificationResult?> TryModifyAsync(ItemModificationCommand command, CancellationToken ct);
}
