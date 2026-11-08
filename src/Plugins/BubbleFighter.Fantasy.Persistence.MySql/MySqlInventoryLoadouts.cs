using System.Text.Json;
using System.Text.Json.Serialization;
using BubbleFighter.Fantasy.Contracts.Inventory;
using SqlSugar;

namespace BubbleFighter.Fantasy.Persistence.MySql;

public sealed partial class MySqlInventoryRepository
{
    private static readonly JsonSerializerOptions LoadoutJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<InventorySlotRecord>> ListLoadoutSlotsAsync(long userNum, InventoryLoadoutScope scope,
        InventoryBag bag, int maxCount, CancellationToken ct)
    {
        RequireUserNum(userNum);
        RequireBag(bag);
        RequireBound(maxCount, InventoryLimits.MaxInventorySlotsPerBag, nameof(maxCount));
        ArgumentNullException.ThrowIfNull(scope);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        // Inspection reads another player's appearance without acquiring write authority or materializing state.
        await using var transaction = await session.BeginAsync().ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(session.Db, userNum, ct).ConfigureAwait(false);
        var (state, _) = await ReadLoadoutAsync(session.Db, userNum, scope, ct).ConfigureAwait(false);
        var bagId = (int)bag;
        var rows = await session.Db.Queryable<InventorySlotRow>().Where(row => row.UserNum == userNum && row.Bag == bagId)
            .OrderBy(row => row.Ordinal).Take(InventoryLimits.MaxInventorySlotsPerBag + 1).ToListAsync(ct).ConfigureAwait(false);
        var result = InventoryLoadouts.Project(rows.Select(ToRecord).ToArray(), state, scope).Take(maxCount).ToArray();
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<InventoryLoadoutState> GetLoadoutAsync(long userNum, InventoryLoadoutScope scope, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ArgumentNullException.ThrowIfNull(scope);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginWriteAsync(userNum, authority, ct).ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(session.Db, userNum, ct).ConfigureAwait(false);
        var (state, exists) = await ReadLoadoutAsync(session.Db, userNum, scope, ct).ConfigureAwait(false);
        await SaveLoadoutAsync(session.Db, userNum, scope.Key, state, exists, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return state.Copy();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<InventorySlotRecord>?> UpdateLoadoutSelectionAsync(long userNum,
        InventoryLoadoutScope scope, IReadOnlyList<InventoryWearGroup> groups, int? itemId, bool worn, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ArgumentNullException.ThrowIfNull(scope);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginWriteAsync(userNum, authority, ct).ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(session.Db, userNum, ct).ConfigureAwait(false);
        var (state, exists) = await ReadLoadoutAsync(session.Db, userNum, scope, ct).ConfigureAwait(false);
        var stored = await session.Db.Queryable<InventorySlotRow>()
            .Where(row => row.UserNum == userNum && (row.Bag == 0 || row.Bag == 1))
            .OrderBy(row => row.Bag).OrderBy(row => row.UpdatedAt).OrderBy(row => row.Ordinal)
            .Take(2 * InventoryLimits.MaxInventorySlotsPerBag + 1).TranLock(DbLockType.Wait).ToListAsync(ct).ConfigureAwait(false);
        var changed = InventoryWearSelection.Apply(
            InventoryLoadouts.Project(stored.Select(ToRecord).ToArray(), state, scope), groups, itemId, worn);
        if (changed is null) return null;
        state = state with
        {
            WornItemIds = changed.Where(row => row.Bag == InventoryBag.CharacterBag
                && (row.Flags & InventorySlotRecord.WornFlagBit) != 0).Select(row => row.ItemId).Distinct().ToArray(),
        };
        await SaveLoadoutAsync(session.Db, userNum, scope.Key, state, exists, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return changed.Where(row => row.Bag == InventoryBag.CharacterBag).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<InventoryLoadoutState> UpdateLoadoutPresetsAsync(long userNum, InventoryLoadoutScope scope,
        byte? activeSlot, WeaponPresetPage[]? pages, bool initializePages, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ArgumentNullException.ThrowIfNull(scope);
        var copy = pages is null ? null : WeaponPresetPages.CopyTable(pages);
        if (activeSlot > InventoryLimits.MaxWeaponPresetSlot) throw new ArgumentOutOfRangeException(nameof(activeSlot));
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginWriteAsync(userNum, authority, ct).ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(session.Db, userNum, ct).ConfigureAwait(false);
        var (state, exists) = await ReadLoadoutAsync(session.Db, userNum, scope, ct).ConfigureAwait(false);
        state = InventoryLoadouts.Presets(state, scope, activeSlot, copy, initializePages);
        await SaveLoadoutAsync(session.Db, userNum, scope.Key, state, exists, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return state.Copy();
    }

    private async ValueTask<(InventoryLoadoutState State, bool Exists)> ReadLoadoutAsync(SqlSugarClient db,
        long userNum, InventoryLoadoutScope scope, CancellationToken ct)
    {
        if (maxLoadoutsPerPlayer is < 1 or > 256) throw new InvalidOperationException("Invalid build loadout capacity.");
        var keys = await db.Queryable<PlayerLoadoutRow>().Where(row => row.UserNum == userNum)
            .Select(row => row.BuildKey).Take(maxLoadoutsPerPlayer + 1).ToListAsync(ct).ConfigureAwait(false);
        if (keys.Count > maxLoadoutsPerPlayer) throw new InvalidDataException("Stored build loadouts exceed the configured capacity.");
        var exists = keys.Contains(scope.Key, StringComparer.Ordinal);
        InventoryLoadoutState state;
        if (exists)
        {
            var rows = await db.Queryable<PlayerLoadoutRow>()
                .Where(row => row.UserNum == userNum && row.BuildKey == scope.Key).Take(1).ToListAsync(ct).ConfigureAwait(false);
            var json = rows.Single().StateJson;
            if (json.Length > InventoryLimits.MaxInventorySlotsPerBag * 12 + 2048)
                throw new InvalidDataException("Stored build loadout exceeds its serialized bound.");
            state = JsonSerializer.Deserialize<InventoryLoadoutState>(json, LoadoutJson)
                ?? throw new InvalidDataException("Stored build loadout is null.");
        }
        else
        {
            if (keys.Count >= maxLoadoutsPerPlayer) throw new InvalidOperationException("The player's build loadout capacity is exhausted.");
            var slot = await db.Queryable<PlayerRow>().Where(row => row.UserNum == userNum)
                .Select(row => row.ActiveWeaponPresetSlot).Take(1).ToListAsync(ct).ConfigureAwait(false);
            var pages = await db.Queryable<WeaponPresetPageRow>().Where(row => row.UserNum == userNum)
                .OrderBy(row => row.Page).Take(WeaponPresetSlots.Count + 1).ToListAsync(ct).ConfigureAwait(false);
            if (pages.Count != 0 && (pages.Count != WeaponPresetSlots.Count
                || !pages.Select(row => (int)row.Page).SequenceEqual(Enumerable.Range(0, WeaponPresetSlots.Count))))
                throw new InvalidDataException("Legacy weapon presets do not form a complete positional table.");
            state = new(null, slot.Single(), pages.Count == 0 ? null
                : pages.Select(row => new WeaponPresetPage(row.Word0, row.Word1, row.Word2)).ToArray());
        }
        return (InventoryLoadouts.Compatible(state, scope), exists);
    }

    private static async ValueTask SaveLoadoutAsync(SqlSugarClient db, long userNum, string buildKey,
        InventoryLoadoutState state, bool exists, CancellationToken ct)
    {
        var row = new PlayerLoadoutRow { UserNum = userNum, BuildKey = buildKey, StateJson = JsonSerializer.Serialize(state.Copy(), LoadoutJson) };
        var written = exists
            ? await db.Updateable(row).ExecuteCommandAsync(ct).ConfigureAwait(false)
            : await db.Insertable(row).ExecuteCommandAsync(ct).ConfigureAwait(false);
        // The player lock keeps the existing row stable; UseAffectedRows reports zero for an identical update.
        if (written != 1 && !(exists && written == 0))
            throw new InvalidOperationException($"Expected one loadout write; stored {written} rows.");
    }
}

[SugarTable("player_loadouts")]
internal sealed class PlayerLoadoutRow
{
    [SugarColumn(ColumnName = "user_num", IsPrimaryKey = true)]
    public long UserNum { get; set; }

    [SugarColumn(ColumnName = "build_key", IsPrimaryKey = true)]
    public string BuildKey { get; set; } = "";

    [SugarColumn(ColumnName = "state_json")]
    public string StateJson { get; set; } = "";
}
