using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.MySql;

public sealed partial class MySqlInventoryRepository : IInventoryMetadataStore
{
    /// <inheritdoc />
    public async ValueTask<InventorySnapshot> PrepareInventorySnapshotAsync(InventoryMetadataPreparationCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var transaction = await session.BeginWriteAsync(command.UserNum, authority, ct).ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, command.UserNum, ct).ConfigureAwait(false);
        var (loadout, exists) = await ReadLoadoutAsync(db, command.UserNum, command.Scope, ct).ConfigureAwait(false);
        var stored = await ReadOwnedInventoryRowsAsync(db, command.UserNum, ct).ConfigureAwait(false);
        var snapshot = await MutateArticlesAsync(db, command.UserNum, command.Scope, stored, loadout, exists,
            command.ArticleInitialization, command.PetInitialization,
            prepared => new(prepared.Rows, prepared.Loadout), ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Metadata preparation unexpectedly refused its own snapshot.");
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return snapshot;
    }
}
