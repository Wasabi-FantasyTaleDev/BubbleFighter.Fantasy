using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Contracts.Fighters;

namespace BubbleFighter.Fantasy.Fighters.Data;

/// <summary>In-process unlocked-item map keyed by <c>user_num</c>.</summary>
public sealed class MemoryFighterRoster : IFighterRoster
{
    private readonly ConcurrentDictionary<long, AccountItems> _byUser = new();

    /// <inheritdoc />
    public ValueTask UnlockAsync(long userNum, int itemId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (itemId == 0)
            throw new InvalidOperationException("Cannot unlock character-item id 0.");
        var account = _byUser.GetOrAdd(userNum, static _ => new AccountItems());
        lock (account.Gate)
        {
            if (account.Unlocked.Count >= FighterCatalog.MaxPlayableFieldId && !account.Unlocked.Contains(itemId))
            {
                throw new InvalidOperationException(
                    $"Fighter roster for user {userNum} already holds {account.Unlocked.Count} items.");
            }

            if (account.Unlocked.Add(itemId))
                account.Order.Add(itemId);

            if (account.Equipped == 0)
                account.Equipped = itemId;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask EquipAsync(long userNum, int itemId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_byUser.TryGetValue(userNum, out var account))
            throw new InvalidOperationException($"Fighter roster for user {userNum} has no unlocked items.");
        lock (account.Gate)
        {
            if (!account.Unlocked.Contains(itemId))
                throw new InvalidOperationException($"User {userNum} has not unlocked item {itemId}.");
            account.Equipped = itemId;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<int>> ListUnlockedAsync(long userNum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_byUser.TryGetValue(userNum, out var account))
            return ValueTask.FromResult<IReadOnlyList<int>>([]);
        lock (account.Gate)
            return ValueTask.FromResult<IReadOnlyList<int>>(account.Order.ToArray());
    }

    /// <inheritdoc />
    public ValueTask<int> GetEquippedAsync(long userNum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_byUser.TryGetValue(userNum, out var account))
            return ValueTask.FromResult(0);
        lock (account.Gate)
            return ValueTask.FromResult(account.Equipped);
    }

    private sealed class AccountItems
    {
        public object Gate { get; } = new();

        public HashSet<int> Unlocked { get; } = [];

        public List<int> Order { get; } = [];

        public int Equipped { get; set; }
    }
}
