using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Purchasing;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Shop.Presents;

/// <summary>
/// The gift box: buys a shop item for another player, lists the received and sent gifts, lets the receiver take, throw
/// away or remove a gift, and keeps the client's new-gift icon current. A gift is bought at the shop's own price with
/// the shop's own catalog, charged to the sender when it is stored, and delivered through the same purchase planner and
/// store transaction as a bought item, so lifetime and stacking match a purchase.
/// </summary>
/// <remarks>
/// Gifts belong to the build whose workbook their key comes from (<see cref="ShopOptions.ClientProfile"/>), so a
/// receiver is notified only while logged in to the same build. Another build's player sees the gift, and the icon, at
/// the next login to the build it was sent from.
/// </remarks>
internal sealed class PresentService(
    ShopOptions options, ShopCatalog catalog, ConditionalShopPurchaser purchaser,
    Func<ShopStockRow, PurchaseGrantTerm> resolve, IInventoryRepository inventory, IPresentStore store,
    ShopOpenScope open, IMessageSender<ShopStateUpdated> state, IMessageSender<ShopBalanceUpdated> balance,
    IMessageSender<PresentSendReplied> sendReplies, IMessageSender<PresentListReplied> listReplies,
    IMessageSender<PresentActionReplied> actionReplies, IMessageSender<PresentNoticeSent> notices,
    ISessionRegistry? sessions, TimeProvider clock, ILogger logger)
{
    private string Profile => options.ClientProfile;

    /// <summary>Buys a shop item as a gift (kr-812 sub_96F350 sends the request from the shop's gift dialog).</summary>
    internal async ValueTask SendAsync(ISession session, PresentSendRequested request, CancellationToken ct)
    {
        var status = await TrySendAsync(session, request, ct).ConfigureAwait(false);
        await sendReplies.SendAsync(session, new(status), ct).ConfigureAwait(false);
    }

    private async ValueTask<PresentSendStatus> TrySendAsync(ISession session, PresentSendRequested request, CancellationToken ct)
    {
        // A purchase is only valid from an opened shop, as an ordinary one is (CatalogPurchaseHandler).
        if (!session.TryFeature<IPlayerSession>(out var player) || !open.TryGet(session.Connection.Id, out var scope)
            || scope.UserNum != player.Identity.UserNum)
            return PresentSendStatus.SenderInvalid;
        var me = player.Identity;
        if (request.Message.Length > options.MaxPresentMessageChars)
            return PresentSendStatus.MessageRejected;
        if (catalog.FindPurchase(request.StockKey) is not { } offer)
            return PresentSendStatus.NotGiftable;
        string currency;
        PurchaseGrantTerm[] terms;
        try
        {
            currency = ShopPurchaser.NormalizeCurrency(offer.MoneyType, offer.Key);
            terms = offer.Stocks.Select(resolve).ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            logger.LogError("Gift {Key} cannot be bought: {Reason}", request.StockKey, ex.Message);
            return PresentSendStatus.NotGiftable;
        }
        if (currency == "cash" && !options.CashPresents)
            return PresentSendStatus.CurrencyRefused;

        var typed = request.ReceiverNickname.Trim();
        var receiver = typed.Length is 0 or > PresentLimits.MaxNicknameChars ? null
            : await store.FindPlayerAsync(typed, ct).ConfigureAwait(false);
        if (receiver is null)
            return PresentSendStatus.ReceiverMissing;
        if (receiver.UserNum == me.UserNum)
            return PresentSendStatus.SelfGift;

        // The receiver must be able to take the item as the shop's terms stand now; the same plan runs again at the
        // moment the receiver takes it.
        var bag = await inventory.ListSlotsAsync(receiver.UserNum, InventoryBag.CharacterBag,
            InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
        try
        {
            _ = ItemPurchasePlanner.Plan(receiver.UserNum, terms, bag, clock.GetUtcNow());
        }
        catch (PurchaseRejectedException ex)
        {
            logger.LogInformation("Gift {Key} from player {Sender} to player {Receiver} refused: {Reason}",
                request.StockKey, me.UserNum, receiver.UserNum, ex.Message);
            return PresentSendStatus.ReceiverOwnsItem;
        }

        var result = await store.SendAsync(new PresentSendCommand(Profile, new(me.UserNum, me.Nickname), receiver,
            request.StockKey, offer.StockSetId, offer.Stocks[0].ItemId, request.Message, currency, offer.Price,
            ShopPurchaser.SeedWallet(options, me.UserNum), clock.GetUtcNow().ToUnixTimeMilliseconds(),
            options.MaxPresentsPerPlayer), ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case PresentSendOutcome.InsufficientFunds: return PresentSendStatus.InsufficientFunds;
            case PresentSendOutcome.ReceiverBoxFull: return PresentSendStatus.ReceiverBoxFull;
            case PresentSendOutcome.SenderBoxFull: return PresentSendStatus.SenderBoxFull;
            case PresentSendOutcome.ReceiverMissing: return PresentSendStatus.ReceiverMissing;
            case PresentSendOutcome.Sent: break;
            default: throw new InvalidOperationException($"Unknown gift outcome {result.Outcome}.");
        }

        // The charge and the gift are committed; a failure past this point must not undo or repeat either.
        await balance.SendAsync(session, new(me, result.Wallet!), ct).ConfigureAwait(false);
        logger.LogInformation("Gift {Id} ({Key}) from player {Sender} to player {Receiver}: {Price} {Money} charged.",
            result.Present!.Id, request.StockKey, me.UserNum, receiver.UserNum, offer.Price, currency);
        await AnnounceAsync(receiver.UserNum, ct).ConfigureAwait(false);
        return PresentSendStatus.Sent;
    }

    /// <summary>Answers one page of the received or sent list (kr-812 sub_960BE0 asks for 4 at a time).</summary>
    internal async ValueTask ListAsync(ISession session, PresentListRequested request, CancellationToken ct)
    {
        var me = session.Feature<IPlayerSession>().Identity;
        var page = request.Box == PresentBox.Received
            ? await store.ListReceivedAsync(Profile, me.UserNum, request.Start, request.Count, ct).ConfigureAwait(false)
            : await store.ListSentAsync(Profile, me.UserNum, request.Start, request.Count, ct).ConfigureAwait(false);
        var pending = await store.CountPendingAsync(Profile, me.UserNum, ct).ConfigureAwait(false);
        await listReplies.SendAsync(session, new(request.Box, page.Total, pending, page.Records), ct).ConfigureAwait(false);
    }

    /// <summary>Takes, throws away or removes one gift.</summary>
    internal async ValueTask ActionAsync(ISession session, PresentActionRequested request, CancellationToken ct)
    {
        var me = session.Feature<IPlayerSession>().Identity;
        var succeeded = false;
        var key = 0;
        switch (request.Action)
        {
            case PresentAction.Receive:
                (succeeded, key) = await ReceiveAsync(session, me, request.Id, ct).ConfigureAwait(false);
                break;
            case PresentAction.Throw:
                succeeded = await store.DiscardAsync(Profile, me.UserNum, request.Id, ct).ConfigureAwait(false);
                break;
            case PresentAction.DeleteReceived:
                // Removing what is already gone leaves the list as the player wanted it (the client deletes a gift it
                // threw away, which the discard has removed already), so it is not a failure.
                _ = await store.DeleteReceivedAsync(Profile, me.UserNum, request.Id, ct).ConfigureAwait(false);
                succeeded = true;
                break;
            case PresentAction.DeleteSent:
                _ = await store.DeleteSentAsync(Profile, me.UserNum, request.Id, ct).ConfigureAwait(false);
                succeeded = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Action, "Unknown gift action.");
        }
        var pending = await store.CountPendingAsync(Profile, me.UserNum, ct).ConfigureAwait(false);
        await actionReplies.SendAsync(session, new(request.Action, succeeded, request.Id, key, pending), ct).ConfigureAwait(false);
    }

    private async ValueTask<(bool Succeeded, int StockKey)> ReceiveAsync(ISession session, PlayerIdentity me, long id, CancellationToken ct)
    {
        var present = await store.FindReceivedAsync(Profile, me.UserNum, id, ct).ConfigureAwait(false);
        if (present is not { State: PresentState.Pending })
            return (false, 0);
        var sale = catalog.FindPurchase(present.StockKey);
        var gift = present.SenderUserNum == PresentLimits.SystemSenderUserNum
            ? catalog.Gifts.GetValueOrDefault(present.StockKey) : null;
        if (sale is null && gift is null)
        {
            logger.LogError("Gift {Id} names shop key {Key}, which this build's workbook no longer sells; it stays in the box.",
                id, present.StockKey);
            return (false, present.StockKey);
        }
        ShopPurchaseResult? result;
        try
        {
            var terms = (gift?.Stocks ?? sale!.Stocks).Select(resolve).ToArray();
            var command = new ShopPurchaseCommand(ShopPurchaser.SeedWallet(options, me.UserNum), present.StockKey,
                gift?.StockSetId ?? sale!.StockSetId, "lucci", 0, ShopPurchaser.PurchaseSlotKey(me.UserNum, 0), [])
                { PresentId = id, LucciReward = gift?.Lucci ?? 0 };
            result = await purchaser.CommitAsync(command, terms, ct).ConfigureAwait(false);
        }
        catch (PurchaseRejectedException ex)
        {
            logger.LogInformation("Gift {Id} not taken by player {Player}: {Reason}", id, me.UserNum, ex.Message);
            return (false, present.StockKey);
        }
        catch (PresentUnavailableException)
        {
            // Taken or removed between the lookup and the commit (a repeated click): nothing changed.
            return (false, present.StockKey);
        }
        if (result is null)
            return (false, present.StockKey);
        await state.SendAsync(session, new(me, result.Wallet, result.Grants, false), ct).ConfigureAwait(false);
        logger.LogInformation("Gift {Id} ({Key}) taken by player {Player}: {Items} item(s) granted.",
            id, present.StockKey, me.UserNum, result.Grants.Count);
        return (true, present.StockKey);
    }

    /// <summary>Answers the empty new-gift query the client sends when it closes the box (kr-812 sub_962090).</summary>
    internal async ValueTask NoticeAsync(ISession session, PresentNoticeRequested request, CancellationToken ct)
    {
        var me = session.Feature<IPlayerSession>().Identity;
        await SendNoticeAsync(session, me.UserNum, ct).ConfigureAwait(false);
    }

    /// <summary>Raises the icon at login when gifts are waiting; the client's task bar exists once the menu is up.</summary>
    internal async ValueTask LoginAsync(LoginMenuInstalled login, CancellationToken ct)
    {
        if (login.Protocol != options.Protocol)
            return;
        var pending = await store.CountPendingAsync(Profile, login.Login.UserNumber, ct).ConfigureAwait(false);
        if (pending > 0)
            await notices.SendAsync(login.Session, new(true, pending), ct).ConfigureAwait(false);
    }

    private async ValueTask SendNoticeAsync(ISession session, long userNum, CancellationToken ct)
    {
        var pending = await store.CountPendingAsync(Profile, userNum, ct).ConfigureAwait(false);
        await notices.SendAsync(session, new(pending > 0, pending), ct).ConfigureAwait(false);
    }

    // Tells every live connection of the receiver on this build that a gift arrived. The gift is stored and paid for
    // already, so a connection that fails is logged and left to the next login; it must not fail the sender.
    internal async ValueTask AnnounceAsync(long receiverUserNum, CancellationToken ct)
    {
        if (sessions is null)
            return;
        foreach (var live in sessions.All)
        {
            if (live.Profile.Id != Profile || !live.TryFeature<IPlayerSession>(out var player)
                || player.Identity.UserNum != receiverUserNum)
                continue;
            try
            {
                await SendNoticeAsync(live, receiverUserNum, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The new-gift notice for player {Player} could not be sent on session {Session}.",
                    receiverUserNum, live.Id);
            }
        }
    }
}
