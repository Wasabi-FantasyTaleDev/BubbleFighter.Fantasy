using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.MySql;

public sealed partial class MySqlInventoryRepository : ILaboratoryInventoryStore
{
    /// <inheritdoc />
    public ValueTask<InventorySnapshot> ReadLaboratoryInventoryAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct) =>
        ReadEnhancementInventoryAsync(userNum, scope, ct);

    /// <inheritdoc />
    public async ValueTask<InventoryLaboratoryResult?> TryLaboratoryAsync(InventoryLaboratoryCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var transaction = await session.BeginWriteAsync(command.UserNum, authority, ct).ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, command.UserNum, ct).ConfigureAwait(false);
        var (loadout, loadoutExists) = await ReadLoadoutAsync(db, command.UserNum, command.Scope, ct).ConfigureAwait(false);
        var stored = await ReadOwnedInventoryRowsAsync(db, command.UserNum, ct).ConfigureAwait(false);
        var keys = await MySqlInventorySlotKeys.ReadAsync(db, command.UserNum, ct).ConfigureAwait(false);
        var plan = command.Plan(stored, loadout, keys.Highest);
        if (plan is null) return null;
        var prepared = await PreparePetInventoryAsync(db, plan.Rows, loadout, command.Scope,
            command.PetInitialization, ct, command.ArticleInitialization).ConfigureAwait(false);
        // Preparation may wait on the shared allocator lock; a conversion timer is judged again after that wait.
        if (!command.ConversionAdmitted(stored, loadout)) return null;
        prepared = prepared with { Stored = stored };
        var inventory = InventorySnapshot.Project(prepared.Rows, prepared.Loadout, command.Scope);
        var result = new InventoryLaboratoryResult(inventory, command.Grants.Select(grant =>
            inventory.CharacterBag.Single(row => row.ItemId == grant.Term.ItemId)).ToArray());
        await CommitPetPreparationAsync(db, command.UserNum, command.Scope, prepared, prepared.Rows,
            prepared.Loadout, loadoutExists, ct).ConfigureAwait(false);
        await MySqlInventorySlotKeys.AdvanceAsync(db, command.UserNum, keys.Stored, plan.LastSlotKey, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return result;
    }
}
