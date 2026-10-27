using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Shop.Data;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Shop.Handlers;

/// <summary>
/// Inbound handling for the client's own inventory-dialog open. The dialect row accepts the empty
/// <c>gcpEnterInventory</c> body and dispatches the typed inventory request; this handler answers it by flushing
/// every held container-2 equip record of the session's player as one burst, then clearing the held set so a
/// repeated dialog open sends nothing.
/// </summary>
/// <param name="hold">The held equip records, keyed by player.</param>
/// <param name="sender">The typed sender that writes the burst the build's S2C 803 reader expects.</param>
/// <param name="logger">The row logger.</param>
internal sealed class EquipHoldFlushHandler(EquipHoldStore hold, IMessageSender<EquippedArticlesFlushed> sender, ILogger logger)
{
    /// <summary>
    /// Handles the inventory-dialog open. The kr-1256 client skips <c>gspInventoryUpdateInventorySlots</c>
    /// container 2 until a my-fighter object exists (kr-1256 <c>sub_9AEB10</c>), and the weapon category
    /// <c>+1632</c> that the equip gate compares against is written only by the client's own <c>DlgInventory2</c>
    /// refresh (kr-1256 <c>sub_5F7C90</c> -> <c>sub_5F8C30(300, 0)</c>). kr-1256 <c>sub_932E10</c> runs that
    /// refresh and then stamps <c>gcpEnterInventory</c>, so the arrival of this request is the earliest point at
    /// which a held record can pass the gate.
    /// </summary>
    /// <param name="session">The session the dialog opened on.</param>
    /// <param name="request">The typed inventory request.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask HandleAsync(ISession session, ShopInventoryRequested request, CancellationToken ct)
    {
        var user = ShopPlugin.TryGetUserNum(session);
        if (user <= 0)
        {
            logger.LogError(
                "Session {Id} gcpEnterInventory has no lobby identity (players row missing); nothing can be flushed.",
                session.Id);
            return;
        }

        if (!hold.TryTake(user, out var records))
        {
            logger.LogInformation(
                "Session {Id} gcpEnterInventory: player {User} holds no equip record; nothing is sent.",
                session.Id, user);
            return;
        }

        await sender.SendAsync(session, new(records), ct).ConfigureAwait(false);
        logger.LogInformation(
            "Session {Id} gcpEnterInventory: flushed {Count} held container-2 equip record(s) for player {User} as one gspInventoryUpdateInventorySlots.",
            session.Id, records.Count, user);
    }
}
