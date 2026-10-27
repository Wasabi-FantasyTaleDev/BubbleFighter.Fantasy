using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Shop.Data;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Shop.Purchasing;

/// <summary>
/// Commits catalog purchases and publishes their state through the selected message dialect. The
/// purchase gate is the per-connection open record: a buy is honored only for the player whose shop
/// open (or inventory-dialog entry) registered that connection, and only for ordinary purchases whose
/// key the catalog knows; everything else answers with the plain rejected reply. Publishing follows a
/// fixed order — state, then the reply — and a send failure after a committed purchase propagates and
/// closes the connection rather than retrying the charge. Opening a shop never creates a wallet row:
/// an absent wallet is shown from the seeded default, and its first write belongs to the atomic
/// purchase.
/// </summary>
internal sealed class CatalogPurchaseHandler(ShopOptions options, ShopCatalog catalog, ShopPurchaser purchaser,
    IInventoryRepository inventory, IPlayerWalletRepository wallets,
    IMessageSender<ShopStateUpdated> state, IMessageSender<ShopBalanceUpdated> balance,
    IMessageSender<ShopPurchaseReplied> reply, ShopOpenScope ready,
    Handlers.InventoryEntryHandler? entry, ILogger logger)
{
    private readonly ShopBalanceHandler _balances = new(wallets, user => ShopPurchaser.SeedWallet(options, user), balance);

    /// <summary>Answers a balance request through the shared balance handler (seeded default when no wallet row exists).</summary>
    internal ValueTask BalanceAsync(ISession session, ShopBalanceRequested request, CancellationToken ct) =>
        _balances.HandleAsync(session, request, ct);

    /// <summary>Opens the shop: sends the current wallet and bag as shop state and arms the connection's purchase gate.</summary>
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
    /// <summary>Arms the purchase gate exactly as a shop entry does.</summary>
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

    /// <summary>
    /// Commits one catalog purchase for the gate's player and answers the reply. A store rejection is
    /// logged (its reason text is a user-facing message, never an exception surface) and answered as
    /// the rejected reply; a successful commit publishes the new wallet and grants as state first.
    /// </summary>
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
