using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Abstractions.Persistence;

namespace BubbleFighter.Fantasy.Persistence.MySql;

/// <summary>
/// MySQL store for the per-player active weapon-preset slot, kept in the
/// <c>players.active_weapon_preset_slot</c> column (migration 0018). A player
/// row with nothing granted yet reads as the default slot 0 through the column
/// default; a set against an unknown <c>user_num</c> writes zero rows and fails
/// loud instead of dropping the granted slot.
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

    private static void RequireUserNum(long userNum)
    {
        if (userNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(userNum), userNum, "user_num must be a positive login id.");
    }
}
