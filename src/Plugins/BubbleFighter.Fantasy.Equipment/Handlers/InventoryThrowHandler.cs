using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Equipment.Handlers;

/// <summary>Removes one owned expired identity and synchronizes scene, bag and equipment state without starter top-up.</summary>
/// <param name="removal">Checks expiration under purchase serialization and repairs a removed character.</param>
/// <param name="selection">Resolves the authoritative equipment after the removal and repair.</param>
/// <param name="inventoryPush">Publishes existing slot records without seeding the login inventory.</param>
/// <param name="equippedView">Publishes the same selected equipment to the requesting client.</param>
/// <param name="replies">Echoes the action identity and native status.</param>
/// <param name="events">Updates scene owners from the selected equipment before acknowledging it.</param>
/// <param name="excludeCategorySix">Whether the native discard entry rejects category 6, as stated by the build's capability.</param>
public sealed class InventoryThrowHandler(IInventoryItemRemoval removal, IEquipmentSelection selection,
    IInventoryPushService inventoryPush, IEquippedViewPush equippedView,
    IMessageSender<InventoryThrowReplied> replies, IEventBus events, bool excludeCategorySix)
{
    /// <summary>Handles a typed action using only the authenticated player's inventory and admitted scene phases.</summary>
    /// <param name="session">The requesting game session.</param>
    /// <param name="request">The selected item identity.</param>
    /// <param name="ct">Cancellation of storage and ordered publications.</param>
    public async ValueTask HandleAsync(ISession session, InventoryThrowRequested request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var player = session.Feature<IPlayerSession>();
        if (player.Identity.UserNum <= 0) throw new InvalidOperationException("Inventory throw requires an authenticated player.");
        // kr-1256 sub_8136F0, cn-774 sub_9B4BC0 and tw-454 sub_C12C40 exclude category 6.
        // Category 6 is 301000..399999 in kr-812 sub_4FB720 and all three modern categorization functions.
        if (request.ItemId <= 0 || (excludeCategorySix && request.ItemId is >= 301000 and <= 399999)
            || player.Phase is not (SessionPhase.MainMenu or SessionPhase.Plaza or SessionPhase.Room))
        {
            await ReplyAsync(1).ConfigureAwait(false);
            return;
        }
        var user = player.Identity.UserNum;
        var removed = await removal.RemoveItemAsync(user, request.ItemId, ct).ConfigureAwait(false);
        if (removed is null)
        {
            await ReplyAsync(1).ConfigureAwait(false);
            return;
        }
        var selected = await selection.ReadAsync(user, ct).ConfigureAwait(false);
        if (selected.Count > InventoryLimits.MaxInventorySlotsPerBag)
            throw new InvalidOperationException("The inventory throw selection exceeds the inventory slot limit.");
        var snapshot = Array.AsReadOnly(selected.ToArray());
        if (snapshot.Count > InventoryLimits.MaxInventorySlotsPerBag
            || snapshot.Any(row => row is null || row.UserNum != user || row.Bag != InventoryBag.CharacterBag || row.ItemId <= 0))
            throw new InvalidOperationException("The inventory throw selection has an invalid owner, bag, item or slot count.");
        var worn = Array.AsReadOnly(snapshot.Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0)
            .Select(row => row.ItemId).Distinct().ToArray());
        var characters = worn.Where(id => id is >= 300001 and <= 300031).ToArray();
        if (characters.Length != 1 || worn.Count > InventoryLimits.MaxEquippedSlots)
            throw new InvalidOperationException("Inventory throw requires one selected character and a bounded equipment set.");
        await events.SerialAsync(new EquipmentSelectionCommitted(session, user, characters[0], worn), ct).ConfigureAwait(false);
        // Native success erases the identity; slot upserts then restore any replacement from this snapshot.
        await ReplyAsync(0).ConfigureAwait(false);
        await inventoryPush.PushSlotUpdatesAsync(session, snapshot, ct).ConfigureAwait(false);
        await equippedView.SendSelectionAsync(session, user, characters[0], worn, ct).ConfigureAwait(false);

        ValueTask ReplyAsync(int result) => replies.SendAsync(session, new(request.ItemId, result), ct);
    }
}
