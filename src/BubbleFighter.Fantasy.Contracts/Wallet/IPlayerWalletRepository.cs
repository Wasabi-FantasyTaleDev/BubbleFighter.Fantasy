namespace BubbleFighter.Fantasy.Contracts.Wallet;

/// <summary>Loads and replaces a player's wallet.</summary>
public interface IPlayerWalletRepository
{
    /// <summary>Returns the stored wallet, or <see langword="null"/> when the player has none yet.</summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<PlayerWallet?> FindByUserNumAsync(long userNum, CancellationToken ct);

    /// <summary>
    /// Stores the given absolute balances, creating the wallet when absent. The store does not merge:
    /// this write wins over any balance read earlier. Changes that must survive a concurrent purchase
    /// go through the purchase store's serialized operation, which reads the wallet under the wallet
    /// row lock inside its own transaction.
    /// </summary>
    /// <param name="wallet">The balances to store; both balances must be non-negative.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for a non-positive user number or a negative balance.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the store writes no row.</exception>
    ValueTask UpsertAsync(PlayerWallet wallet, CancellationToken ct);
}
