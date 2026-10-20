using System.Diagnostics;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Equipment.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Equipment.Handlers;

/// <summary>Applies typed equipment requests and publishes each committed selection in event, result, inventory and view order.</summary>
/// <param name="equippedItems">Bounded connection-local state for ack-only and direct flag-only callers.</param>
/// <param name="inventory">Persistence for worn state, or null for ack-only mode.</param>
/// <param name="push">Publishes the committed inventory slots.</param>
/// <param name="logger">Reports rejected targets and completed selections.</param>
/// <param name="replies">Encodes results for this connection protocol.</param>
/// <param name="selection">Atomically commits exclusive equipment positions; required by persisted plugin activation.</param>
/// <param name="equippedView">Publishes the same committed selection without rereading storage.</param>
/// <param name="events">Notifies scene owners before acknowledging the selection.</param>
public sealed class EquipmentHandlers(
    EquippedItemScope equippedItems,
    IInventoryRepository? inventory,
    IInventoryPushService? push,
    ILogger logger,
    IMessageSender<EquipmentChangeReplied> replies,
    IEquipmentSelection? selection = null,
    IEquippedViewPush? equippedView = null,
    IEventBus? events = null)
{
    /// <summary>Handles a decoded request; unsupported targets fail without storage access in persisted mode.</summary>
    /// <param name="session">The requesting session and its authenticated player feature.</param>
    /// <param name="request">The target resolved by the connection's codec and its unchanged correlation reference.</param>
    /// <param name="ct">Cancellation for the commit and ordered publications.</param>
    public async ValueTask HandleAsync(ISession session, EquipmentChangeRequested request, CancellationToken ct)
    {
        if (inventory is not null && push is not null)
        {
            var user = EquipmentPlugin.TryGetUserNum(session);
            if (user <= 0 || request.ItemId is not > 0)
            {
                logger.LogError("Session {Session} equipment reference {Reference} refused: missing player identity or unsupported target.",
                    session.Id, request.Reference);
                await ReplyAsync(false).ConfigureAwait(false);
                return;
            }

            if (selection is not null && equippedView is not null)
            {
                var identity = checked((int)user);
                var started = Stopwatch.GetTimestamp();
                var bag = await selection.ApplyAsync(user, request.ItemId.Value, request.Worn, ct).ConfigureAwait(false);
                var commitMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (bag is null)
                {
                    await ReplyAsync(false).ConfigureAwait(false);
                    return;
                }

                if (bag.Count > InventoryLimits.MaxInventorySlotsPerBag)
                    throw new InvalidOperationException("The committed equipment bag exceeds the inventory slot limit.");
                // Copy before the first publication can yield; every recipient must observe this commit.
                var snapshot = Array.AsReadOnly(bag.ToArray());
                if (snapshot.Count > InventoryLimits.MaxInventorySlotsPerBag
                    || snapshot.Any(row => row is null || row.UserNum != user || row.Bag != InventoryBag.CharacterBag || row.ItemId <= 0))
                    throw new InvalidOperationException("The committed equipment bag has an invalid owner, bag or item.");
                var ids = Array.AsReadOnly(snapshot.Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0)
                    .Select(row => row.ItemId).Distinct().ToArray());
                var characters = ids.Where(id => id is >= 300001 and <= 300031).ToArray();
                if (ids.Count > InventoryLimits.MaxEquippedSlots || characters.Length != 1)
                    throw new InvalidOperationException("The committed equipment selection requires exactly one usable character and bounded items.");
                var character = characters[0];
                if (events is not null)
                    await events.SerialAsync(new EquipmentSelectionCommitted(session, identity, character, ids), ct).ConfigureAwait(false);
                var replyAfterInventory = replies is IEquipmentReplyPolicy { ReplyAfterInventory: true };
                if (!replyAfterInventory)
                    await ReplyAsync(true).ConfigureAwait(false);
                await push.PushSlotUpdatesAsync(session, snapshot, ct).ConfigureAwait(false);
                await equippedView.SendSelectionAsync(session, identity, character, ids, ct).ConfigureAwait(false);
                if (replyAfterInventory)
                    await ReplyAsync(true).ConfigureAwait(false);
                logger.LogInformation("Session {Session} player {User} equipment item {Item} worn={Worn}; inventory and view synchronized; commit={CommitMs:F1}ms total={TotalMs:F1}ms.",
                    session.Id, user, request.ItemId, request.Worn, commitMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return;
            }

            // Direct flag-only callers remain supported; persisted plugin activation requires selection and view.
            var updated = await inventory.SetSlotWornAsync(user, request.ItemId.Value, request.Worn, ct).ConfigureAwait(false);
            if (updated is null)
            {
                await ReplyAsync(false).ConfigureAwait(false);
                return;
            }
            RecordInSessionSet(session, request);
            await ReplyAsync(true).ConfigureAwait(false);
            await push.PushSlotUpdateAsync(session, updated, ct).ConfigureAwait(false);
            return;
        }

        RecordInSessionSet(session, request);
        await ReplyAsync(true).ConfigureAwait(false);
        logger.LogInformation("Session {Session} equipment reference {Reference} worn={Worn} acknowledged without persistence.",
            session.Id, request.Reference, request.Worn);

        ValueTask ReplyAsync(bool succeeded) => replies.SendAsync(session, new EquipmentChangeReplied(request, succeeded), ct);
    }

    private void RecordInSessionSet(ISession session, EquipmentChangeRequested request)
    {
        var set = equippedItems.GetOrAdd(session.Connection.Id);
        if (request.Worn) set.Equip(request.Reference);
        else set.Unequip(request.Reference);
    }
}
