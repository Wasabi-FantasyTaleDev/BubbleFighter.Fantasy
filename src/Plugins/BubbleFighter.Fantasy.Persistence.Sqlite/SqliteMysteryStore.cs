using System.Globalization;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.MysteryStore;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

internal sealed partial class SqliteRepositories : IMysteryStore
{
    /// <summary>Gets the clock the Mysterystore reads inside its write transaction; tests replace it.</summary>
    internal TimeProvider MysteryStoreClock { get; init; } = TimeProvider.System;

    ValueTask<MysteryStoreResult> IMysteryStore.EnsureAsync(MysteryStoreEnsureCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new(database.Write((db, _) =>
        {
            var state = db.RequireState(command.UserNum);
            var current = ReadSelection(state, command.UserNum, command.Scope.Key);
            var plan = MysteryStorePlanner.Ensure(command, current, NextToken(current), MysteryStoreClock.GetUtcNow());
            if (plan.RequiresCommit)
            {
                if (current is null && state.MysteryStores.Count >= database.Options.MaxLoadoutsPerPlayer)
                    throw new InvalidOperationException("The player's mystery store scope limit is exhausted.");
                state.MysteryStores[command.Scope.Key] = Store(plan.Selection!);
                db.Save(command.UserNum, state);
            }
            return new MysteryStoreResult(plan.Outcome, plan.Selection, null, []);
        }, ct));
    }

    ValueTask<MysteryStoreResult> IMysteryStore.RefreshAsync(MysteryStoreRefreshCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Inventory);
        return new(database.Write((db, transaction) =>
        {
            var inventory = command.Inventory;
            var state = db.RequireState(inventory.UserNum);
            var current = ReadSelection(state, inventory.UserNum, inventory.Scope.Key);
            var plan = MysteryStorePlanner.Refresh(command, current, state.Slots, ReadLoadout(state, inventory.Scope),
                state.LastPurchaseSlotKey, NextToken(current), MysteryStoreClock.GetUtcNow());
            return Commit(db, transaction, state, inventory, plan);
        }, ct));
    }

    ValueTask<MysteryStoreResult> IMysteryStore.PurchaseAsync(MysteryStorePurchaseCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Inventory);
        return new(database.Write((db, transaction) =>
        {
            var inventory = command.Inventory;
            var state = db.RequireState(inventory.UserNum);
            var plan = MysteryStorePlanner.Purchase(command, ReadSelection(state, inventory.UserNum, inventory.Scope.Key),
                state.Slots, ReadLoadout(state, inventory.Scope), state.LastPurchaseSlotKey, MysteryStoreClock.GetUtcNow());
            return Commit(db, transaction, state, inventory, plan);
        }, ct));
    }

    // Inside the write transaction: prepares metadata for the staged rows and saves rows, loadout, watermark, allocator
    // and selection together; a refusal or replay saves nothing.
    private MysteryStoreResult Commit(SqliteDatabase db, Microsoft.Data.Sqlite.SqliteTransaction transaction,
        SaveState state, InventoryMetadataPreparationCommand inventory, MysteryStorePlan plan)
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
        var allocate = InventoryMetadataPreparation.NeedsIdentity(inventory.UserNum, scope, rows,
            inventory.ArticleInitialization, inventory.PetInitialization);
        ulong lastId = 0, lastArticle = 0;
        if (allocate)
        {
            using var query = db.RawCommand("SELECT entitlement_id,article_key FROM pet_identity_allocator WHERE singleton=1", transaction);
            using var reader = query.ExecuteReader();
            if (!reader.Read()) throw new InvalidDataException("Shared Article allocator is missing.");
            lastId = ulong.Parse(reader.GetString(0), NumberStyles.None, CultureInfo.InvariantCulture);
            lastArticle = ulong.Parse(reader.GetString(1), NumberStyles.None, CultureInfo.InvariantCulture);
        }
        var prepared = InventoryMetadataPreparation.Prepare(inventory.UserNum, scope, rows, ReadLoadout(state, scope),
            inventory.ArticleInitialization, inventory.PetInitialization, () =>
            {
                if (!allocate || lastArticle < PetArticleKeys.First - 1 || lastArticle >= PetArticleKeys.Last)
                    throw new InvalidOperationException("Native Article allocation is exhausted or corrupt.");
                return new(checked(++lastId), checked(++lastArticle));
            });
        if (!plan.AdmissionStillValid(MysteryStoreClock.GetUtcNow()))
            return new(MysteryStoreOutcome.Expired, ReadSelection(state, inventory.UserNum, scope.Key), null, []);
        var snapshot = InventorySnapshot.Project(prepared.Rows, prepared.Loadout, scope);
        var granted = plan.GrantItemIds.Select(item => snapshot.CharacterBag.Single(row => row.ItemId == item)).ToArray();
        state.Slots = prepared.Rows.ToList();
        state.Loadouts[scope.Key] = prepared.Loadout;
        state.LastPurchaseSlotKey = plan.LastSlotKey;
        state.MysteryStores[scope.Key] = Store(plan.Selection!);
        db.Save(inventory.UserNum, state);
        if (allocate && db.RawExecute("UPDATE pet_identity_allocator SET entitlement_id=$id,article_key=$article WHERE singleton=1", transaction,
            ("$id", lastId.ToString(CultureInfo.InvariantCulture)), ("$article", lastArticle.ToString(CultureInfo.InvariantCulture))) != 1)
            throw new InvalidDataException("Shared Article allocator disappeared during a Mysterystore mutation.");
        return new(plan.Outcome, plan.Selection, snapshot, granted, plan.ReceiptNativeKey);
    }

    private static MysteryStoreSelection? ReadSelection(SaveState state, long userNum, string scopeKey) =>
        state.MysteryStores.TryGetValue(scopeKey, out var stored)
            ? new(userNum, scopeKey, stored.Token, stored.ExpiresAtUnixMilliseconds,
                MysteryStoreSelectionJson.Decode(stored.Board), stored.PurchasedMask)
            : null;

    private static SqliteMysteryStoreSelection Store(MysteryStoreSelection selection) => new()
    {
        Token = selection.Token, ExpiresAtUnixMilliseconds = selection.ExpiresAtUnixMilliseconds,
        PurchasedMask = selection.PurchasedMask, Board = MysteryStoreSelectionJson.Encode(selection.Board),
    };

    // Tokens only grow: the stored selection holds the newest one (the planner refuses a wrap).
    private static uint NextToken(MysteryStoreSelection? current) =>
        current is null ? 1u : current.Token == uint.MaxValue ? uint.MaxValue : current.Token + 1;
}
