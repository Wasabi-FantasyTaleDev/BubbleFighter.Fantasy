namespace BubbleFighter.Fantasy.Contracts.Wallet;

/// <summary>Loads and replaces a player's wallet.</summary>
public interface IPlayerWalletRepository
{
    /// <summary>Returns the stored wallet, or <see langword="null"/> when the player has none yet.</summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<PlayerWallet?> FindByUserNumAsync(long userNum, CancellationToken ct);

    /// <summary>Inserts or replaces the wallet.</summary>
    /// <param name="wallet">The balances to store.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask UpsertAsync(PlayerWallet wallet, CancellationToken ct);
}
