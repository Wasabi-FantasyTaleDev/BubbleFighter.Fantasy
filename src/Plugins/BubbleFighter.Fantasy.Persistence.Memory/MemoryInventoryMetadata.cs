using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.Memory;

public sealed partial class MemoryInventoryRepository : IInventoryMetadataStore
{
    /// <inheritdoc />
    public ValueTask<InventorySnapshot> PrepareInventorySnapshotAsync(InventoryMetadataPreparationCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        var entry = table.Find(command.UserNum) ?? throw new InvalidOperationException("The inventory owner does not exist.");
        lock (entry.Gate)
        {
            var snapshot = MutateArticles(entry, command.UserNum, command.Scope,
                command.ArticleInitialization, command.PetInitialization,
                prepared => new(prepared.Rows, prepared.Loadout), ct);
            return ValueTask.FromResult(snapshot ?? throw new InvalidOperationException("Metadata preparation unexpectedly refused its own snapshot."));
        }
    }
}
