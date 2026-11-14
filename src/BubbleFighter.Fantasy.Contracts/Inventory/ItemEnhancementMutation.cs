namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Describes an exact expected consumable row and the configured quantity to remove atomically.</summary>
/// <param name="ExpectedSlot">Committed build-projected material ownership used when planning the request.</param>
/// <param name="Amount">Positive recipe debit for this planned outcome.</param>
public sealed record ItemEnhancementDebit(InventorySlotRecord ExpectedSlot, int Amount);

/// <summary>Commits a prevalidated enhancement outcome with every material debit under one account lock.</summary>
/// <param name="ExpectedTargets">Exact unequipped permanent Article ownership rows, including every member of a set.</param>
/// <param name="Scope">Native catalog and equipment selection scope.</param>
/// <param name="MaterialDebits">Exact material rows and outcome-specific quantities selected by the recipe policy.</param>
/// <param name="EnhancementKind">The earned semantic enhancement if this attempt succeeds.</param>
/// <param name="ApplyEnhancement">Whether the preplanned roll succeeded; persistence never rolls or retries it.</param>
/// <param name="Now">Authoritative time used to reject ended material ownership.</param>
public sealed record ItemEnhancementCommand(IReadOnlyList<InventorySlotRecord> ExpectedTargets, InventoryLoadoutScope Scope,
    IReadOnlyList<ItemEnhancementDebit> MaterialDebits, ItemEnhancementKind EnhancementKind, bool ApplyEnhancement, DateTimeOffset Now)
{
    /// <summary>Gets equipment identity initialization required by this transaction's complete inventory publication.</summary>
    public ArticleInitializationCommand? ArticleInitialization { get; init; }

    /// <summary>Gets pet identity and progression initialization committed with the same outcome and material debits.</summary>
    public PetInitializationCommand? PetInitialization { get; init; }

    /// <summary>
    /// Gets the Favorite weapon points added to every target in this scope when a Weapon enhancement succeeds; zero awards
    /// nothing. The points commit with the revision and material debits, and a failed attempt never awards them.
    /// </summary>
    public int FavoritePoints { get; init; }

    /// <summary>
    /// Gets the native ItemAttr selector of a laboratory grade-up, or zero for an ordinary enhancement attempt. A grade-up
    /// command lists every member of one native set as its targets and installs the selector on the first of them only, replacing
    /// any selector it carried; it requires <see cref="ItemEnhancementKind.None"/>, no applied enhancement and no Favorite points,
    /// leaves each member's earned enhancement as it was, and debits its materials in the same transaction.
    /// </summary>
    public int Advancement { get; init; }

    /// <summary>Gets copied target rows that cannot be replaced during the transaction.</summary>
    public IReadOnlyList<InventorySlotRecord> ExpectedTargets { get; } = Array.AsReadOnly(ExpectedTargets.ToArray());

    /// <summary>Gets copied recipe debits that cannot be changed during the transaction.</summary>
    public IReadOnlyList<ItemEnhancementDebit> MaterialDebits { get; } = Array.AsReadOnly(MaterialDebits.ToArray());

    /// <summary>Gets the authenticated owner shared by every expected row.</summary>
    public long UserNum => ExpectedTargets.Count == 0 ? 0 : ExpectedTargets[0]?.UserNum ?? 0;

    /// <summary>Rejects unsupported, ambiguous or overlapping plan identities before a persistence transaction is opened.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Scope);
        if (ExpectedTargets.Count is < 1 or > InventoryLimits.MaxInventorySlotsPerBag
            || MaterialDebits.Count > InventoryLimits.MaxInventorySlotsPerBag
            || (Advancement == 0 ? EnhancementKind is not (ItemEnhancementKind.Weapon or ItemEnhancementKind.Clothing)
                : EnhancementKind != ItemEnhancementKind.None || ApplyEnhancement || FavoritePoints != 0))
            throw new ArgumentException("Enhancement plan exceeds its bound or has an unknown result kind.");
        ArgumentOutOfRangeException.ThrowIfNegative(Advancement);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(UserNum);
        ArgumentOutOfRangeException.ThrowIfNegative(FavoritePoints);
        ArticleInitialization?.ValidateFor(UserNum, Scope);
        PetInitialization?.ValidateFor(UserNum, Scope);
        if (ExpectedTargets.Any(row => row is null || row.UserNum != UserNum || row.Bag != InventoryBag.CharacterBag
                || row.SlotKey <= 0 || row.ItemId <= 0 || !Scope.Supports(row.ItemId)
                || InventoryArticleIdentityResolver.Of(row) is null)
            || ExpectedTargets.Select(row => row.ItemId).Distinct().Count() != ExpectedTargets.Count
            || ExpectedTargets.Select(InventoryArticleIdentityResolver.Of).Distinct().Count() != ExpectedTargets.Count
            || MaterialDebits.Any(debit => debit is null || debit.ExpectedSlot is null || debit.Amount <= 0
                || debit.ExpectedSlot.UserNum != UserNum || debit.ExpectedSlot.Bag != InventoryBag.CharacterBag
                || debit.ExpectedSlot.SlotKey <= 0 || debit.ExpectedSlot.ItemId <= 0 || !Scope.Supports(debit.ExpectedSlot.ItemId)
                || InventoryArticleIdentityResolver.Of(debit.ExpectedSlot) is not null
                || ExpectedTargets.Any(target => target.ItemId == debit.ExpectedSlot.ItemId))
            || MaterialDebits.Select(debit => debit.ExpectedSlot.ItemId).Distinct().Count() != MaterialDebits.Count)
            throw new ArgumentException("Enhancement plan contains invalid or overlapping ownership references.");
        foreach (var target in ExpectedTargets)
        {
            InventoryArticleIdentityResolver.Of(target)!.Validate();
            target.Enhancements?.Validate();
        }
    }

    /// <summary>Produces a complete changed inventory, or refuses the entire stale or ineligible plan without changing input.</summary>
    public IReadOnlyList<InventorySlotRecord>? Apply(IReadOnlyList<InventorySlotRecord> stored, InventoryLoadoutState loadout)
    {
        Validate();
        InventoryArticles.ValidateRows(UserNum, stored);
        var projected = InventoryLoadouts.Project(stored, loadout, Scope);
        foreach (var expected in ExpectedTargets)
        {
            var matches = projected.Where(row => row.Bag == InventoryBag.CharacterBag && row.ItemId == expected.ItemId).Take(2).ToArray();
            if (matches.Length != 1 || matches[0] != expected || expected.Quantity <= 0
                || expected.Lifetime.Kind != InventoryLifetimeKind.Permanent || (expected.Flags & InventorySlotRecord.WornFlagBit) != 0
                || Advancement == 0 && expected.Enhancements?.Find(Scope.Key)?.Kind is ItemEnhancementKind.Weapon or ItemEnhancementKind.Clothing)
                return null;
            InventoryArticles.RequireMirrors(expected, projected.Where(row => row.Bag == InventoryBag.EquippedBag && row.ItemId == expected.ItemId).ToArray());
        }
        foreach (var debit in MaterialDebits)
        {
            var matches = projected.Where(row => row.Bag == InventoryBag.CharacterBag && row.ItemId == debit.ExpectedSlot.ItemId).Take(2).ToArray();
            if (matches.Length != 1 || matches[0] != debit.ExpectedSlot || matches[0].Quantity < debit.Amount
                || matches[0].Lifetime.Kind == InventoryLifetimeKind.Life || InventoryExpiry.IsExpired(matches[0], Now)) return null;
            InventoryArticles.RequireMirrors(matches[0], projected.Where(row => row.Bag == InventoryBag.EquippedBag && row.ItemId == matches[0].ItemId).ToArray());
        }
        var targets = ExpectedTargets.ToDictionary(row => row.ItemId);
        var debits = MaterialDebits.ToDictionary(debit => debit.ExpectedSlot.ItemId);
        var changed = new List<InventorySlotRecord>(stored.Count);
        foreach (var row in stored)
        {
            if (targets.TryGetValue(row.ItemId, out var target))
            {
                var prior = target.Enhancements?.Find(Scope.Key);
                if (Advancement != 0)
                {
                    // The grade-up rides on the first member; the others keep their rows, and a repeated selector is a stale plan.
                    if (!ReferenceEquals(target, ExpectedTargets[0])) { changed.Add(row); continue; }
                    if (prior?.Advancement == Advancement) return null;
                    changed.Add(row with { Enhancements = (row.Enhancements ?? ItemEnhancements.Empty).With(Scope.Key,
                        new ItemEnhancementState(prior?.Kind ?? ItemEnhancementKind.None, checked((prior?.Revision ?? 0) + 1), Advancement)) });
                    continue;
                }
                var state = new ItemEnhancementState(ApplyEnhancement ? EnhancementKind : ItemEnhancementKind.None,
                    checked((prior?.Revision ?? 0) + 1), prior?.Advancement ?? 0);
                var enhanced = row with { Enhancements = (row.Enhancements ?? ItemEnhancements.Empty).With(Scope.Key, state) };
                // kr-1256 sub_5EE200 shows the points kept in the slot's attribute vector, so they commit with the same row.
                if (ApplyEnhancement && EnhancementKind == ItemEnhancementKind.Weapon && FavoritePoints > 0)
                    enhanced = enhanced with { Favorites = (row.Favorites ?? ItemFavorites.Empty).With(Scope.Key,
                        checked((row.Favorites?.Find(Scope.Key) ?? 0) + FavoritePoints)) };
                changed.Add(enhanced);
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

/// <summary>Serializes equipment Article allocation, selection and enhancement with other account inventory writes.</summary>
public interface IItemEnhancementStore
{
    /// <summary>Initializes eligible permanent Articles without changing existing pet identities and returns both committed bags.</summary>
    ValueTask<InventorySnapshot> InitializeArticlesAsync(ArticleInitializationCommand command, CancellationToken ct);

    /// <summary>Reads both build-projected bags under one ownership transaction without allocating new identities.</summary>
    ValueTask<InventorySnapshot> ReadEnhancementInventoryAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct);

    /// <summary>Commits all target revisions and material debits together, or returns null for a stale or ineligible plan.</summary>
    ValueTask<InventorySnapshot?> TryEnhanceAsync(ItemEnhancementCommand command, CancellationToken ct);

    /// <summary>Selects an exact live Article ownership in one build, or returns null if its expected row is stale.</summary>
    ValueTask<InventorySnapshot?> SelectArticleAsync(ArticleSelectionCommand command, CancellationToken ct);
}
