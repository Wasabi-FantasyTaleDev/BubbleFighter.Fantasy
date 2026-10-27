using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// In-process store for the per-player weapon-preset state - the active slot and, once materialized, the
/// server-owned preset pages - mirroring the guard behaviour of the sibling repositories: argument checks run
/// before any state is touched, a first-seen player reads as the default slot 0 and no pages, and a set
/// tracks the player through the shared bounded table, so a full table fails the write instead of dropping
/// the granted state. State lives only for the life of the process, like every other store of this row.
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

    /// <inheritdoc />
    public ValueTask<WeaponPresetPage[]?> GetWeaponPresetPagesAsync(long userNum, CancellationToken ct)
    {
        RequireUserNum(userNum);

        var entry = table.Find(userNum);
        if (entry is null)
            return ValueTask.FromResult<WeaponPresetPage[]?>(null);
        lock (entry.Gate)
            return ValueTask.FromResult(entry.WeaponPresetPages?.ToArray());
    }

    /// <inheritdoc />
    public ValueTask SetWeaponPresetPagesAsync(long userNum, WeaponPresetPage[] pages, CancellationToken ct)
    {
        RequireUserNum(userNum);
        var copy = WeaponPresetPages.CopyTable(pages);

        var entry = table.GetOrAdd(userNum);
        lock (entry.Gate)
            entry.WeaponPresetPages = copy;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<WeaponPresetPage[]> GetOrCreateWeaponPresetPagesAsync(long userNum, WeaponPresetPage[] initialPages, CancellationToken ct)
    {
        RequireUserNum(userNum);
        var copy = WeaponPresetPages.CopyTable(initialPages);
        ct.ThrowIfCancellationRequested();
        var entry = table.GetOrAdd(userNum);
        lock (entry.Gate)
        {
            entry.WeaponPresetPages ??= copy;
            return ValueTask.FromResult(entry.WeaponPresetPages.ToArray());
        }
    }

    private static void RequireUserNum(long userNum)
    {
        if (userNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(userNum), userNum, "user_num must be a positive login id.");
    }
}
