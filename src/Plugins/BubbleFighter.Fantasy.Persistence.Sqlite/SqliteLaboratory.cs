using System.Globalization;
using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

internal sealed partial class SqliteRepositories : ILaboratoryInventoryStore
{
    public ValueTask<InventorySnapshot> ReadLaboratoryInventoryAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct) =>
        ReadEnhancementInventoryAsync(userNum, scope, ct);

    public ValueTask<InventoryLaboratoryResult?> TryLaboratoryAsync(InventoryLaboratoryCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        return new(database.Write<InventoryLaboratoryResult?>((db, transaction) =>
        {
            var state = db.RequireState(command.UserNum);
            var loadout = ReadLoadout(state, command.Scope);
            var plan = command.Plan(state.Slots, loadout, state.LastPurchaseSlotKey);
            if (plan is null) return null;
            var allocate = InventoryMetadataPreparation.NeedsIdentity(command.UserNum, command.Scope, plan.Rows,
                command.ArticleInitialization, command.PetInitialization);
            ulong lastId = 0, lastArticle = 0;
            if (allocate)
            {
                using var query = db.RawCommand("SELECT entitlement_id,article_key FROM pet_identity_allocator WHERE singleton=1", transaction);
                using var reader = query.ExecuteReader();
                if (!reader.Read()) throw new InvalidDataException("Shared Article allocator is missing.");
                lastId = ulong.Parse(reader.GetString(0), NumberStyles.None, CultureInfo.InvariantCulture);
                lastArticle = ulong.Parse(reader.GetString(1), NumberStyles.None, CultureInfo.InvariantCulture);
            }
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
            state.Slots = prepared.Rows.ToList();
            state.Loadouts[command.Scope.Key] = prepared.Loadout;
            state.LastPurchaseSlotKey = plan.LastSlotKey;
            db.Save(command.UserNum, state);
            if (allocate && db.RawExecute("UPDATE pet_identity_allocator SET entitlement_id=$id,article_key=$article WHERE singleton=1", transaction,
                ("$id", lastId.ToString(CultureInfo.InvariantCulture)), ("$article", lastArticle.ToString(CultureInfo.InvariantCulture))) != 1)
                throw new InvalidDataException("Shared Article allocator disappeared during laboratory mutation.");
            return result;
        }, ct));
    }
}
