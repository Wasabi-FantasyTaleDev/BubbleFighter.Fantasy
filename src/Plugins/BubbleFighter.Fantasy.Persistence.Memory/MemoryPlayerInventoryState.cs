using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// In-process store for the per-player active weapon-preset slot, mirroring the
/// guard behaviour of the sibling repositories: argument checks run before any
/// state is touched, a first-seen player reads as the default slot 0, and a set
/// tracks the player through the shared bounded table, so a full table fails
/// the write instead of dropping the granted slot. State lives only for the
/// life of the process, like every other store of this row.
/// </summary>
public sealed class MemoryPlayerInventoryState(MemoryPlayerTable table) : IPlayerInventoryState
{
    /// <inheritdoc />
    public ValueTask<byte> GetActiveWeaponPresetSlotAsync(long userNum, CancellationToken ct)
    {
        RequireUserNum(userNum);

        var entry = table.Find(userNum);
        if (entry is null)
            return ValueTask.FromResult((byte)0);
        lock (entry.Gate)
            return ValueTask.FromResult(entry.ActiveWeaponPresetSlot);
    }

    /// <inheritdoc />
    public ValueTask SetActiveWeaponPresetSlotAsync(long userNum, byte slot, CancellationToken ct)
    {
        RequireUserNum(userNum);
        if (slot > InventoryLimits.MaxWeaponPresetSlot)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slot), slot, $"slot must be 0..{InventoryLimits.MaxWeaponPresetSlot}.");
        }

        var entry = table.GetOrAdd(userNum);
        lock (entry.Gate)
            entry.ActiveWeaponPresetSlot = slot;
        return ValueTask.CompletedTask;
    }

    private static void RequireUserNum(long userNum)
    {
        if (userNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(userNum), userNum, "user_num must be a positive login id.");
    }
}
