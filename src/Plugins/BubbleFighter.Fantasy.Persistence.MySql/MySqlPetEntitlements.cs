using System.Globalization;
using BubbleFighter.Fantasy.Contracts.Inventory;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

public sealed partial class MySqlInventoryRepository : IPetEntitlementStore
{
    /// <inheritdoc />
    public async ValueTask<PetInventorySnapshot> InitializePetsAsync(PetInitializationCommand command, CancellationToken ct)
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
        var plan = await PreparePetInventoryAsync(db, stored, loadout, command.Scope, command, ct).ConfigureAwait(false);
        var result = PetInventorySnapshot.Project(plan.Rows, plan.Loadout, command.Scope);
        await CommitPetPreparationAsync(db, command.UserNum, command.Scope, plan, plan.Rows, plan.Loadout, exists, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<PetInventorySnapshot> ReadPetInventoryAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ArgumentNullException.ThrowIfNull(scope);
        ct.ThrowIfCancellationRequested();
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginAsync().ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(session.Db, userNum, ct).ConfigureAwait(false);
        var (state, _) = await ReadLoadoutAsync(session.Db, userNum, scope, ct).ConfigureAwait(false);
        var result = PetInventorySnapshot.Project(await ReadOwnedInventoryRowsAsync(session.Db, userNum, ct).ConfigureAwait(false), state, scope);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<PetInventorySnapshot?> SelectPetAsync(PetEquipmentSelectionCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Validate();
        ct.ThrowIfCancellationRequested();
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginWriteAsync(command.UserNum, authority, ct).ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(session.Db, command.UserNum, ct).ConfigureAwait(false);
        var (state, exists) = await ReadLoadoutAsync(session.Db, command.UserNum, command.Scope, ct).ConfigureAwait(false);
        var stored = await ReadOwnedInventoryRowsAsync(session.Db, command.UserNum, ct).ConfigureAwait(false);
        var plan = await PreparePetInventoryAsync(session.Db, stored, state, command.Scope, command.Initialization, ct, command.ArticleInitialization).ConfigureAwait(false);
        var changed = PetEntitlementInventory.Select(command, plan.Rows, plan.Loadout);
        if (changed is null) return null;
        state = InventoryLoadouts.Selection(plan.Loadout, changed);
        var result = PetInventorySnapshot.Project(changed, state, command.Scope);
        await CommitPetPreparationAsync(session.Db, command.UserNum, command.Scope, plan, plan.Rows, state, exists, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return result;
    }

    private static async ValueTask<PreparedPetInventory> PreparePetInventoryAsync(SqlSugarClient db,
        IReadOnlyList<InventorySlotRecord> stored, InventoryLoadoutState loadout, InventoryLoadoutScope scope,
        PetInitializationCommand? initialization, CancellationToken ct, ArticleInitializationCommand? articleInitialization = null, bool reserveIdentityAllocation = false)
    {
        var allocate = reserveIdentityAllocation || (articleInitialization is not null
            ? InventoryMetadataPreparation.NeedsIdentity(articleInitialization.UserNum, scope, stored, articleInitialization, initialization)
            : initialization is not null && PetEntitlementInventory.NeedsIdentity(initialization, stored));
        ulong lastId = 0, lastArticle = 0;
        // Decimal text avoids any intermediate signed Int64 or floating-point conversion in the ORM.
        if (allocate)
        {
            var counters = Convert.ToString(await db.Ado.GetScalarAsync(
                "SELECT CONCAT(CAST(entitlement_id AS CHAR),',',CAST(article_key AS CHAR)) FROM pet_identity_allocator WHERE singleton=1 FOR UPDATE",
                new List<SugarParameter>(), ct).ConfigureAwait(false), CultureInfo.InvariantCulture)?.Split(',');
            if (counters is not { Length: 2 }) throw new InvalidDataException("Pet identity allocator is missing or malformed.");
            lastId = ulong.Parse(counters[0], NumberStyles.None, CultureInfo.InvariantCulture);
            lastArticle = ulong.Parse(counters[1], NumberStyles.None, CultureInfo.InvariantCulture);
        }
        var allocatorStartId = lastId;
        var allocatorStartArticle = lastArticle;
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
        var preparedLoadout = metadata?.Loadout ?? (initialization is null ? loadout : InventoryLoadouts.MaterializePets(loadout, rows, scope));
        return new(stored, rows, loadout, preparedLoadout, allocate, lastId, lastArticle, allocatorStartId, allocatorStartArticle);
    }

    private async ValueTask CommitPetPreparationAsync(SqlSugarClient db, long userNum, InventoryLoadoutScope scope,
        PreparedPetInventory plan, IReadOnlyList<InventorySlotRecord> rows, InventoryLoadoutState? loadout, bool exists, CancellationToken ct)
    {
        if (!plan.Stored.SequenceEqual(rows))
            await ReplaceOwnedInventoryRowsAsync(db, userNum, plan.Stored.Count, rows, ct).ConfigureAwait(false);
        if (loadout is not null && (!exists || !InventoryLoadouts.SameState(plan.OriginalLoadout, loadout)))
            await SaveLoadoutAsync(db, userNum, scope.Key, loadout, exists, ct).ConfigureAwait(false);
        if (plan.Allocate && (plan.LastId != plan.AllocatorStartId || plan.LastArticle != plan.AllocatorStartArticle))
        {
            var written = await db.Ado.ExecuteCommandAsync(
                "UPDATE pet_identity_allocator SET entitlement_id=CAST(@id AS UNSIGNED),article_key=CAST(@article AS UNSIGNED) WHERE singleton=1",
                new List<SugarParameter> { new("@id", plan.LastId.ToString(CultureInfo.InvariantCulture)),
                    new("@article", plan.LastArticle.ToString(CultureInfo.InvariantCulture)) }, ct).ConfigureAwait(false);
            if (written != 1) throw new InvalidDataException("Pet identity allocator update affected an invalid row count.");
        }
    }

    private sealed record PreparedPetInventory(IReadOnlyList<InventorySlotRecord> Stored,
        IReadOnlyList<InventorySlotRecord> Rows, InventoryLoadoutState OriginalLoadout, InventoryLoadoutState Loadout,
        bool Allocate, ulong LastId, ulong LastArticle, ulong AllocatorStartId, ulong AllocatorStartArticle);

    private async ValueTask<IReadOnlyList<InventorySlotRecord>> ReadOwnedInventoryRowsAsync(SqlSugarClient db, long userNum, CancellationToken ct)
    {
        var rows = await db.Queryable<InventorySlotRow>()
            .Where(row => row.UserNum == userNum && (row.Bag == 0 || row.Bag == 1))
            .OrderBy(row => row.Bag).OrderBy(row => row.Ordinal)
            .Take(2 * InventoryLimits.MaxInventorySlotsPerBag + 1).TranLock(DbLockType.Wait).ToListAsync(ct).ConfigureAwait(false);
        var result = rows.Select(ReadRecord).ToArray();
        PetEntitlementInventory.ValidateRows(userNum, result);
        return result;
    }

    private async ValueTask ReplaceOwnedInventoryRowsAsync(SqlSugarClient db, long userNum, int originalCount,
        IReadOnlyList<InventorySlotRecord> rows, CancellationToken ct)
    {
        foreach (var row in rows)
        {
            row.Pet?.Validate(maxLoadoutsPerPlayer);
            row.Enhancements?.Validate(maxLoadoutsPerPlayer);
            row.Modifications?.Validate(maxLoadoutsPerPlayer);
            row.Favorites?.Validate(maxLoadoutsPerPlayer);
        }
        PetEntitlementInventory.ValidateRows(userNum, rows);
        var replacements = rows.GroupBy(row => row.Bag).SelectMany(bag => bag.Select((row, ordinal) => ToRow(row, ordinal))).ToArray();
        var removed = await db.Deleteable<InventorySlotRow>().Where(row => row.UserNum == userNum && (row.Bag == 0 || row.Bag == 1))
            .ExecuteCommandAsync(ct).ConfigureAwait(false);
        if (removed != originalCount) throw new InvalidOperationException("Pet inventory changed under its account lock.");
        if (replacements.Length > 0 && await db.Insertable(replacements).ExecuteCommandAsync(ct).ConfigureAwait(false) != replacements.Length)
            throw new InvalidOperationException("Pet inventory replacement was incomplete.");
    }

    private static PetEntitlementIdentity? DecodePetIdentity(string? entitlement, string? article)
    {
        if (entitlement is null && article is null) return null;
        if (entitlement is null || article is null) throw new InvalidDataException("Stored pet identity has only one of its keys.");
        var identity = new PetEntitlementIdentity(ulong.Parse(entitlement, NumberStyles.None, CultureInfo.InvariantCulture),
            ulong.Parse(article, NumberStyles.None, CultureInfo.InvariantCulture));
        identity.Validate();
        return identity;
    }
}
