using BubbleFighter.Fantasy.Contracts.Shop;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>Stores one latest shop open per player in the persistence row's shared bounded table.</summary>
public sealed class MemoryShopSessionStore : IShopSessionStore
{
    private readonly MemoryPlayerTable _table;

    /// <summary>Uses the same capacity and per-player mutation locks as the row's other repositories.</summary>
    /// <param name="table">The bounded player table owned by this persistence row.</param>
    public MemoryShopSessionStore(MemoryPlayerTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
    }

    /// <inheritdoc />
    public ValueTask RememberOpenAsync(ShopOpenRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfNegative(record.UserNum);
        if (!Enum.IsDefined(record.Kind))
            throw new ArgumentOutOfRangeException(nameof(record), record.Kind, "Shop kind is not defined.");
        if (record.UserNum == 0)
            return ValueTask.CompletedTask;
        var entry = _table.GetOrAdd(record.UserNum);
        lock (entry.Gate)
            entry.ShopOpen = record;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<ShopOpenRecord?> FindLastOpenAsync(long userNum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(userNum);
        var entry = _table.Find(userNum);
        if (entry is null)
            return ValueTask.FromResult<ShopOpenRecord?>(null);
        lock (entry.Gate)
            return ValueTask.FromResult(entry.ShopOpen);
    }
}
