using System.Globalization;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Microsoft.Data.Sqlite;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

internal sealed partial class SqliteRepositories : IItemEnhancementStore
{
    public ValueTask<InventorySnapshot> InitializeArticlesAsync(ArticleInitializationCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        return new(database.Write((db, transaction) =>
        {
            var state = db.RequireState(command.UserNum);
            var allocate = InventoryArticles.NeedsIdentity(command, state.Slots);
            ulong lastId = 0, lastArticle = 0;
            if (allocate)
            {
                using var query = db.RawCommand("SELECT entitlement_id,article_key FROM pet_identity_allocator WHERE singleton=1", transaction);
                using var reader = query.ExecuteReader();
                if (!reader.Read()) throw new InvalidDataException("Shared Article allocator is missing.");
                lastId = ulong.Parse(reader.GetString(0), NumberStyles.None, CultureInfo.InvariantCulture);
                lastArticle = ulong.Parse(reader.GetString(1), NumberStyles.None, CultureInfo.InvariantCulture);
            }
            var rows = InventoryArticles.Initialize(command, state.Slots, () =>
            {
                if (!allocate || lastArticle < PetArticleKeys.First - 1 || lastArticle >= PetArticleKeys.Last)
                    throw new InvalidOperationException("Native Article allocation is exhausted or corrupt.");
                return new(checked(++lastId), checked(++lastArticle));
            });
            var loadout = InventoryLoadouts.MaterializeArticles(ReadLoadout(state, command.Scope), rows, command.Scope);
            var result = InventorySnapshot.Project(rows, loadout, command.Scope);
            state.Slots = rows.ToList();
            state.Loadouts[command.Scope.Key] = loadout;
            db.Save(command.UserNum, state);
            if (allocate && db.RawExecute("UPDATE pet_identity_allocator SET entitlement_id=$id,article_key=$article WHERE singleton=1", transaction,
                ("$id", lastId.ToString(CultureInfo.InvariantCulture)), ("$article", lastArticle.ToString(CultureInfo.InvariantCulture))) != 1)
                throw new InvalidDataException("Shared Article allocator disappeared during initialization.");
            return result;
        }, ct));
    }

    public ValueTask<InventorySnapshot> ReadEnhancementInventoryAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        ArgumentNullException.ThrowIfNull(scope);
        return new(database.Write((db, _) =>
        {
            var state = db.RequireState(userNum);
            return InventorySnapshot.Project(state.Slots, ReadLoadout(state, scope), scope);
        }, ct));
    }

    public ValueTask<InventorySnapshot?> TryEnhanceAsync(ItemEnhancementCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        return new(database.Write<InventorySnapshot?>((db, transaction) =>
        {
            var state = db.RequireState(command.UserNum);
            return MutateArticles(db, transaction, state, command.UserNum, command.Scope,
                command.ArticleInitialization, command.PetInitialization, prepared =>
                {
                    var rows = command.Apply(prepared.Rows, prepared.Loadout);
                    return rows is null ? null : new ArticleMutation(rows, prepared.Loadout);
                });
        }, ct));
    }

    public ValueTask<InventorySnapshot?> SelectArticleAsync(ArticleSelectionCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        return new(database.Write<InventorySnapshot?>((db, transaction) =>
        {
            var state = db.RequireState(command.UserNum);
            return MutateArticles(db, transaction, state, command.UserNum, command.Scope,
                command.ArticleInitialization, command.PetInitialization, prepared =>
                {
                    var selected = InventoryArticles.Select(command, prepared.Rows, prepared.Loadout);
                    return selected is null ? null : new ArticleMutation(prepared.Rows, InventoryLoadouts.Selection(prepared.Loadout, selected));
                });
        }, ct));
    }

    private InventorySnapshot? MutateArticles(SqliteDatabase db, SqliteTransaction transaction, SaveState state,
        long userNum, InventoryLoadoutScope scope, ArticleInitializationCommand? articles, PetInitializationCommand? pets,
        Func<PreparedInventoryMetadata, ArticleMutation?> operation)
    {
        var allocate = InventoryMetadataPreparation.NeedsIdentity(userNum, scope, state.Slots, articles, pets);
        ulong lastId = 0, lastArticle = 0;
        if (allocate)
        {
            using var query = db.RawCommand("SELECT entitlement_id,article_key FROM pet_identity_allocator WHERE singleton=1", transaction);
            using var reader = query.ExecuteReader();
            if (!reader.Read()) throw new InvalidDataException("Shared Article allocator is missing.");
            lastId = ulong.Parse(reader.GetString(0), NumberStyles.None, CultureInfo.InvariantCulture);
            lastArticle = ulong.Parse(reader.GetString(1), NumberStyles.None, CultureInfo.InvariantCulture);
        }
        var prepared = InventoryMetadataPreparation.Prepare(userNum, scope, state.Slots, ReadLoadout(state, scope), articles, pets, () =>
        {
            if (!allocate || lastArticle < PetArticleKeys.First - 1 || lastArticle >= PetArticleKeys.Last)
                throw new InvalidOperationException("Native Article allocation is exhausted or corrupt.");
            return new(checked(++lastId), checked(++lastArticle));
        });
        var changed = operation(prepared);
        if (changed is null) return null;
        var result = InventorySnapshot.Project(changed.Rows, changed.Loadout, scope);
        state.Slots = changed.Rows.ToList();
        state.Loadouts[scope.Key] = changed.Loadout;
        db.Save(userNum, state);
        if (allocate && db.RawExecute("UPDATE pet_identity_allocator SET entitlement_id=$id,article_key=$article WHERE singleton=1", transaction,
            ("$id", lastId.ToString(CultureInfo.InvariantCulture)), ("$article", lastArticle.ToString(CultureInfo.InvariantCulture))) != 1)
            throw new InvalidDataException("Shared Article allocator disappeared during mutation.");
        return result;
    }

    private sealed record ArticleMutation(IReadOnlyList<InventorySlotRecord> Rows, InventoryLoadoutState Loadout);
}
