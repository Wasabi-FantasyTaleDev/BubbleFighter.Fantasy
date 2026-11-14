namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Identifies one owned pet independently of its catalog item and its native Article reference.</summary>
/// <param name="EntitlementId">Database-wide identity allocated once for this ownership.</param>
/// <param name="ArticleKey">Database-wide native Article key, retained across client builds.</param>
public sealed record PetEntitlementIdentity(ulong EntitlementId, ulong ArticleKey)
{
    /// <summary>Rejects unset ownership and references in the native item-ID range.</summary>
    public void Validate()
    {
        if (EntitlementId == 0 || ArticleKey < PetArticleKeys.First)
            throw new InvalidDataException("Pet entitlement and Article identities are outside their native domains.");
    }
}

/// <summary>Owns bounded, immutable copies of both bags projected from one persistence transaction.</summary>
public sealed class PetInventorySnapshot
{
    /// <summary>Copies both vectors after checking their bag, owner and identity domains.</summary>
    public PetInventorySnapshot(IReadOnlyList<InventorySlotRecord> characterBag, IReadOnlyList<InventorySlotRecord> equippedBag)
    {
        CharacterBag = Copy(characterBag, InventoryBag.CharacterBag);
        EquippedBag = Copy(equippedBag, InventoryBag.EquippedBag);
        if (CharacterBag.Concat(EquippedBag).Select(row => row.UserNum).Distinct().Take(2).Count() > 1)
            throw new InvalidDataException("Pet inventory contains more than one owner.");
    }

    /// <summary>Gets authoritative ownership rows projected for the requested build.</summary>
    public IReadOnlyList<InventorySlotRecord> CharacterBag { get; }

    /// <summary>Gets existing equipped mirror rows from the same committed projection.</summary>
    public IReadOnlyList<InventorySlotRecord> EquippedBag { get; }

    /// <summary>Projects and splits a locked complete inventory without a second persistence read.</summary>
    public static PetInventorySnapshot Project(IReadOnlyList<InventorySlotRecord> rows, InventoryLoadoutState state,
        InventoryLoadoutScope scope)
    {
        var projected = InventoryLoadouts.Project(rows, state, scope);
        return new(projected.Where(row => row.Bag == InventoryBag.CharacterBag).ToArray(),
            projected.Where(row => row.Bag == InventoryBag.EquippedBag).ToArray());
    }

    private static IReadOnlyList<InventorySlotRecord> Copy(IReadOnlyList<InventorySlotRecord> rows, InventoryBag bag)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count > InventoryLimits.MaxInventorySlotsPerBag || rows.Any(row => row is null || row.Bag != bag || row.UserNum <= 0))
            throw new InvalidDataException("Pet inventory exceeds its bag bound or contains an invalid owner or bag.");
        foreach (var row in rows) { row.Pet?.Validate(); row.PetIdentity?.Validate(); }
        return Array.AsReadOnly(rows.ToArray());
    }
}

/// <summary>Initializes owned catalog pets once for a build while retaining shared ownership identities.</summary>
/// <param name="UserNum">Owner whose account lock guards the operation.</param>
/// <param name="Scope">The selected build and its supported item catalog.</param>
/// <param name="PetItemIds">Native catalog items recognized as pets.</param>
/// <param name="InitialProgression">Initial state used only for a missing build entry.</param>
/// <param name="Now">Authoritative time used to exclude ended ownership.</param>
public sealed record PetInitializationCommand(long UserNum, InventoryLoadoutScope Scope,
    IReadOnlyList<int> PetItemIds, PetProgression InitialProgression, DateTimeOffset Now)
{
    /// <summary>Gets a copied, unique catalog list that cannot change during initialization.</summary>
    public IReadOnlyList<int> PetItemIds { get; } = CopyIds(PetItemIds, Scope);
    private readonly HashSet<int> _petItems = PetItemIds.ToHashSet();

    /// <summary>Tests the copied native pet catalog without scanning it for each owned inventory row.</summary>
    public bool IsPetItem(int itemId) => _petItems.Contains(itemId);

    /// <summary>Checks owner, build and initial state before allocation or persistence writes.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(UserNum);
        ArgumentNullException.ThrowIfNull(Scope);
        ArgumentNullException.ThrowIfNull(InitialProgression);
        if (PetItemIds.Any(id => !Scope.Supports(id))) throw new ArgumentException("Pet catalog belongs to another build scope.");
        InitialProgression.Validate();
    }

    /// <summary>Requires initialization to use the same owner, build key and catalog as its enclosing mutation.</summary>
    public void ValidateFor(long userNum, InventoryLoadoutScope scope)
    {
        Validate();
        ArgumentNullException.ThrowIfNull(scope);
        if (UserNum != userNum || Scope.Key != scope.Key || Scope.SupportedItemIds.Count != scope.SupportedItemIds.Count
            || Scope.SupportedItemIds.Any(id => !scope.Supports(id)))
            throw new ArgumentException("Pet initialization must match the mutation owner and complete build catalog.");
    }

    private static IReadOnlyList<int> CopyIds(IReadOnlyList<int> ids, InventoryLoadoutScope scope)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(scope);
        if (ids.Count > 65536 || ids.Any(id => id <= 0 || !scope.Supports(id))
            || ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("Pet catalog identities exceed their bound or are not distinct supported items.", nameof(ids));
        return Array.AsReadOnly(ids.ToArray());
    }
}

/// <summary>Pairs a resolved catalog item with the exact ownership that authorized an Article request.</summary>
/// <param name="ItemId">The native catalog item retained by ordinary inventory policies.</param>
/// <param name="Identity">The exact ownership and Article reference resolved by the feature service.</param>
public sealed record PetEquipmentTarget(int ItemId, PetEntitlementIdentity Identity)
{
    /// <summary>Rejects an invalid item or unset native ownership reference.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ItemId);
        ArgumentNullException.ThrowIfNull(Identity);
        Identity.Validate();
    }
}

/// <summary>Requests equipment selection with ownership revalidation in the same transaction as the write.</summary>
/// <param name="UserNum">The account that must still own the target.</param>
/// <param name="Scope">The build whose equipment selection changes.</param>
/// <param name="Groups">Native exclusive-position rules copied for the operation.</param>
/// <param name="Target">The exact item and ownership identity resolved from the request.</param>
/// <param name="Worn">Whether to select or remove the target.</param>
/// <param name="Now">Authoritative time for the ownership lifetime check.</param>
public sealed record PetEquipmentSelectionCommand(long UserNum, InventoryLoadoutScope Scope,
    IReadOnlyList<InventoryWearGroup> Groups, PetEquipmentTarget Target, bool Worn, DateTimeOffset Now)
{
    /// <summary>Gets ordinary Article metadata staged in the same exact-ownership selection transaction.</summary>
    public ArticleInitializationCommand? ArticleInitialization { get; init; }

    /// <summary>Gets optional metadata initialization performed within the same exact-ownership selection transaction.</summary>
    public PetInitializationCommand? Initialization { get; init; }

    /// <summary>Gets copied native equipment groups and their explicit item vectors.</summary>
    public IReadOnlyList<InventoryWearGroup> Groups { get; } = CopyGroups(Groups);

    /// <summary>Checks the owner, catalog and immutable target before entering the transaction.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(UserNum);
        ArgumentNullException.ThrowIfNull(Scope);
        ArgumentNullException.ThrowIfNull(Target);
        Target.Validate();
        if (!Scope.Supports(Target.ItemId)) throw new ArgumentException("Pet selection targets another build's catalog.");
        Initialization?.ValidateFor(UserNum, Scope);
        ArticleInitialization?.ValidateFor(UserNum, Scope);
    }

    private static IReadOnlyList<InventoryWearGroup> CopyGroups(IReadOnlyList<InventoryWearGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (groups.Count > InventoryLimits.MaxWearGroups || groups.Any(group => group is null
            || group.ItemIds?.Count > InventoryLimits.MaxItemsPerWearGroup))
            throw new ArgumentException("Pet selection exceeds the equipment group bounds.", nameof(groups));
        return Array.AsReadOnly(groups.Select(group => group with
        {
            ItemIds = group.ItemIds is null ? null : Array.AsReadOnly(group.ItemIds.ToArray()),
        }).ToArray());
    }
}

/// <summary>Owns atomic pet identity materialization, complete inventory reads and exact Article equipment selection.</summary>
public interface IPetEntitlementStore
{
    /// <summary>Initializes live owned pets and returns both projected bags from that commit.</summary>
    ValueTask<PetInventorySnapshot> InitializePetsAsync(PetInitializationCommand command, CancellationToken ct);

    /// <summary>Reads both projected bags under one ownership transaction without allocating identities.</summary>
    ValueTask<PetInventorySnapshot> ReadPetInventoryAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct);

    /// <summary>Selects only the exact still-owned Article target, returning null when it is stale or unavailable.</summary>
    ValueTask<PetInventorySnapshot?> SelectPetAsync(PetEquipmentSelectionCommand command, CancellationToken ct);
}

/// <summary>Shares bounded ownership and mirror invariants across all persistence providers.</summary>
public static class PetEntitlementInventory
{
    /// <summary>Determines whether a locked ownership snapshot needs allocation before acquiring the global allocator.</summary>
    public static bool NeedsIdentity(PetInitializationCommand command, IReadOnlyList<InventorySlotRecord> stored) =>
        stored.Any(row => row.Bag == InventoryBag.CharacterBag && command.IsPetItem(row.ItemId)
            && InventoryArticleIdentityResolver.Of(row) is null && row.Quantity > 0 && !InventoryExpiry.IsExpired(row, command.Now));

    /// <summary>Initializes existing live character ownership and its existing mirrors using transaction-owned allocation.</summary>
    public static IReadOnlyList<InventorySlotRecord> Initialize(PetInitializationCommand command,
        IReadOnlyList<InventorySlotRecord> stored, Func<PetEntitlementIdentity> allocate)
    {
        command.Validate();
        ArgumentNullException.ThrowIfNull(allocate);
        ValidateRows(command.UserNum, stored);
        var changed = stored.ToArray();
        foreach (var group in stored.Where(row => row.Bag == InventoryBag.CharacterBag && command.IsPetItem(row.ItemId)).GroupBy(row => row.ItemId))
        {
            var item = group.Key;
            var owned = group.ToArray();
            if (owned.Length > 1) throw new InvalidDataException("A pet item has ambiguous character-bag ownership.");
            if (owned.Length == 0 || owned[0].Quantity <= 0 || InventoryExpiry.IsExpired(owned[0], command.Now)) continue;
            var current = owned[0];
            var mirrors = stored.Where(row => row.Bag == InventoryBag.EquippedBag && row.ItemId == item).ToArray();
            if (mirrors.Length > 1) throw new InvalidDataException("A pet item has ambiguous equipped mirrors.");
            if (current.PetIdentity is not null) RequireMirrors(current, mirrors, allowUninitialized: true);
            var identity = current.PetIdentity ?? (current.ArticleIdentity is { } article
                ? new PetEntitlementIdentity(article.EntitlementId, article.ArticleKey) : allocate());
            identity.Validate();
            var progress = current.Pet ?? PetProgressions.Empty;
            if (progress.Find(command.Scope.Key) is null) progress = progress.With(command.Scope.Key, command.InitialProgression);
            for (var index = 0; index < changed.Length; index++)
                if (changed[index].ItemId == item) changed[index] = changed[index] with { PetIdentity = identity, Pet = progress };
        }
        ValidateRows(command.UserNum, changed);
        return changed;
    }

    /// <summary>Checks exact live character ownership and mirror agreement before applying native wear rules.</summary>
    public static IReadOnlyList<InventorySlotRecord>? Select(PetEquipmentSelectionCommand command,
        IReadOnlyList<InventorySlotRecord> stored, InventoryLoadoutState loadout)
    {
        command.Validate();
        ValidateRows(command.UserNum, stored);
        var owned = stored.Where(row => row.Bag == InventoryBag.CharacterBag && row.ItemId == command.Target.ItemId).ToArray();
        if (owned.Length != 1 || owned[0].PetIdentity != command.Target.Identity || owned[0].Quantity <= 0
            || InventoryExpiry.IsExpired(owned[0], command.Now)) return null;
        RequireMirrors(owned[0], stored.Where(row => row.Bag == InventoryBag.EquippedBag && row.ItemId == owned[0].ItemId).ToArray());
        return InventoryWearSelection.Apply(InventoryLoadouts.Project(stored, loadout, command.Scope),
            command.Groups, command.Target.ItemId, command.Worn);
    }

    /// <summary>Rejects conflicting mirrored identities or growth; missing mirror rows are permitted.</summary>
    public static void RequireMirrors(InventorySlotRecord owned, IReadOnlyList<InventorySlotRecord> mirrors,
        bool allowUninitialized = false)
    {
        if (mirrors.Count > 1 || mirrors.Any(row => row.UserNum != owned.UserNum || row.ItemId != owned.ItemId
            || (row.PetIdentity != owned.PetIdentity && !(allowUninitialized && row.PetIdentity is null))
            || (!Equals(row.Pet, owned.Pet) && !(allowUninitialized && row.Pet is null))))
            throw new InvalidDataException("Pet mirror identity or progression conflicts with character ownership.");
    }

    /// <summary>Rejects externally manufactured identities and stale ownership copied back after removal.</summary>
    public static void ValidateExternalWrite(long userNum, IReadOnlyList<InventorySlotRecord> stored,
        IReadOnlyList<InventorySlotRecord> incoming)
    {
        InventoryArticles.ValidateExternalWrite(userNum, stored, incoming);
        ValidateRows(userNum, incoming);
        foreach (var row in incoming)
        {
            var owned = stored.Where(old => old.Bag == InventoryBag.CharacterBag && old.ItemId == row.ItemId).ToArray();
            if (row.PetIdentity is { } identity && (owned.Length != 1 || owned[0].PetIdentity != identity))
                throw new InvalidDataException("External inventory writes cannot allocate, clone or resurrect pet identities.");
            if (row.Bag == InventoryBag.CharacterBag && owned.Length == 1 && owned[0].PetIdentity is not null
                && row.PetIdentity != owned[0].PetIdentity)
                throw new InvalidDataException("Existing pet ownership must retain its exact identity until removal.");
        }
    }

    /// <summary>Checks inventory capacity and uniqueness of active identities without keeping retired ownership rows.</summary>
    public static void ValidateRows(long userNum, IReadOnlyList<InventorySlotRecord> rows)
    {
        InventoryArticles.ValidateRows(userNum, rows);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count > 2 * InventoryLimits.MaxInventorySlotsPerBag || rows.Any(row => row.UserNum != userNum
                || row.Bag is not (InventoryBag.CharacterBag or InventoryBag.EquippedBag))
            || rows.GroupBy(row => row.Bag).Any(bag => bag.Count() > InventoryLimits.MaxInventorySlotsPerBag))
            throw new InvalidDataException("Pet inventory exceeds its bound or crosses an ownership boundary.");
        foreach (var row in rows) row.PetIdentity?.Validate();
        var identified = rows.Where(row => row.PetIdentity is not null).ToArray();
        if (identified.GroupBy(row => (row.Bag, row.ItemId)).Any(group => group.Count() > 1)
            || identified.GroupBy(row => row.PetIdentity!.EntitlementId).Any(group => group.Select(row => row.ItemId).Distinct().Count() != 1)
            || identified.GroupBy(row => row.PetIdentity!.ArticleKey).Any(group => group.Select(row => row.ItemId).Distinct().Count() != 1))
            throw new InvalidDataException("An active pet identity is cloned across inventory ownership.");
    }
}
