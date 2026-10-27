using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Abstractions.Persistence;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>
/// MySQL store for the per-player weapon-preset state: the active slot in the
/// <c>players.active_weapon_preset_slot</c> column (migration 0018) and the materialized preset pages in
/// <c>weapon_preset_pages</c> (migration 0033), one row per page keyed by list position. A player with
/// nothing granted reads as the default slot 0 and no pages through the column defaults; writes against an
/// unknown <c>user_num</c> fail loud instead of dropping the granted state.
/// </summary>
public sealed class MySqlPlayerInventoryState(IDbConnectionFactory connections) : IPlayerInventoryState
{
    /// <inheritdoc />
    public async ValueTask<byte> GetActiveWeaponPresetSlotAsync(long userNum, CancellationToken ct)
    {
        RequireUserNum(userNum);

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var slots = await session.Db.Queryable<PlayerRow>()
            .Where(p => p.UserNum == userNum)
            .Select(p => p.ActiveWeaponPresetSlot)
            .Take(1)
            .ToListAsync(ct).ConfigureAwait(false);
        return slots.Count == 0 ? (byte)0 : slots[0];
    }

    /// <inheritdoc />
    public async ValueTask SetActiveWeaponPresetSlotAsync(long userNum, byte slot, CancellationToken ct)
    {
        RequireUserNum(userNum);
        if (slot > InventoryLimits.MaxWeaponPresetSlot)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slot), slot, $"slot must be 0..{InventoryLimits.MaxWeaponPresetSlot}.");
        }

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var rows = await session.Db.Updateable<PlayerRow>()
            .SetColumns(p => p.ActiveWeaponPresetSlot == slot)
            .Where(p => p.UserNum == userNum)
            .ExecuteCommandAsync(ct).ConfigureAwait(false);
        if (rows != 1)
        {
            throw new InvalidOperationException(
                $"players has no row for user_num {userNum}; the active weapon-preset slot {slot} cannot be stored.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<WeaponPresetPage[]?> GetWeaponPresetPagesAsync(long userNum, CancellationToken ct)
    {
        RequireUserNum(userNum);

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        var rows = await session.Db.Queryable<WeaponPresetPageRow>()
            .Where(row => row.UserNum == userNum)
            .OrderBy(row => row.Page)
            .ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count == 0)
            return null;
        return ReadTable(rows, userNum);
    }

    private static WeaponPresetPage[] ReadTable(List<WeaponPresetPageRow> rows, long userNum)
    {
        // Partial page sets cannot be published: the client keys pages by list position, so a hole would
        // shift every later page onto the wrong hotkey.
        if (rows.Count != WeaponPresetSlots.Count || rows.Any(row => row.Page >= WeaponPresetSlots.Count))
        {
            throw new InvalidOperationException(
                $"weapon_preset_pages holds {rows.Count} rows for user_num {userNum}; exactly {WeaponPresetSlots.Count} are required.");
        }
        return rows.Select(row => new WeaponPresetPage(row.Word0, row.Word1, row.Word2)).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask SetWeaponPresetPagesAsync(long userNum, WeaponPresetPage[] pages, CancellationToken ct)
    {
        RequireUserNum(userNum);
        var copy = WeaponPresetPages.CopyTable(pages);

        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginAsync().ConfigureAwait(false);
        var db = session.Db;
        // The lock doubles as the existence check: pages attach to a known player row or nothing is written.
        await MySqlPlayerLock.AcquireAsync(db, userNum, ct).ConfigureAwait(false);
        await db.Deleteable<WeaponPresetPageRow>().Where(row => row.UserNum == userNum)
            .ExecuteCommandAsync(ct).ConfigureAwait(false);
        await db.Insertable(Enumerable.Range(0, copy.Length)
                .Select(index => new WeaponPresetPageRow
                {
                    UserNum = userNum, Page = (byte)index,
                    Word0 = copy[index].Unknown0, Word1 = copy[index].Unknown4, Word2 = copy[index].Unknown8,
                })
                .ToArray())
            .ExecuteCommandAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<WeaponPresetPage[]> GetOrCreateWeaponPresetPagesAsync(long userNum, WeaponPresetPage[] initialPages, CancellationToken ct)
    {
        RequireUserNum(userNum);
        var copy = WeaponPresetPages.CopyTable(initialPages);
        await using var session = await SugarSession.OpenAsync(connections, ct).ConfigureAwait(false);
        await using var transaction = await session.BeginAsync().ConfigureAwait(false);
        await MySqlPlayerLock.AcquireAsync(session.Db, userNum, ct).ConfigureAwait(false);
        var rows = await session.Db.Queryable<WeaponPresetPageRow>()
            .Where(row => row.UserNum == userNum).OrderBy(row => row.Page).ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count != 0)
            copy = ReadTable(rows, userNum);
        else
            await session.Db.Insertable(Enumerable.Range(0, copy.Length).Select(index => new WeaponPresetPageRow
            {
                UserNum = userNum, Page = (byte)index,
                Word0 = copy[index].Unknown0, Word1 = copy[index].Unknown4, Word2 = copy[index].Unknown8,
            }).ToArray()).ExecuteCommandAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await transaction.CommitAsync().ConfigureAwait(false);
        return copy;
    }

    private static void RequireUserNum(long userNum)
    {
        if (userNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(userNum), userNum, "user_num must be a positive login id.");
    }
}
