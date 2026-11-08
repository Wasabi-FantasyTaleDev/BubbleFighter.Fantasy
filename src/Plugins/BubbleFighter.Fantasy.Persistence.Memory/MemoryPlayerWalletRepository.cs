using BubbleFighter.Fantasy.Contracts.Wallet;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// Stores wallet snapshots under each player's lock. Initialization preserves an existing wallet;
/// explicit upserts replace it. Purchases use their separate transactional store.
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
    public ValueTask<PlayerWallet> GetOrCreateAsync(PlayerWallet initialWallet, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(initialWallet);
        if (initialWallet.UserNum <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialWallet), initialWallet.UserNum, "user_num must be a positive login id.");
        if (initialWallet.Lucci < 0 || initialWallet.Cash < 0)
            throw new ArgumentOutOfRangeException(nameof(initialWallet), "A wallet balance cannot be negative.");

        var entry = table.GetOrAdd(initialWallet.UserNum);
        lock (entry.Gate)
        {
            entry.Money ??= initialWallet;
            entry.Progress ??= new BubbleFighter.Fantasy.Contracts.Progress.PlayerProgress(initialWallet.UserNum, 1, 0);
            return ValueTask.FromResult(entry.Money);
        }
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
