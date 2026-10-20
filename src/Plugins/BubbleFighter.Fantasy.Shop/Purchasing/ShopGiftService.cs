using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Shop.Data;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Shop.Purchasing;

/// <summary>
/// Gives a Stock or StockSet of the build's workbook at no charge: its items under the same terms a purchase plans
/// (count or days added to an owned item, the item's limits enforced), its Lucci credited in the same transaction,
/// then the committed rows and wallet published through the shop's own state sender, as after a purchase.
/// </summary>
internal sealed class ShopGiftService(ShopOptions options, ShopCatalog catalog, ConditionalShopPurchaser purchaser,
    IShopPurchaseStore store, Func<ShopStockRow, PurchaseGrantTerm> resolve, IMessageSender<ShopStateUpdated> state,
    ILogger logger) : IShopGiftService
{
    /// <inheritdoc />
    public bool CanGrant(int key)
    {
        if (!catalog.Gifts.TryGetValue(key, out var gift))
            return false;
        try
        {
            foreach (var stock in gift.Stocks)
                resolve(stock);
            return true;
        }
        catch (PurchaseRejectedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> GrantAsync(ISession session, long userNum, int key, CancellationToken ct)
    {
        if (!catalog.Gifts.TryGetValue(key, out var gift))
            throw new InvalidOperationException($"Shop gift {key} is not a Stock or StockSet of this build's workbook.");
        var player = session.Feature<IPlayerSession>().Identity;
        if (player.UserNum != userNum)
            throw new InvalidOperationException($"Shop gift {key} names player {userNum} on the session of player {player.UserNum}.");
        var command = new ShopPurchaseCommand(ShopPurchaser.SeedWallet(options, userNum), key, gift.StockSetId,
            "lucci", 0, ShopPurchaser.PurchaseSlotKey(userNum, 0), []) { LucciReward = gift.Lucci };
        ShopPurchaseResult result;
        try
        {
            result = (gift.Stocks.Count == 0
                ? await store.PurchaseAsync(command, ct).ConfigureAwait(false)
                : await purchaser.CommitAsync(command, gift.Stocks.Select(resolve).ToArray(), ct).ConfigureAwait(false))
                ?? throw new InvalidOperationException($"Shop gift {key} at no charge was refused by the purchase store.");
        }
        catch (PurchaseRejectedException ex)
        {
            logger.LogWarning("Shop gift {Key} not given to player {Player}: {Reason}", key, userNum, ex.Message);
            return false;
        }
        await state.SendAsync(session, new(player, result.Wallet, result.Grants, false), ct).ConfigureAwait(false);
        logger.LogInformation("Shop gift {Key} given to player {Player}: {Items} item(s), {Lucci} Lucci.",
            key, userNum, result.Grants.Count, gift.Lucci);
        return true;
    }
}
