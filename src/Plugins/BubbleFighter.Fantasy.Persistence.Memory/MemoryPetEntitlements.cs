using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.Memory;

public sealed partial class MemoryInventoryRepository : IPetEntitlementStore
{
    /// <inheritdoc />
    public ValueTask<PetInventorySnapshot> InitializePetsAsync(PetInitializationCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        var entry = table.GetOrAdd(command.UserNum);
        lock (entry.Gate)
        {
            return ValueTask.FromResult(WithPetPreparation(entry, command.Scope, command, plan =>
            {
                var result = PetInventorySnapshot.Project(plan.Rows, plan.Loadout, command.Scope);
                CommitPetPreparation(entry, command.Scope, plan, plan.Rows, plan.Loadout, ct);
                return result;
            }));
        }
    }

    /// <inheritdoc />
    public ValueTask<PetInventorySnapshot> ReadPetInventoryAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(userNum);
        if (entry is null) return ValueTask.FromResult(new PetInventorySnapshot([], []));
        lock (entry.Gate)
        {
            ct.ThrowIfCancellationRequested();
            var rows = PetRows(entry);
            PetEntitlementInventory.ValidateRows(userNum, rows);
            return ValueTask.FromResult(PetInventorySnapshot.Project(rows, ReadLoadout(entry, scope), scope));
        }
    }

    /// <inheritdoc />
    public ValueTask<PetInventorySnapshot?> SelectPetAsync(PetEquipmentSelectionCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(command.UserNum);
        if (entry is null) return ValueTask.FromResult<PetInventorySnapshot?>(null);
        lock (entry.Gate)
        {
            return ValueTask.FromResult(WithPetPreparation<PetInventorySnapshot?>(entry, command.Scope, command.Initialization, plan =>
            {
                var changed = PetEntitlementInventory.Select(command, plan.Rows, plan.Loadout);
                if (changed is null) return null;
                var state = InventoryLoadouts.Selection(plan.Loadout, changed);
                var result = PetInventorySnapshot.Project(changed, state, command.Scope);
                CommitPetPreparation(entry, command.Scope, plan, plan.Rows, state, ct);
                return result;
            }, command.ArticleInitialization));
        }
    }

    private T WithPetPreparation<T>(PlayerEntry entry, InventoryLoadoutScope scope,
        PetInitializationCommand? initialization, Func<PreparedPetInventory, T> operation,
        ArticleInitializationCommand? articleInitialization = null, bool reserveIdentityAllocation = false)
    {
        var stored = PetRows(entry);
        var loadout = ReadLoadout(entry, scope);
        var allocate = reserveIdentityAllocation || (articleInitialization is not null
            ? InventoryMetadataPreparation.NeedsIdentity(articleInitialization.UserNum, scope, stored, articleInitialization, initialization)
            : initialization is not null && PetEntitlementInventory.NeedsIdentity(initialization, stored));
        if (!allocate) return Prepare();
        lock (table.PetAllocatorGate) return Prepare();

        T Prepare()
        {
            var lastId = allocate ? table.LastPetEntitlementId : 0;
            var lastArticle = allocate ? table.LastPetArticleKey : 0;
            InventoryArticleIdentity Allocate()
            {
                if (!allocate || lastArticle < PetArticleKeys.First - 1 || lastArticle >= PetArticleKeys.Last)
                    throw new InvalidOperationException("Native Article allocation is exhausted or corrupt.");
                return new(checked(++lastId), checked(++lastArticle));
            }
            var metadata = articleInitialization is null ? null : InventoryMetadataPreparation.Prepare(
                articleInitialization.UserNum, scope, stored, loadout, articleInitialization, initialization, Allocate);
            var rows = metadata?.Rows ?? (initialization is null ? stored : PetEntitlementInventory.Initialize(initialization, stored, () =>
            {
                var identity = Allocate();
                return new(identity.EntitlementId, identity.ArticleKey);
            }));
            foreach (var row in rows)
            {
                row.Pet?.Validate(maxLoadoutsPerPlayer);
                row.Enhancements?.Validate(maxLoadoutsPerPlayer);
                row.Modifications?.Validate(maxLoadoutsPerPlayer);
            }
            var state = metadata?.Loadout ?? (initialization is null ? loadout : InventoryLoadouts.MaterializePets(loadout, rows, scope));
            return operation(new(stored, rows, state, allocate, lastId, lastArticle));
        }
    }

    private void CommitPetPreparation(PlayerEntry entry, InventoryLoadoutScope scope, PreparedPetInventory plan,
        IReadOnlyList<InventorySlotRecord> rows, InventoryLoadoutState? loadout, CancellationToken ct, long? lastSlotKey = null)
    {
        if (lastSlotKey < entry.LastPurchaseSlotKey) throw new InvalidOperationException("Ordinary inventory keys cannot move backwards.");
        foreach (var row in rows)
        {
            row.Pet?.Validate(maxLoadoutsPerPlayer);
            row.Enhancements?.Validate(maxLoadoutsPerPlayer);
            row.Modifications?.Validate(maxLoadoutsPerPlayer);
        }
        var replace = !plan.Stored.SequenceEqual(rows);
        var character = replace ? rows.Where(row => row.Bag == InventoryBag.CharacterBag).ToList() : null;
        var equipped = replace ? rows.Where(row => row.Bag == InventoryBag.EquippedBag).ToList() : null;
        ct.ThrowIfCancellationRequested();
        if (replace)
        {
            entry.Bags[(int)InventoryBag.CharacterBag] = character!;
            entry.Bags[(int)InventoryBag.EquippedBag] = equipped!;
        }
        if (loadout is not null && !InventoryLoadouts.SameState(entry.Loadouts.GetValueOrDefault(scope.Key), loadout))
            entry.Loadouts[scope.Key] = loadout;
        if (lastSlotKey is { } allocated) entry.LastPurchaseSlotKey = allocated;
        if (plan.Allocate)
        {
            table.LastPetEntitlementId = plan.LastId;
            table.LastPetArticleKey = plan.LastArticle;
        }
    }

    private sealed record PreparedPetInventory(IReadOnlyList<InventorySlotRecord> Stored,
        IReadOnlyList<InventorySlotRecord> Rows, InventoryLoadoutState Loadout, bool Allocate, ulong LastId, ulong LastArticle);

    private static InventorySlotRecord[] PetRows(PlayerEntry entry) =>
        new[] { InventoryBag.CharacterBag, InventoryBag.EquippedBag }
            .SelectMany(bag => entry.Bags.GetValueOrDefault((int)bag) ?? []).ToArray();
}
