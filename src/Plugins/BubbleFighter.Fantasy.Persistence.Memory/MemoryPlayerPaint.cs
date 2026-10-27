using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.Memory;

public sealed partial class MemoryPlayerRepository
{
    /// <inheritdoc />
    public bool SupportsAtomicPaintApplication => true;

    /// <inheritdoc />
    /// <remarks>
    /// The whole application is atomic under the identity gate and then the entry gate. A spray is
    /// usable when its row is in the character bag with a positive, non-life quantity that has not
    /// expired; applying it decrements the stack (removing the row at zero), mirrors the decrement on
    /// the equipped-bag row when one exists, and then sets the color index. The call reports false —
    /// changing nothing — when the color is already applied or no usable spray is owned; a player
    /// without an identity entry throws.
    /// </remarks>
    public ValueTask<bool> ApplyPaintWithItemAsync(long userNum, int itemId, ushort colorIndex, CancellationToken ct)
    {
        RequireUserNum(userNum);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(itemId);
        ArgumentOutOfRangeException.ThrowIfZero(colorIndex);
        ct.ThrowIfCancellationRequested();
        lock (_identityGate)
        {
            var entry = table.Find(userNum) ?? throw new InvalidOperationException("Paint application requires an existing player.");
            lock (entry.Gate)
            {
                if (entry.ColorIndex == colorIndex) return ValueTask.FromResult(false);
                var bag = entry.Bags.GetValueOrDefault((int)InventoryBag.CharacterBag);
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var index = bag?.FindLastIndex(row => row.ItemId == itemId && row.Quantity > 0
                    && row.Lifetime.Kind != InventoryLifetimeKind.Life
                    && (row.Lifetime.ExpiresAtUnixMilliseconds is not { } end || end > now)) ?? -1;
                if (index < 0) return ValueTask.FromResult(false);
                var remaining = bag![index].Quantity - 1;
                DecreasePaintStack(bag, index, remaining);
                if (entry.Bags.TryGetValue((int)InventoryBag.EquippedBag, out var equipped))
                {
                    var mirror = equipped.FindLastIndex(row => row.ItemId == itemId);
                    if (mirror >= 0) DecreasePaintStack(equipped, mirror, remaining);
                }
                entry.ColorIndex = colorIndex;
                return ValueTask.FromResult(true);
            }
        }
    }

    private static void DecreasePaintStack(List<InventorySlotRecord> rows, int index, int remaining)
    {
        if (remaining == 0) rows.RemoveAt(index);
        else rows[index] = rows[index] with { Quantity = remaining };
    }
}
