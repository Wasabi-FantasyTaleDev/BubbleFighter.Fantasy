using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Shop;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Shop.Handlers;

/// <summary>
/// Answers the legacy inventory-dialog entry. The server replies with the bag
/// restatement (the empty S2C 801 plus the whole bag on S2C 802) followed by the equipped
/// view and preset bar — never the shop state. The answer
/// is written through the build's typed senders, so this handler holds no wire layout.
/// </summary>
/// <param name="inventory">The store the bag is read from.</param>
/// <param name="replaced">The build's whole-inventory writer.</param>
/// <param name="equippedView">The view publication, or null when no view row is mounted.</param>
/// <param name="logger">Reports entries the view cannot describe.</param>
/// <param name="expiry">Removes the player's ended time-limited items before the bag is read, or null when expiry is off.</param>
/// <param name="expiredNotice">The build's native expiry notice, or null when the build has none.</param>
internal sealed class InventoryEntryHandler(
    IInventoryRepository inventory,
    IMessageSender<InventoryReplaced> replaced,
    IEquippedViewPush? equippedView,
    ILogger logger,
    IInventoryExpiry? expiry = null,
    IMessageSender<ItemsExpired>? expiredNotice = null)
{
    /// <summary>Publishes the bag restatement and, when a view provider is mounted, the equipped view.</summary>
    /// <param name="session">The session entering the inventory dialog.</param>
    /// <param name="request">The typed entry request.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async ValueTask HandleAsync(ISession session, ShopInventoryRequested request, CancellationToken ct)
    {
        if (!session.TryFeature<IPlayerSession>(out var player) || player is null)
            throw new InvalidOperationException("Inventory entry requires an authenticated player.");
        // Items that ended while the player was online leave the bag before it is restated.
        IReadOnlyList<int> expired = expiry is null ? [] : await expiry.SweepAsync(player.Identity.UserNum, ct).ConfigureAwait(false);
        var bag = await inventory.ListSlotsAsync(player.Identity.UserNum, InventoryBag.CharacterBag,
            InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
        // The equipped bag has no legacy wire form; its worn flags ride the character bag rows.
        await replaced.SendAsync(session, new InventoryReplaced(bag, []), ct).ConfigureAwait(false);
        await PublishViewAsync(session, (int)player.Identity.UserNum, bag, ct).ConfigureAwait(false);
        if (expired.Count > 0 && expiredNotice is not null)
            await expiredNotice.SendAsync(session, new ItemsExpired(expired), ct).ConfigureAwait(false);
    }

    private async ValueTask PublishViewAsync(ISession session, int userNum, IReadOnlyList<InventorySlotRecord> bag, CancellationToken ct)
    {
        if (equippedView is null)
            return;
        var worn = bag.Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0)
            .Select(row => row.ItemId).Distinct().ToArray();
        var character = worn.FirstOrDefault(id => id is >= 300001 and <= 300031);
        if (character == 0)
        {
            logger.LogWarning("Session {Id} player {User} wears no character; the equipped view is not published.",
                session.Id, userNum);
            return;
        }
        var ids = worn.Where(id => id != character).Prepend(character).ToArray();
        // A published selection re-reads the store inside the view push; these ids are the fallback.
        await equippedView.SendAsync(session, userNum, character, ids, ct).ConfigureAwait(false);
    }
}
