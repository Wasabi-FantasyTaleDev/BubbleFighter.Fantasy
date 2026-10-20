using BubbleFighter.Fantasy.Contracts.Wallet;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// In-process store for the six S2C 106 money integers, mirroring the guard
/// behaviour of the MySQL store: the same argument checks run before any state
/// is touched, a first-seen player reads as <see langword="null"/>, and an
/// upsert inserts or replaces the whole snapshot. Callers seed and spend by
/// reading a snapshot, producing the adjusted record and upserting it.
/// </summary>
public sealed class MemoryPlayerWalletRepository(MemoryPlayerTable table) : IPlayerWalletRepository
{
    /// <inheritdoc />
    public ValueTask<PlayerWallet?> FindByUserNumAsync(long userNum, CancellationToken ct)
    {
        if (userNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(userNum), userNum, "user_num must be a positive login id.");

        var entry = table.Find(userNum);
        if (entry is null)
            return ValueTask.FromResult<PlayerWallet?>(null);
        lock (entry.Gate)
            return ValueTask.FromResult(entry.Money);
    }

    /// <inheritdoc />
    public ValueTask UpsertAsync(PlayerWallet record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.UserNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(record), record.UserNum, "user_num must be a positive login id.");

        var entry = table.GetOrAdd(record.UserNum);
        lock (entry.Gate)
        {
            entry.Money = record;
            // A new memory player has no database row to initialize its first round's progress.
            entry.Progress ??= new BubbleFighter.Fantasy.Contracts.Progress.PlayerProgress(record.UserNum, 1, 0);
        }
        return ValueTask.CompletedTask;
    }
}
