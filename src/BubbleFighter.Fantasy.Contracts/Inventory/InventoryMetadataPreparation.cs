namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Collects enabled native initialization inputs before one atomic inventory publication.</summary>
/// <param name="UserNum">Owner whose metadata will be initialized.</param>
/// <param name="Scope">The canonical build catalogue used by every contributor.</param>
/// <param name="Now">One authoritative instant shared by all catalogue inputs.</param>
public sealed record InventoryMetadataInputsRequested(long UserNum, InventoryLoadoutScope Scope, DateTimeOffset Now)
    : BubbleFighter.Fantasy.Abstractions.IEvent;

/// <summary>Prepares every enabled ownership catalogue from one account snapshot before inventory publication.</summary>
/// <param name="UserNum">The existing account whose ownership metadata is prepared.</param>
/// <param name="Scope">The complete native item catalogue and selected build loadout.</param>
public sealed record InventoryMetadataPreparationCommand(long UserNum, InventoryLoadoutScope Scope)
{
    /// <summary>Gets the native permanent-equipment catalogue to initialize within the account transaction.</summary>
    public ArticleInitializationCommand? ArticleInitialization { get; init; }

    /// <summary>Gets the native pet catalogue and initial progression to initialize in that same transaction.</summary>
    public PetInitializationCommand? PetInitialization { get; init; }

    /// <summary>Rejects foreign owners or differing catalogue scopes before acquiring the account lock.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(UserNum);
        ArgumentNullException.ThrowIfNull(Scope);
        ArticleInitialization?.ValidateFor(UserNum, Scope);
        PetInitialization?.ValidateFor(UserNum, Scope);
    }
}

/// <summary>Commits complete ownership metadata with one allocator and returns both inventory bags from that transaction.</summary>
public interface IInventoryMetadataStore
{
    /// <summary>Initializes the supplied catalogues atomically and returns a complete, build-projected inventory snapshot.</summary>
    ValueTask<InventorySnapshot> PrepareInventorySnapshotAsync(InventoryMetadataPreparationCommand command, CancellationToken ct);
}

/// <summary>Stages publication metadata without committing identity counters or modifying the input inventory.</summary>
public static class InventoryMetadataPreparation
{
    /// <summary>Determines whether the shared allocator is needed for either configured ownership catalog.</summary>
    public static bool NeedsIdentity(long userNum, InventoryLoadoutScope scope, IReadOnlyList<InventorySlotRecord> rows,
        ArticleInitializationCommand? articles, PetInitializationCommand? pets)
    {
        articles?.ValidateFor(userNum, scope);
        pets?.ValidateFor(userNum, scope);
        return articles is not null && InventoryArticles.NeedsIdentity(articles, rows)
            || pets is not null && PetEntitlementInventory.NeedsIdentity(pets, rows);
    }

    /// <summary>Prepares both catalogs from one locked snapshot and one provisional allocator.</summary>
    public static PreparedInventoryMetadata Prepare(long userNum, InventoryLoadoutScope scope,
        IReadOnlyList<InventorySlotRecord> rows, InventoryLoadoutState loadout,
        ArticleInitializationCommand? articles, PetInitializationCommand? pets, Func<InventoryArticleIdentity> allocate)
    {
        articles?.ValidateFor(userNum, scope);
        pets?.ValidateFor(userNum, scope);
        InventoryArticles.ValidateRows(userNum, rows);
        if (pets is not null)
        {
            rows = PetEntitlementInventory.Initialize(pets, rows, () =>
            {
                var identity = allocate();
                return new(identity.EntitlementId, identity.ArticleKey);
            });
            loadout = InventoryLoadouts.MaterializePets(loadout, rows, scope);
        }
        if (articles is not null)
        {
            rows = InventoryArticles.Initialize(articles, rows, allocate);
            loadout = InventoryLoadouts.MaterializeArticles(loadout, rows, scope);
        }
        return new(Array.AsReadOnly(rows.ToArray()), loadout.Copy());
    }
}

/// <summary>Contains provisional metadata and projected selection to commit only after the enclosing operation succeeds.</summary>
/// <param name="Rows">Complete raw inventory retaining metadata outside the selected catalog.</param>
/// <param name="Loadout">The selected build's staged ownership references and equipment selection.</param>
public sealed record PreparedInventoryMetadata(IReadOnlyList<InventorySlotRecord> Rows, InventoryLoadoutState Loadout);
