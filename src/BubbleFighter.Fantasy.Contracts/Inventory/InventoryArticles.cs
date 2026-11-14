namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Identifies one equipment ownership and its stable native Article reference.</summary>
/// <param name="EntitlementId">Database-wide ownership identity allocated from the shared identity pool.</param>
/// <param name="ArticleKey">Native reference in the signed Article lookup domain.</param>
public sealed record InventoryArticleIdentity(ulong EntitlementId, ulong ArticleKey)
{
    /// <summary>Rejects unset ownership and references that the native client routes to ordinary item lookup.</summary>
    public void Validate()
    {
        if (EntitlementId == 0 || !PetArticleKeys.IsAddressable(ArticleKey))
            throw new InvalidDataException("Equipment Article identity is outside the native lookup domain.");
    }
}

/// <summary>Resolves ordinary and existing pet Article ownership without changing either stored identity.</summary>
public static class InventoryArticleIdentityResolver
{
    /// <summary>Returns the exact Article identity, rejecting conflicting compatibility fields.</summary>
    public static InventoryArticleIdentity? Of(InventorySlotRecord slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        slot.ArticleIdentity?.Validate();
        slot.PetIdentity?.Validate();
        var pet = slot.PetIdentity is { } existing ? new InventoryArticleIdentity(existing.EntitlementId, existing.ArticleKey) : null;
        if (slot.ArticleIdentity is not null && pet is not null && slot.ArticleIdentity != pet)
            throw new InvalidDataException("Equipment and pet Article identities disagree.");
        return slot.ArticleIdentity ?? pet;
    }
}

/// <summary>Owns immutable copies of both build-projected bags from one transaction.</summary>
public sealed class InventorySnapshot
{
    /// <summary>Copies bounded character ownership and equipped mirrors after checking their ownership domains.</summary>
    public InventorySnapshot(IReadOnlyList<InventorySlotRecord> characterBag, IReadOnlyList<InventorySlotRecord> equippedBag)
    {
        ArgumentNullException.ThrowIfNull(characterBag);
        ArgumentNullException.ThrowIfNull(equippedBag);
        if (characterBag.Any(row => row.Bag != InventoryBag.CharacterBag)
            || equippedBag.Any(row => row.Bag != InventoryBag.EquippedBag))
            throw new InvalidDataException("Inventory snapshot contains the wrong bag.");
        var rows = characterBag.Concat(equippedBag).ToArray();
        var users = rows.Select(row => row.UserNum).Distinct().ToArray();
        if (users.Length > 1 || users.Any(user => user <= 0)) throw new InvalidDataException("Inventory snapshot crosses ownership.");
        InventoryArticles.ValidateRows(users.FirstOrDefault(), rows);
        CharacterBag = Array.AsReadOnly(characterBag.ToArray());
        EquippedBag = Array.AsReadOnly(equippedBag.ToArray());
    }

    /// <summary>Gets the committed ownership rows compatible with this client.</summary>
    public IReadOnlyList<InventorySlotRecord> CharacterBag { get; }

    /// <summary>Gets the committed equipped mirrors compatible with this client.</summary>
    public IReadOnlyList<InventorySlotRecord> EquippedBag { get; }

    /// <summary>Projects a locked complete inventory through the selected build's loadout without a later read.</summary>
    public static InventorySnapshot Project(IReadOnlyList<InventorySlotRecord> rows, InventoryLoadoutState state, InventoryLoadoutScope scope)
    {
        InventoryArticles.ValidateRows(rows.FirstOrDefault()?.UserNum ?? 0, rows);
        var projected = InventoryLoadouts.Project(rows, state, scope);
        return new(projected.Where(row => row.Bag == InventoryBag.CharacterBag).ToArray(),
            projected.Where(row => row.Bag == InventoryBag.EquippedBag).ToArray());
    }
}

/// <summary>Requests stable Articles for a native catalog's eligible permanent equipment.</summary>
/// <param name="UserNum">Owner whose inventory lock guards allocation.</param>
/// <param name="Scope">Client catalog and equipment selection scope.</param>
/// <param name="EligibleItemIds">Native catalog items permitted to become equipment Articles.</param>
/// <param name="Now">Authoritative time used when checking live ownership.</param>
public sealed record ArticleInitializationCommand(long UserNum, InventoryLoadoutScope Scope,
    IReadOnlyList<int> EligibleItemIds, DateTimeOffset Now)
{
    /// <summary>Gets a copied catalog selection that cannot be changed during allocation.</summary>
    public IReadOnlyList<int> EligibleItemIds { get; } = Array.AsReadOnly(EligibleItemIds.ToArray());

    /// <summary>Rejects inconsistent owner, duplicate catalog identities and unsupported items before allocation.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(UserNum);
        ArgumentNullException.ThrowIfNull(Scope);
        if (EligibleItemIds.Count > 65536 || EligibleItemIds.Any(id => id <= 0 || !Scope.Supports(id))
            || EligibleItemIds.Distinct().Count() != EligibleItemIds.Count)
            throw new ArgumentException("Equipment Article catalog is invalid or exceeds its bound.");
    }

    /// <summary>Requires initialization to use the enclosing transaction's exact owner and complete catalog scope.</summary>
    public void ValidateFor(long userNum, InventoryLoadoutScope scope)
    {
        Validate();
        ArgumentNullException.ThrowIfNull(scope);
        if (UserNum != userNum || Scope.Key != scope.Key || Scope.SupportedItemIds.Count != scope.SupportedItemIds.Count
            || Scope.SupportedItemIds.Any(id => !scope.Supports(id)))
            throw new ArgumentException("Article initialization must match its transaction owner and catalog.");
    }
}

/// <summary>Requests an exact ownership selection using this build's native exclusive equipment groups.</summary>
/// <param name="UserNum">Authenticated inventory owner.</param>
/// <param name="Scope">The catalog and loadout whose selection changes.</param>
/// <param name="ExpectedTarget">The exact projected Article row resolved from the native request.</param>
/// <param name="Groups">Native equipment conflict groups.</param>
/// <param name="Worn">Whether to select the target.</param>
/// <param name="Now">Authoritative ownership expiry check time.</param>
public sealed record ArticleSelectionCommand(long UserNum, InventoryLoadoutScope Scope,
    InventorySlotRecord ExpectedTarget, IReadOnlyList<InventoryWearGroup> Groups, bool Worn, DateTimeOffset Now)
{
    /// <summary>Gets equipment identity initialization to commit with this exact selection.</summary>
    public ArticleInitializationCommand? ArticleInitialization { get; init; }

    /// <summary>Gets pet identity and progression initialization required by the same complete publication.</summary>
    public PetInitializationCommand? PetInitialization { get; init; }

    /// <summary>Gets copied bounded equipment group definitions.</summary>
    public IReadOnlyList<InventoryWearGroup> Groups { get; } = CopyGroups(Groups);

    /// <summary>Rejects mismatched ownership, catalog or native identity before entering a persistence transaction.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(UserNum);
        ArgumentNullException.ThrowIfNull(Scope);
        ArgumentNullException.ThrowIfNull(ExpectedTarget);
        if (ExpectedTarget.UserNum != UserNum || ExpectedTarget.Bag != InventoryBag.CharacterBag
            || ExpectedTarget.SlotKey <= 0 || !Scope.Supports(ExpectedTarget.ItemId)
            || InventoryArticleIdentityResolver.Of(ExpectedTarget) is null)
            throw new ArgumentException("Equipment selection has invalid Article ownership.");
        InventoryArticleIdentityResolver.Of(ExpectedTarget)!.Validate();
        ArticleInitialization?.ValidateFor(UserNum, Scope);
        PetInitialization?.ValidateFor(UserNum, Scope);
    }

    private static IReadOnlyList<InventoryWearGroup> CopyGroups(IReadOnlyList<InventoryWearGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count > InventoryLimits.MaxWearGroups || groups.Any(group => group is null
            || group.ItemIds?.Count > InventoryLimits.MaxItemsPerWearGroup))
            throw new ArgumentException("Equipment groups exceed their bounds.", nameof(groups));
        return Array.AsReadOnly(groups.Select(group => group with
        { ItemIds = group.ItemIds is null ? null : Array.AsReadOnly(group.ItemIds.ToArray()) }).ToArray());
    }
}

/// <summary>Shares Article allocation, exact ownership and metadata invariants across persistence providers.</summary>
public static class InventoryArticles
{
    /// <summary>Tests whether allocation is needed without advancing the shared allocator.</summary>
    public static bool NeedsIdentity(ArticleInitializationCommand command, IReadOnlyList<InventorySlotRecord> rows)
    {
        command.Validate();
        var eligible = command.EligibleItemIds.ToHashSet();
        return rows.Any(row => row.Bag == InventoryBag.CharacterBag && eligible.Contains(row.ItemId)
            && row.Quantity > 0 && row.Lifetime.Kind == InventoryLifetimeKind.Permanent
            && InventoryArticleIdentityResolver.Of(row) is null);
    }

    /// <summary>Initializes only unambiguous permanent ownership while retaining all existing identities and pet state.</summary>
    public static IReadOnlyList<InventorySlotRecord> Initialize(ArticleInitializationCommand command,
        IReadOnlyList<InventorySlotRecord> rows, Func<InventoryArticleIdentity> allocate)
    {
        command.Validate();
        ValidateRows(command.UserNum, rows);
        var eligible = command.EligibleItemIds.ToHashSet();
        var changed = rows.ToArray();
        foreach (var group in rows.Where(row => row.Bag == InventoryBag.CharacterBag && eligible.Contains(row.ItemId)).GroupBy(row => row.ItemId))
        {
            var owned = group.ToArray();
            if (owned.Length != 1) throw new InvalidDataException("Equipment ownership is ambiguous.");
            var owner = owned[0];
            if (owner.Quantity <= 0 || owner.Lifetime.Kind != InventoryLifetimeKind.Permanent) continue;
            RequireMirrors(owner, rows.Where(row => row.Bag == InventoryBag.EquippedBag && row.ItemId == owner.ItemId).ToArray());
            if (InventoryArticleIdentityResolver.Of(owner) is not null) continue;
            var identity = allocate();
            identity.Validate();
            for (var index = 0; index < changed.Length; index++)
                if (changed[index].ItemId == owner.ItemId) changed[index] = changed[index] with { ArticleIdentity = identity };
        }
        ValidateRows(command.UserNum, changed);
        return changed;
    }

    /// <summary>Revalidates the exact projected ownership and returns a new selection without mutating shared worn flags.</summary>
    public static IReadOnlyList<InventorySlotRecord>? Select(ArticleSelectionCommand command,
        IReadOnlyList<InventorySlotRecord> rows, InventoryLoadoutState loadout)
    {
        command.Validate();
        ValidateRows(command.UserNum, rows);
        var projected = InventoryLoadouts.Project(rows, loadout, command.Scope);
        var targets = projected.Where(row => row.Bag == InventoryBag.CharacterBag && row.ItemId == command.ExpectedTarget.ItemId).ToArray();
        if (targets.Length != 1 || targets[0] != command.ExpectedTarget || targets[0].Quantity <= 0
            || InventoryExpiry.IsExpired(targets[0], command.Now)) return null;
        RequireMirrors(targets[0], projected.Where(row => row.Bag == InventoryBag.EquippedBag && row.ItemId == targets[0].ItemId).ToArray());
        return InventoryWearSelection.Apply(projected, command.Groups, targets[0].ItemId, command.Worn);
    }

    /// <summary>Rejects conflicting copies of one owned item while allowing an absent equipped mirror.</summary>
    public static void RequireMirrors(InventorySlotRecord owner, IReadOnlyList<InventorySlotRecord> mirrors)
    {
        if (mirrors.Count > 1 || mirrors.Any(row => row.UserNum != owner.UserNum || row.ItemId != owner.ItemId
            || InventoryArticleIdentityResolver.Of(row) != InventoryArticleIdentityResolver.Of(owner)
            || !Equals(row.Enhancements, owner.Enhancements) || !Equals(row.Modifications, owner.Modifications)
            || !Equals(row.Favorites, owner.Favorites) || !Equals(row.Pet, owner.Pet)
            || row.Quantity != owner.Quantity || row.Lifetime != owner.Lifetime))
            throw new InvalidDataException("Equipment mirrors disagree with their owned item.");
    }

    /// <summary>Rejects malformed, duplicated or cross-item identities and bounded enhancement metadata.</summary>
    public static void ValidateRows(long userNum, IReadOnlyList<InventorySlotRecord> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count > 2 * InventoryLimits.MaxInventorySlotsPerBag || rows.Any(row => row.UserNum != userNum
            || row.Bag is not (InventoryBag.CharacterBag or InventoryBag.EquippedBag))
            || rows.GroupBy(row => row.Bag).Any(group => group.Count() > InventoryLimits.MaxInventorySlotsPerBag))
            throw new InvalidDataException("Article inventory exceeds its bound or crosses ownership.");
        foreach (var row in rows)
        {
            _ = InventoryArticleIdentityResolver.Of(row);
            row.Enhancements?.Validate();
            row.Modifications?.Validate();
            row.Favorites?.Validate();
        }
        var identified = rows.Select(row => (Row: row, Identity: InventoryArticleIdentityResolver.Of(row)))
            .Where(pair => pair.Identity is not null).ToArray();
        if (identified.GroupBy(pair => (pair.Row.Bag, pair.Row.ItemId)).Any(group => group.Count() > 1)
            || identified.GroupBy(pair => pair.Identity!.EntitlementId).Any(group => group.Select(pair => (pair.Row.ItemId, pair.Identity)).Distinct().Count() != 1)
            || identified.GroupBy(pair => pair.Identity!.ArticleKey).Any(group => group.Select(pair => (pair.Row.ItemId, pair.Identity)).Distinct().Count() != 1))
            throw new InvalidDataException("An Article identity is cloned across equipment ownership.");
    }

    /// <summary>Prevents ordinary replace and append writers from inventing, erasing or restoring managed Article metadata.</summary>
    public static void ValidateExternalWrite(long userNum, IReadOnlyList<InventorySlotRecord> stored, IReadOnlyList<InventorySlotRecord> incoming)
    {
        ValidateRows(userNum, incoming);
        foreach (var row in incoming)
        {
            var owners = stored.Where(old => old.Bag == InventoryBag.CharacterBag && old.ItemId == row.ItemId).ToArray();
            if (row.ArticleIdentity is not null || row.Enhancements is not null || row.Modifications is not null || row.Favorites is not null
                || owners.Any(owner => owner.ArticleIdentity is not null || owner.Enhancements is not null || owner.Modifications is not null
                    || owner.Favorites is not null))
            {
                if (owners.Length != 1 || row.ArticleIdentity != owners[0].ArticleIdentity
                    || !Equals(row.Enhancements, owners[0].Enhancements) || !Equals(row.Modifications, owners[0].Modifications)
                    || !Equals(row.Favorites, owners[0].Favorites))
                    throw new InvalidDataException("External inventory writes cannot manufacture or replace Article metadata.");
            }
        }
    }
}
