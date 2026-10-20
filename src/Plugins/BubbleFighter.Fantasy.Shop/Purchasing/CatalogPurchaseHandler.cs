using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Shop.Data;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Shop.Purchasing;

/// <summary>Commits catalog purchases and publishes their state through the selected message dialect.</summary>
internal sealed class CatalogPurchaseHandler(ShopOptions options, ShopCatalog catalog, ShopPurchaser purchaser,
    IInventoryRepository inventory, IPlayerWalletRepository wallets,
    IMessageSender<ShopStateUpdated> state, IMessageSender<ShopBalanceUpdated> balance,
    IMessageSender<ShopPurchaseReplied> reply, ShopOpenScope ready,
    Handlers.InventoryEntryHandler? entry, ILogger logger)
{
    private readonly ShopBalanceHandler _balances = new(wallets, user => ShopPurchaser.SeedWallet(options, user), balance);

    internal ValueTask BalanceAsync(ISession session, ShopBalanceRequested request, CancellationToken ct) =>
        _balances.HandleAsync(session, request, ct);

    internal async ValueTask OpenAsync(ISession session, CancellationToken ct)
    {
        var player = session.Feature<IPlayerSession>().Identity;
        var bag = await inventory.ListSlotsAsync(player.UserNum, InventoryBag.CharacterBag,
            InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
        // Wallet creation belongs to the atomic purchase, avoiding a racing first-open upsert.
        var wallet = await wallets.FindByUserNumAsync(player.UserNum, ct).ConfigureAwait(false)
            ?? ShopPurchaser.SeedWallet(options, player.UserNum);
        await state.SendAsync(session, new(player, wallet, bag, true), ct).ConfigureAwait(false);
        ready.Remember(session.Connection.Id, new(player.UserNum, 0, 0, ShopKind.ItemShop));
    }

    // Inventory-dialog entry answers with the bag restatement and the equipped view, not
    // the shop state; the open record still arms the
    // purchase gate exactly as a shop entry does.
    internal async ValueTask InventoryAsync(ISession session, ShopInventoryRequested request, CancellationToken ct)
    {
        if (entry is not null)
        {
            await entry.HandleAsync(session, request, ct).ConfigureAwait(false);
            var player = session.Feature<IPlayerSession>().Identity;
            ready.Remember(session.Connection.Id, new(player.UserNum, 0, 0, ShopKind.ItemShop));
            return;
        }
        await OpenAsync(session, ct).ConfigureAwait(false);
    }

    internal async ValueTask BuyAsync(ISession session, ShopPurchaseRequested request, CancellationToken ct)
    {
        if (!request.IsOrdinaryPurchase || !session.TryFeature<IPlayerSession>(out var player)
            || !ready.TryGet(session.Connection.Id, out var open) || open.UserNum != player.Identity.UserNum
            || catalog.FindPurchase(request.Key) is not { } offer)
        {
            await reply.SendAsync(session, new(request.Key, false), ct).ConfigureAwait(false);
            return;
        }

        ShopPurchaseResult? result;
        try
        {
            result = await purchaser.CommitOfferAsync(session, player.Identity.UserNum, offer, ct).ConfigureAwait(false);
        }
        catch (PurchaseRejectedException ex)
        {
            logger.LogWarning("Purchase {Key} refused for player {Player}: {Reason}",
                request.Key, player.Identity.UserNum, ex.Message);
            result = null;
        }

        if (result is not null)
            await state.SendAsync(session, new(player.Identity, result.Wallet, result.Grants, false), ct)
                .ConfigureAwait(false);
        // A send failure after commit propagates and closes the connection; it never retries the charge.
        await reply.SendAsync(session, new(request.Key, result is not null), ct).ConfigureAwait(false);
    }
}
