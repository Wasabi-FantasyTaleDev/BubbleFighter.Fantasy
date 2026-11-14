using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.Memory;

public sealed partial class MemoryInventoryRepository : IItemEnhancementStore
{
    /// <inheritdoc />
    public ValueTask<InventorySnapshot> InitializeArticlesAsync(ArticleInitializationCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        var entry = table.GetOrAdd(command.UserNum);
        lock (entry.Gate)
        {
            var stored = ArticleRows(entry);
            var allocate = InventoryArticles.NeedsIdentity(command, stored);
            if (allocate) { lock (table.PetAllocatorGate) return Initialize(); }
            return Initialize();

            ValueTask<InventorySnapshot> Initialize()
            {
                var lastId = table.LastPetEntitlementId;
                var lastArticle = table.LastPetArticleKey;
                var rows = InventoryArticles.Initialize(command, stored, () =>
                {
                    // Pet and ordinary Articles share one historical allocator; native keys must never collide.
                    if (!allocate || lastArticle < PetArticleKeys.First - 1 || lastArticle >= PetArticleKeys.Last)
                        throw new InvalidOperationException("Native Article allocation is exhausted or corrupt.");
                    return new(checked(++lastId), checked(++lastArticle));
                });
                var state = InventoryLoadouts.MaterializeArticles(ReadLoadout(entry, command.Scope), rows, command.Scope);
                var result = InventorySnapshot.Project(rows, state, command.Scope);
                CommitArticleRows(entry, command.Scope, rows, state, ct);
                if (allocate) { table.LastPetEntitlementId = lastId; table.LastPetArticleKey = lastArticle; }
                return ValueTask.FromResult(result);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<InventorySnapshot> ReadEnhancementInventoryAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(userNum);
        if (entry is null) return ValueTask.FromResult(new InventorySnapshot([], []));
        lock (entry.Gate)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(InventorySnapshot.Project(ArticleRows(entry), ReadLoadout(entry, scope), scope));
        }
    }

    /// <inheritdoc />
    public ValueTask<InventorySnapshot?> TryEnhanceAsync(ItemEnhancementCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(command.UserNum);
        if (entry is null) return ValueTask.FromResult<InventorySnapshot?>(null);
        lock (entry.Gate)
        {
            return ValueTask.FromResult(MutateArticles(entry, command.UserNum, command.Scope,
                command.ArticleInitialization, command.PetInitialization, prepared =>
                {
                    var rows = command.Apply(prepared.Rows, prepared.Loadout);
                    return rows is null ? null : new ArticleMutation(rows, prepared.Loadout);
                }, ct));
        }
    }

    /// <inheritdoc />
    public ValueTask<InventorySnapshot?> SelectArticleAsync(ArticleSelectionCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(command.UserNum);
        if (entry is null) return ValueTask.FromResult<InventorySnapshot?>(null);
        lock (entry.Gate)
        {
            return ValueTask.FromResult(MutateArticles(entry, command.UserNum, command.Scope,
                command.ArticleInitialization, command.PetInitialization, prepared =>
                {
                    var selected = InventoryArticles.Select(command, prepared.Rows, prepared.Loadout);
                    return selected is null ? null : new ArticleMutation(prepared.Rows, InventoryLoadouts.Selection(prepared.Loadout, selected));
                }, ct));
        }
    }

    private InventorySnapshot? MutateArticles(PlayerEntry entry, long userNum, InventoryLoadoutScope scope,
        ArticleInitializationCommand? articles, PetInitializationCommand? pets,
        Func<PreparedInventoryMetadata, ArticleMutation?> operation, CancellationToken ct)
    {
        var stored = ArticleRows(entry);
        var allocate = InventoryMetadataPreparation.NeedsIdentity(userNum, scope, stored, articles, pets);
        if (allocate) { lock (table.PetAllocatorGate) return PrepareAndCommit(); }
        return PrepareAndCommit();

        InventorySnapshot? PrepareAndCommit()
        {
            var lastId = table.LastPetEntitlementId;
            var lastArticle = table.LastPetArticleKey;
            var prepared = InventoryMetadataPreparation.Prepare(userNum, scope, stored, ReadLoadout(entry, scope), articles, pets, () =>
            {
                if (!allocate || lastArticle < PetArticleKeys.First - 1 || lastArticle >= PetArticleKeys.Last)
                    throw new InvalidOperationException("Native Article allocation is exhausted or corrupt.");
                return new(checked(++lastId), checked(++lastArticle));
            });
            var changed = operation(prepared);
            if (changed is null) return null;
            var result = InventorySnapshot.Project(changed.Rows, changed.Loadout, scope);
            CommitArticleRows(entry, scope, changed.Rows, changed.Loadout, ct);
            if (allocate) { table.LastPetEntitlementId = lastId; table.LastPetArticleKey = lastArticle; }
            return result;
        }
    }

    private sealed record ArticleMutation(IReadOnlyList<InventorySlotRecord> Rows, InventoryLoadoutState Loadout);

    private static InventorySlotRecord[] ArticleRows(PlayerEntry entry) =>
        new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag }
            .SelectMany(bag => entry.Bags.GetValueOrDefault((int)bag) ?? []).ToArray();

    private void CommitArticleRows(PlayerEntry entry, InventoryLoadoutScope scope, IReadOnlyList<InventorySlotRecord> rows,
        InventoryLoadoutState? state, CancellationToken ct)
    {
        foreach (var row in rows) { row.Pet?.Validate(maxLoadoutsPerPlayer); row.Enhancements?.Validate(maxLoadoutsPerPlayer); row.Modifications?.Validate(maxLoadoutsPerPlayer); row.Favorites?.Validate(maxLoadoutsPerPlayer); }
        var character = rows.Where(row => row.Bag == InventoryBag.CharacterBag).ToList();
        var equipped = rows.Where(row => row.Bag == InventoryBag.EquippedBag).ToList();
        ct.ThrowIfCancellationRequested();
        entry.Bags[(int)InventoryBag.CharacterBag] = character;
        entry.Bags[(int)InventoryBag.EquippedBag] = equipped;
        if (state is not null) entry.Loadouts[scope.Key] = state;
    }
}
