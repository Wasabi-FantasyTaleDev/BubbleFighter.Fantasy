using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.MysteryStore;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

public sealed partial class MySqlInventoryRepository : IMysteryStore
{
    /// <summary>Gets the clock the Mysterystore reads after taking the player lock; tests replace it.</summary>
    public TimeProvider MysteryStoreClock { get; init; } = TimeProvider.System;

    /// <inheritdoc />
    async ValueTask<MysteryStoreResult> IMysteryStore.EnsureAsync(MysteryStoreEnsureCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ct.ThrowIfCancellationRequested();
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var transaction = await session.BeginWriteAsync(command.UserNum, authority, ct).ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, command.UserNum, ct).ConfigureAwait(false);
        var (current, exists) = await ReadSelectionAsync(db, command.UserNum, command.Scope.Key, ct).ConfigureAwait(false);
        var plan = MysteryStorePlanner.Ensure(command, current, NextMysteryToken(current), MysteryStoreClock.GetUtcNow());
        if (plan.RequiresCommit)
        {
            if (!exists && await db.Queryable<MysteryStoreSelectionRow>()
                .Where(row => row.UserNum == command.UserNum).CountAsync(ct).ConfigureAwait(false) >= maxLoadoutsPerPlayer)
                throw new InvalidOperationException("The player's mystery store scope limit is exhausted.");
            await WriteSelectionAsync(db, plan.Selection!, exists, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync().ConfigureAwait(false);
        }
        return new(plan.Outcome, plan.Selection, null, []);
    }

    /// <inheritdoc />
    async ValueTask<MysteryStoreResult> IMysteryStore.RefreshAsync(MysteryStoreRefreshCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Inventory);
        return await MutateAsync(command.Inventory, (current, stored, loadout, lastSlotKey, now) =>
            MysteryStorePlanner.Refresh(command, current, stored, loadout, lastSlotKey, NextMysteryToken(current), now), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    async ValueTask<MysteryStoreResult> IMysteryStore.PurchaseAsync(MysteryStorePurchaseCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Inventory);
        return await MutateAsync(command.Inventory, (current, stored, loadout, lastSlotKey, now) =>
            MysteryStorePlanner.Purchase(command, current, stored, loadout, lastSlotKey, now), ct).ConfigureAwait(false);
    }

    // One account transaction: lock, read selection, inventory, loadout and slot watermark, plan, prepare metadata,
    // recheck the deadline after any allocator wait, then write everything or nothing.
    private async ValueTask<MysteryStoreResult> MutateAsync(InventoryMetadataPreparationCommand inventory,
        Func<MysteryStoreSelection?, IReadOnlyList<InventorySlotRecord>, InventoryLoadoutState, long, DateTimeOffset, MysteryStorePlan> planner,
        CancellationToken ct)
    {
        inventory.Validate();
        ct.ThrowIfCancellationRequested();
        var scope = inventory.Scope;
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var db = session.Db;
        await using var transaction = await session.BeginWriteAsync(inventory.UserNum, authority, ct).ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(db, inventory.UserNum, ct).ConfigureAwait(false);
        var (current, exists) = await ReadSelectionAsync(db, inventory.UserNum, scope.Key, ct).ConfigureAwait(false);
        var (loadout, loadoutExists) = await ReadLoadoutAsync(db, inventory.UserNum, scope, ct).ConfigureAwait(false);
        var stored = await ReadOwnedInventoryRowsAsync(db, inventory.UserNum, ct).ConfigureAwait(false);
        var keys = await MySqlInventorySlotKeys.ReadAsync(db, inventory.UserNum, ct).ConfigureAwait(false);
        var plan = planner(current, stored, loadout, keys.Highest, MysteryStoreClock.GetUtcNow());
        if (!plan.RequiresCommit)
            return new(plan.Outcome, plan.Selection, plan.ReplayInventory, [], plan.ReceiptNativeKey, plan.GrantRefusal);
        inventory = inventory with
        {
            ArticleInitialization = inventory.ArticleInitialization is { } articles ? articles with { Now = plan.PlannedAt } : null,
            PetInitialization = inventory.PetInitialization is { } pets ? pets with { Now = plan.PlannedAt } : null,
        };
        var rows = plan.Rows ?? throw new InvalidOperationException("A committing Mysterystore plan has no staged inventory.");
        var prepared = await PreparePetInventoryAsync(db, rows, loadout, scope, inventory.PetInitialization, ct,
            inventory.ArticleInitialization).ConfigureAwait(false);
        if (!plan.AdmissionStillValid(MysteryStoreClock.GetUtcNow()))
            return new(MysteryStoreOutcome.Expired, current, null, []);
        prepared = prepared with { Stored = stored };
        var snapshot = InventorySnapshot.Project(prepared.Rows, prepared.Loadout, scope);
        var granted = plan.GrantItemIds.Select(item => snapshot.CharacterBag.Single(row => row.ItemId == item)).ToArray();
        await CommitPetPreparationAsync(db, inventory.UserNum, scope, prepared, prepared.Rows, prepared.Loadout,
            loadoutExists, ct).ConfigureAwait(false);
        await MySqlInventorySlotKeys.AdvanceAsync(db, inventory.UserNum, keys.Stored, plan.LastSlotKey, ct).ConfigureAwait(false);
        await WriteSelectionAsync(db, plan.Selection!, exists, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return new(plan.Outcome, plan.Selection, snapshot, granted, plan.ReceiptNativeKey);
    }

    private static async ValueTask<(MysteryStoreSelection? Selection, bool Exists)> ReadSelectionAsync(SqlSugarClient db,
        long userNum, string scopeKey, CancellationToken ct)
    {
        var rows = await db.Queryable<MysteryStoreSelectionRow>()
            .Where(row => row.UserNum == userNum && row.BuildKey == scopeKey).Take(1)
            .ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count == 0) return (null, false);
        var row = rows[0];
        return (new(userNum, scopeKey, row.Token, row.ExpiresAt, MysteryStoreSelectionJson.Decode(row.BoardJson),
            row.PurchasedMask), true);
    }

    private static async ValueTask WriteSelectionAsync(SqlSugarClient db, MysteryStoreSelection selection, bool exists,
        CancellationToken ct)
    {
        var row = new MysteryStoreSelectionRow
        {
            UserNum = selection.UserNum, BuildKey = selection.ScopeKey, Token = selection.Token,
            ExpiresAt = selection.ExpiresAtUnixMilliseconds, PurchasedMask = selection.PurchasedMask,
            BoardJson = MysteryStoreSelectionJson.Encode(selection.Board),
        };
        var written = exists
            ? await db.Updateable(row).ExecuteCommandAsync(ct).ConfigureAwait(false)
            : await db.Insertable(row).ExecuteCommandAsync(ct).ConfigureAwait(false);
        if (written != 1)
            throw new InvalidDataException($"Mysterystore selection of player {selection.UserNum} was not written exactly once.");
    }

    // Tokens only grow: the stored selection holds the newest one (the planner refuses a wrap).
    private static uint NextMysteryToken(MysteryStoreSelection? current) =>
        current is null ? 1u : current.Token == uint.MaxValue ? uint.MaxValue : current.Token + 1;
}
