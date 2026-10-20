using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Persistence.Memory;

public sealed partial class MemoryPlayerRepository
{
    /// <inheritdoc />
    public bool SupportsAtomicPaintApplication => true;

    /// <inheritdoc />
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
