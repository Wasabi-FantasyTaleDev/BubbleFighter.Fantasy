using System.Globalization;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Microsoft.Data.Sqlite;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

internal sealed partial class SqliteRepositories : IPetEntitlementStore
{
    public ValueTask<PetInventorySnapshot> InitializePetsAsync(PetInitializationCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        return new(database.Write((db, transaction) =>
        {
            var state = db.RequireState(command.UserNum);
            var plan = PreparePetInventory(db, transaction, state, command.Scope, command);
            var result = PetInventorySnapshot.Project(plan.Rows, plan.Loadout, command.Scope);
            CommitPetPreparation(db, transaction, command.UserNum, state, command.Scope, plan, plan.Rows, plan.Loadout);
            return result;
        }, ct));
    }

    public ValueTask<PetInventorySnapshot> ReadPetInventoryAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        ArgumentNullException.ThrowIfNull(scope);
        return new(database.Write((db, _) =>
        {
            var state = db.RequireState(userNum);
            return PetInventorySnapshot.Project(state.Slots, ReadLoadout(state, scope), scope);
        }, ct));
    }

    public ValueTask<PetInventorySnapshot?> SelectPetAsync(PetEquipmentSelectionCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        return new(database.Write<PetInventorySnapshot?>((db, transaction) =>
        {
            var state = db.RequireState(command.UserNum);
            var plan = PreparePetInventory(db, transaction, state, command.Scope, command.Initialization, command.ArticleInitialization);
            var changed = PetEntitlementInventory.Select(command, plan.Rows, plan.Loadout);
            if (changed is null) return null;
            var loadout = InventoryLoadouts.Selection(plan.Loadout, changed);
            var result = PetInventorySnapshot.Project(changed, loadout, command.Scope);
            CommitPetPreparation(db, transaction, command.UserNum, state, command.Scope, plan, plan.Rows, loadout);
            return result;
        }, ct));
    }

    private PreparedPetInventory PreparePetInventory(SqliteDatabase db, SqliteTransaction transaction, SaveState state,
        InventoryLoadoutScope scope, PetInitializationCommand? initialization,
        ArticleInitializationCommand? articleInitialization = null, bool reserveIdentityAllocation = false)
    {
        var stored = state.Slots.ToArray();
        var loadout = ReadLoadout(state, scope);
        var allocate = reserveIdentityAllocation || (articleInitialization is not null
            ? InventoryMetadataPreparation.NeedsIdentity(articleInitialization.UserNum, scope, stored, articleInitialization, initialization)
            : initialization is not null && PetEntitlementInventory.NeedsIdentity(initialization, stored));
        ulong lastId = 0, lastArticle = 0;
        if (allocate)
        {
            using var query = db.RawCommand("SELECT entitlement_id,article_key FROM pet_identity_allocator WHERE singleton=1", transaction);
            using var reader = query.ExecuteReader();
            if (!reader.Read()) throw new InvalidDataException("Pet identity allocator is missing.");
            lastId = ulong.Parse(reader.GetString(0), NumberStyles.None, CultureInfo.InvariantCulture);
            lastArticle = ulong.Parse(reader.GetString(1), NumberStyles.None, CultureInfo.InvariantCulture);
        }
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
        loadout = metadata?.Loadout ?? (initialization is null ? loadout : InventoryLoadouts.MaterializePets(loadout, rows, scope));
        return new(stored, rows, loadout, allocate, lastId, lastArticle);
    }

    private static void CommitPetPreparation(SqliteDatabase db, SqliteTransaction transaction, long userNum, SaveState state,
        InventoryLoadoutScope scope, PreparedPetInventory plan, IReadOnlyList<InventorySlotRecord> rows, InventoryLoadoutState? loadout,
        long? lastSlotKey = null)
    {
        if (lastSlotKey < state.LastPurchaseSlotKey) throw new InvalidOperationException("Ordinary inventory keys cannot move backwards.");
        if (!plan.Stored.SequenceEqual(rows)
            || lastSlotKey is { } allocated && allocated != state.LastPurchaseSlotKey
            || loadout is not null && !InventoryLoadouts.SameState(state.Loadouts.GetValueOrDefault(scope.Key), loadout))
        {
            state.Slots = rows.ToList();
            if (loadout is not null) state.Loadouts[scope.Key] = loadout;
            if (lastSlotKey is { } highest) state.LastPurchaseSlotKey = highest;
            db.Save(userNum, state);
        }
        if (plan.Allocate && db.RawExecute("UPDATE pet_identity_allocator SET entitlement_id=$id,article_key=$article WHERE singleton=1", transaction,
            ("$id", plan.LastId.ToString(CultureInfo.InvariantCulture)), ("$article", plan.LastArticle.ToString(CultureInfo.InvariantCulture))) != 1)
            throw new InvalidDataException("Pet identity allocator disappeared during initialization.");
    }

    private sealed record PreparedPetInventory(IReadOnlyList<InventorySlotRecord> Stored,
        IReadOnlyList<InventorySlotRecord> Rows, InventoryLoadoutState Loadout, bool Allocate, ulong LastId, ulong LastArticle);
}
