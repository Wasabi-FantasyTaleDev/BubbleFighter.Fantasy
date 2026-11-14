using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.Memory;

public sealed partial class MemoryInventoryRepository : ILaboratoryInventoryStore
{
    /// <inheritdoc />
    public ValueTask<InventorySnapshot> ReadLaboratoryInventoryAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct) =>
        ReadEnhancementInventoryAsync(userNum, scope, ct);

    /// <inheritdoc />
    public ValueTask<InventoryLaboratoryResult?> TryLaboratoryAsync(InventoryLaboratoryCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(command.UserNum);
        if (entry is null) return ValueTask.FromResult<InventoryLaboratoryResult?>(null);
        lock (entry.Gate)
        {
            var loadout = ReadLoadout(entry, command.Scope);
            var plan = command.Plan(ArticleRows(entry), loadout, entry.LastPurchaseSlotKey);
            if (plan is null) return ValueTask.FromResult<InventoryLaboratoryResult?>(null);
            var allocate = InventoryMetadataPreparation.NeedsIdentity(command.UserNum, command.Scope, plan.Rows,
                command.ArticleInitialization, command.PetInitialization);
            if (allocate) { lock (table.PetAllocatorGate) return Commit(); }
            return Commit();

            ValueTask<InventoryLaboratoryResult?> Commit()
            {
                var lastId = table.LastPetEntitlementId;
                var lastArticle = table.LastPetArticleKey;
                var prepared = InventoryMetadataPreparation.Prepare(command.UserNum, command.Scope, plan.Rows, loadout,
                    command.ArticleInitialization, command.PetInitialization, () =>
                    {
                        if (!allocate || lastArticle < PetArticleKeys.First - 1 || lastArticle >= PetArticleKeys.Last)
                            throw new InvalidOperationException("Native Article allocation is exhausted or corrupt.");
                        return new(checked(++lastId), checked(++lastArticle));
                    });
                var inventory = InventorySnapshot.Project(prepared.Rows, prepared.Loadout, command.Scope);
                var result = new InventoryLaboratoryResult(inventory, command.Grants.Select(grant =>
                    inventory.CharacterBag.Single(row => row.ItemId == grant.Term.ItemId)).ToArray());
                CommitArticleRows(entry, command.Scope, prepared.Rows, prepared.Loadout, ct);
                entry.LastPurchaseSlotKey = plan.LastSlotKey;
                if (allocate) { table.LastPetEntitlementId = lastId; table.LastPetArticleKey = lastArticle; }
                return ValueTask.FromResult<InventoryLaboratoryResult?>(result);
            }
        }
    }
}
