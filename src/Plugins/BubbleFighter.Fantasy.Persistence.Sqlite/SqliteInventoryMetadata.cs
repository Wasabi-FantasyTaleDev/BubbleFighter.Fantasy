using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

internal sealed partial class SqliteRepositories : IInventoryMetadataStore
{
    public ValueTask<InventorySnapshot> PrepareInventorySnapshotAsync(InventoryMetadataPreparationCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        return new(database.Write((db, transaction) =>
        {
            var state = db.RequireState(command.UserNum);
            return MutateArticles(db, transaction, state, command.UserNum, command.Scope,
                command.ArticleInitialization, command.PetInitialization,
                prepared => new(prepared.Rows, prepared.Loadout))
                ?? throw new InvalidOperationException("Metadata preparation unexpectedly refused its own snapshot.");
        }, ct));
    }
}
