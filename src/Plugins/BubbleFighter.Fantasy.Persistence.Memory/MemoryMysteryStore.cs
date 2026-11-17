using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.MysteryStore;

namespace BubbleFighter.Fantasy.Persistence.Memory;

public sealed partial class MemoryInventoryRepository : IMysteryStore
{
    /// <summary>Gets the clock the Mysterystore reads under the player lock; tests replace it.</summary>
    public TimeProvider MysteryStoreClock { get; init; } = TimeProvider.System;

    /// <inheritdoc />
    ValueTask<MysteryStoreResult> IMysteryStore.EnsureAsync(MysteryStoreEnsureCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ct.ThrowIfCancellationRequested();
        var entry = RequireMysteryEntry(command.UserNum);
        lock (entry.Gate)
        {
            var current = entry.MysteryStores.GetValueOrDefault(command.Scope.Key);
            var plan = MysteryStorePlanner.Ensure(command, current, NextToken(current), MysteryStoreClock.GetUtcNow());
            ct.ThrowIfCancellationRequested();
            if (plan.RequiresCommit)
            {
                if (current is null && entry.MysteryStores.Count >= maxLoadoutsPerPlayer)
                    throw new InvalidOperationException("The player's mystery store scope limit is exhausted.");
                entry.MysteryStores[command.Scope.Key] = plan.Selection!;
            }
            return ValueTask.FromResult(new MysteryStoreResult(plan.Outcome, plan.Selection, null, []));
        }
    }

    /// <inheritdoc />
    ValueTask<MysteryStoreResult> IMysteryStore.RefreshAsync(MysteryStoreRefreshCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Inventory);
        ct.ThrowIfCancellationRequested();
        var entry = RequireMysteryEntry(command.Inventory.UserNum);
        lock (entry.Gate)
        {
            var scope = command.Inventory.Scope;
            var current = entry.MysteryStores.GetValueOrDefault(scope.Key);
            var plan = MysteryStorePlanner.Refresh(command, current, ArticleRows(entry), ReadLoadout(entry, scope),
                entry.LastPurchaseSlotKey, NextToken(current), MysteryStoreClock.GetUtcNow());
            return ValueTask.FromResult(Commit(entry, command.Inventory, plan, ct));
        }
    }

    /// <inheritdoc />
    ValueTask<MysteryStoreResult> IMysteryStore.PurchaseAsync(MysteryStorePurchaseCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Inventory);
        ct.ThrowIfCancellationRequested();
        var entry = RequireMysteryEntry(command.Inventory.UserNum);
        lock (entry.Gate)
        {
            var scope = command.Inventory.Scope;
            var plan = MysteryStorePlanner.Purchase(command, entry.MysteryStores.GetValueOrDefault(scope.Key),
                ArticleRows(entry), ReadLoadout(entry, scope), entry.LastPurchaseSlotKey, MysteryStoreClock.GetUtcNow());
            return ValueTask.FromResult(Commit(entry, command.Inventory, plan, ct));
        }
    }

    // Called under entry.Gate: prepares metadata for the staged rows, then commits rows, loadout, slot watermark and
    // selection together; a refusal or replay writes nothing.
    private MysteryStoreResult Commit(PlayerEntry entry, InventoryMetadataPreparationCommand inventory, MysteryStorePlan plan,
        CancellationToken ct)
    {
        if (!plan.RequiresCommit)
            return new(plan.Outcome, plan.Selection, plan.ReplayInventory, [], plan.ReceiptNativeKey, plan.GrantRefusal);
        var scope = inventory.Scope;
        inventory = inventory with
        {
            ArticleInitialization = inventory.ArticleInitialization is { } articles ? articles with { Now = plan.PlannedAt } : null,
            PetInitialization = inventory.PetInitialization is { } pets ? pets with { Now = plan.PlannedAt } : null,
        };
        var rows = plan.Rows ?? throw new InvalidOperationException("A committing Mysterystore plan has no staged inventory.");
        var loadout = ReadLoadout(entry, scope);
        var allocate = InventoryMetadataPreparation.NeedsIdentity(inventory.UserNum, scope, rows,
            inventory.ArticleInitialization, inventory.PetInitialization);
        if (allocate) { lock (table.PetAllocatorGate) return Apply(); }
        return Apply();

        MysteryStoreResult Apply()
        {
            var lastId = table.LastPetEntitlementId;
            var lastArticle = table.LastPetArticleKey;
            var prepared = InventoryMetadataPreparation.Prepare(inventory.UserNum, scope, rows, loadout,
                inventory.ArticleInitialization, inventory.PetInitialization, () =>
                {
                    if (!allocate || lastArticle < PetArticleKeys.First - 1 || lastArticle >= PetArticleKeys.Last)
                        throw new InvalidOperationException("Native Article allocation is exhausted or corrupt.");
                    return new(checked(++lastId), checked(++lastArticle));
                });
            // The allocator wait may have outlived the selection; a paid change is never committed after its deadline.
            if (!plan.AdmissionStillValid(MysteryStoreClock.GetUtcNow()))
                return new(MysteryStoreOutcome.Expired, entry.MysteryStores.GetValueOrDefault(scope.Key), null, []);
            var snapshot = InventorySnapshot.Project(prepared.Rows, prepared.Loadout, scope);
            var granted = plan.GrantItemIds.Select(item => snapshot.CharacterBag.Single(row => row.ItemId == item)).ToArray();
            ct.ThrowIfCancellationRequested();
            CommitArticleRows(entry, scope, prepared.Rows, prepared.Loadout, ct);
            entry.LastPurchaseSlotKey = plan.LastSlotKey;
            entry.MysteryStores[scope.Key] = plan.Selection!;
            if (allocate) { table.LastPetEntitlementId = lastId; table.LastPetArticleKey = lastArticle; }
            return new(plan.Outcome, plan.Selection, snapshot, granted, plan.ReceiptNativeKey);
        }
    }

    private PlayerEntry RequireMysteryEntry(long userNum)
    {
        RequireUserNum(userNum);
        return table.Find(userNum) ?? throw new InvalidOperationException($"Player {userNum} has no stored account.");
    }

    // Tokens only grow: the stored selection holds the newest one, so the next is one past it (the planner refuses a wrap).
    private static uint NextToken(MysteryStoreSelection? current) =>
        current is null ? 1u : current.Token == uint.MaxValue ? uint.MaxValue : current.Token + 1;
}
