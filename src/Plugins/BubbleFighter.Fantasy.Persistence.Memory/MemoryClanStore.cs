using BubbleFighter.Fantasy.Contracts.Clans;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// Stores the latest clan open in the persistence row's bounded player table.
/// Membership stays empty until a supported clan mutation path is implemented.
/// </summary>
public sealed class MemoryClanStore : IClanStore
{
    private readonly MemoryPlayerTable _table;

    /// <summary>Uses the player table shared by the other repositories in this persistence row.</summary>
    /// <param name="table">The bounded table that owns player entries and their mutation locks.</param>
    public MemoryClanStore(MemoryPlayerTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
    }

    /// <inheritdoc />
    public ValueTask RememberOpenAsync(ClanOpenRecord record, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfNegative(record.UserNum);
        if (!Enum.IsDefined(record.Kind))
            throw new ArgumentOutOfRangeException(nameof(record), record.Kind, "Clan open kind is not defined.");
        if (record.UserNum == 0)
            return ValueTask.CompletedTask;
        var entry = _table.GetOrAdd(record.UserNum);
        lock (entry.Gate)
            entry.ClanOpen = record;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<ClanOpenRecord?> FindLastOpenAsync(long userNum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(userNum);
        var entry = _table.Find(userNum);
        if (entry is null)
            return ValueTask.FromResult<ClanOpenRecord?>(null);
        lock (entry.Gate)
            return ValueTask.FromResult(entry.ClanOpen);
    }

    /// <inheritdoc />
    public ValueTask<ClanMembership> GetMembershipAsync(long userNum, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(userNum);
        return ValueTask.FromResult(new ClanMembership(userNum, 0));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<long>> ListClanIdsAsync(int maxCount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateMaxCount(maxCount);
        return ValueTask.FromResult<IReadOnlyList<long>>(Array.Empty<long>());
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<long>> ListMemberUserNumsAsync(long clanId, int maxCount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateMaxCount(maxCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clanId);
        return ValueTask.FromResult<IReadOnlyList<long>>(Array.Empty<long>());
    }

    private static void ValidateMaxCount(int maxCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        ArgumentOutOfRangeException.ThrowIfEqual(maxCount, int.MaxValue);
    }
}
