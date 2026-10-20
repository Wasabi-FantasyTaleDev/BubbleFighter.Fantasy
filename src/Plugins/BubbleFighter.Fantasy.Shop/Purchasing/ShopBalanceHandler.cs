using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;

namespace BubbleFighter.Fantasy.Shop.Purchasing;

/// <summary>Answers a balance refresh without creating a wallet or republishing inventory.</summary>
internal sealed class ShopBalanceHandler(IPlayerWalletRepository? wallets, Func<long, PlayerWallet> fallback,
    IMessageSender<ShopBalanceUpdated> sender)
{
    internal async ValueTask HandleAsync(ISession session, ShopBalanceRequested request, CancellationToken ct)
    {
        var player = session.Feature<IPlayerSession>().Identity;
        var stored = wallets is null ? null : await wallets.FindByUserNumAsync(player.UserNum, ct).ConfigureAwait(false);
        var wallet = stored ?? fallback(player.UserNum);
        if (wallet.UserNum != player.UserNum)
            throw new InvalidOperationException("Balance refresh resolved another player's wallet.");
        await sender.SendAsync(session, new(player, wallet), ct).ConfigureAwait(false);
    }
}
