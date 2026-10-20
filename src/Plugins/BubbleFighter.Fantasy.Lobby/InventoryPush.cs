using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Lobby.Data;

namespace BubbleFighter.Fantasy.Lobby;

/// <summary>
/// Republishes a player's stored inventory for other rows (equipment, shop) through the build's
/// published inventory senders: this class decides what is sent, the senders how it is written.
/// </summary>
/// <param name="resolver">The stored inventory, seeded on first use.</param>
/// <param name="replaced">The dialect's whole-inventory sender.</param>
/// <param name="purchase">The dialect's after-purchase sender.</param>
/// <param name="slots">The dialect's slot sender.</param>
public sealed class InventoryPush(
    LoginInventoryResolver resolver,
    IMessageSender<InventoryReplaced> replaced,
    IMessageSender<InventoryPurchaseApplied> purchase,
    IMessageSender<InventorySlotsUpdated> slots) : IInventoryPushService
{
    /// <inheritdoc />
    public ValueTask PushSlotUpdatesAsync(ISession session, IReadOnlyList<InventorySlotRecord> updated, CancellationToken ct) =>
        slots.SendAsync(session, new InventorySlotsUpdated(updated), ct);

    /// <inheritdoc />
    public async ValueTask PushInventoryAsync(ISession session, long userNum, CancellationToken ct)
    {
        var snapshot = await resolver.ResolveAsync(userNum, ct).ConfigureAwait(false);
        await replaced.SendAsync(session, new InventoryReplaced(snapshot.CharacterBag, snapshot.EquippedBag), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask PushPurchaseAsync(ISession session, long userNum, IReadOnlyList<InventorySlotRecord> grants, CancellationToken ct)
    {
        var snapshot = await resolver.ResolveAsync(userNum, ct).ConfigureAwait(false);
        await purchase.SendAsync(session, new InventoryPurchaseApplied(snapshot.CharacterBag), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask PushSlotUpdateAsync(ISession session, InventorySlotRecord slot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return slots.SendAsync(session, new InventorySlotsUpdated([slot]), ct);
    }
}
