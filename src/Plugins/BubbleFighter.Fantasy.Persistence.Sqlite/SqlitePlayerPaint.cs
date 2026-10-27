using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.Sqlite;

/// <summary>
/// The save's paint fragment: one spray's consumption, the equipped-bag mirror and the player's color
/// index commit as a single snapshot mutation, which is why <see cref="SupportsAtomicPaintApplication"/>
/// reports true here.
/// </summary>
internal sealed partial class SqliteRepositories
{
    /// <summary>Paint consumption and the color index commit in one operation.</summary>
    public bool SupportsAtomicPaintApplication => true;

    public ValueTask<bool> ApplyPaintWithItemAsync(long userNum, int itemId, ushort colorIndex, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userNum);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemId);
        ArgumentOutOfRangeException.ThrowIfZero(colorIndex);
        return new(database.Write((db, transaction) =>
        {
            var state = db.RequireState(userNum);
            if (state.ColorIndex == colorIndex) return false;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var index = state.Slots.FindLastIndex(row => row.Bag == InventoryBag.CharacterBag
                && row.ItemId == itemId && row.Quantity > 0
                && row.Lifetime.Kind != InventoryLifetimeKind.Life
                && (row.Lifetime.ExpiresAtUnixMilliseconds is not { } end || end > now));
            if (index < 0) return false;
            var remaining = state.Slots[index].Quantity - 1;
            DecreasePaintStack(state.Slots, index, remaining);
            var mirror = state.Slots.FindLastIndex(row => row.Bag == InventoryBag.EquippedBag && row.ItemId == itemId);
            if (mirror >= 0) DecreasePaintStack(state.Slots, mirror, remaining);
            state.ColorIndex = colorIndex;
            db.Save(userNum, state);
            return true;
        }, ct));
    }

    private static void DecreasePaintStack(List<InventorySlotRecord> rows, int index, int remaining)
    {
        if (remaining == 0) rows.RemoveAt(index);
        else rows[index] = rows[index] with { Quantity = remaining };
    }
}
