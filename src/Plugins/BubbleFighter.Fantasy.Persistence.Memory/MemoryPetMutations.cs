using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.Memory;

public sealed partial class MemoryInventoryRepository : IPetMutationStore
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<InventorySlotRecord>?> MutatePetAsync(PetMutationCommand command, CancellationToken ct) =>
        (await MutatePetSnapshotAsync(command, ct).ConfigureAwait(false))?.CharacterBag;

    /// <inheritdoc />
    public async ValueTask<PetInventorySnapshot?> MutatePetSnapshotAsync(PetMutationCommand command, CancellationToken ct) =>
        (await MutatePetWithResultAsync(command, ct).ConfigureAwait(false))?.Snapshot;

    /// <inheritdoc />
    public ValueTask<PetMutationResult?> MutatePetWithResultAsync(PetMutationCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(command.ExpectedSlot.UserNum);
        if (entry is null) return ValueTask.FromResult<PetMutationResult?>(null);
        lock (entry.Gate)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(WithPetPreparation<PetMutationResult?>(entry, command.Scope, command.Initialization, plan =>
            {
                var changed = command.Plan(plan.Rows, plan.Loadout, entry.LastPurchaseSlotKey);
                if (changed is null) return null;
                var finalRows = changed.Rows;
                if (command.ArticleInitialization is not null)
                {
                    var lastId = plan.LastId;
                    var lastArticle = plan.LastArticle;
                    // Reward grants are resolved before Article preparation so their expected rows are not changed by our own staging.
                    var prepared = InventoryMetadataPreparation.Prepare(command.ExpectedSlot.UserNum, command.Scope,
                        changed.Rows, plan.Loadout, command.ArticleInitialization, null, () =>
                        {
                            if (!plan.Allocate || lastArticle < PetArticleKeys.First - 1 || lastArticle >= PetArticleKeys.Last)
                                throw new InvalidOperationException("Native Article allocation is exhausted or corrupt.");
                            return new(checked(++lastId), checked(++lastArticle));
                        });
                    finalRows = prepared.Rows;
                    plan = plan with { Rows = prepared.Rows, Loadout = prepared.Loadout, LastId = lastId, LastArticle = lastArticle };
                }
                var result = new PetMutationResult(PetInventorySnapshot.Project(finalRows, plan.Loadout, command.Scope), changed.RewardItemIds);
                CommitPetPreparation(entry, command.Scope, plan, finalRows,
                    command.Initialization is null && command.ArticleInitialization is null ? null : plan.Loadout, ct, command.Mileage is null ? null : changed.LastSlotKey);
                return result;
            }, reserveIdentityAllocation: command.ArticleInitialization is not null));
        }
    }
}
