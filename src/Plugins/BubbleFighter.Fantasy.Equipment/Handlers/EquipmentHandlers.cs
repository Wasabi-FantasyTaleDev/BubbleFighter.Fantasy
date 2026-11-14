using System.Diagnostics;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Pets;
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
/// <param name="pets">Resolves owned Article targets without exposing their keys to item policies.</param>
/// <param name="inventories">Publishes both bags returned by an Article selection transaction.</param>
/// <param name="articles">Resolves equipment Articles when their owning feature is enabled.</param>
public sealed class EquipmentHandlers(
    EquippedItemScope equippedItems,
    IInventoryRepository? inventory,
    IInventoryPushService? push,
    ILogger logger,
    IMessageSender<EquipmentChangeReplied> replies,
    IEquipmentSelection? selection = null,
    IEquippedViewPush? equippedView = null,
    IEventBus? events = null,
    IPetEntitlements? pets = null,
    IMessageSender<InventoryReplaced>? inventories = null,
    IEquipmentArticleResolver? articles = null)
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
            var petTarget = user > 0 && request.ItemId is null && pets is not null
                ? await pets.ResolveAsync(user, unchecked((ulong)request.Reference), ct).ConfigureAwait(false) : null;
            var articleTarget = user > 0 && request.ItemId is null && petTarget is null && articles is not null
                ? await articles.ResolveAsync(user, unchecked((ulong)request.Reference), ct).ConfigureAwait(false) : null;
            if (articleTarget is not null && (articleTarget.UserNum != user || articleTarget.Bag != InventoryBag.CharacterBag
                || InventoryArticleIdentityResolver.Of(articleTarget)?.ArticleKey != unchecked((ulong)request.Reference)))
                throw new InvalidDataException("Equipment Article resolution changed the requested ownership.");
            var itemId = petTarget?.ItemId ?? articleTarget?.ItemId ?? request.ItemId;
            if (user <= 0 || itemId is not > 0)
            {
                logger.LogError("Session {Session} equipment reference {Reference} refused: missing player identity or unsupported target.",
                    session.Id, request.Reference);
                await ReplyAsync(false).ConfigureAwait(false);
                return;
            }

            if (selection is not null && equippedView is not null)
            {
                var identity = checked((int)user);
                try
                {
                    // A waiting room holds its start from here until every publication below has finished. Starting is
                    // inside the try: a later subscriber's failure or a cancellation after the room marked the seat must
                    // still settle it, and a settlement without a mark is ignored.
                    if (events is not null)
                        await events.SerialAsync(new EquipmentSelectionStarting(session), ct).ConfigureAwait(false);
                    await ApplySelectionAsync(session, request, user, identity, petTarget, articleTarget, itemId.Value, selection, equippedView, push, ct)
                        .ConfigureAwait(false);
                }
                finally
                {
                    // Always settled, cancelled requests included: an open mark would hold the room's start forever.
                    if (events is not null)
                        await events.SerialAsync(new EquipmentSelectionSettled(session), CancellationToken.None).ConfigureAwait(false);
                }
                return;
            }

            // Direct flag-only callers remain supported; persisted plugin activation requires selection and view.
            if (petTarget is not null || articleTarget is not null)
            {
                await ReplyAsync(false).ConfigureAwait(false);
                return;
            }
            var updated = await inventory.SetSlotWornAsync(user, itemId.Value, request.Worn, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Wears an owned weapon on behalf of a granted preset switch through the same commit, event and inventory
    /// publication as a client equip, without an equip result (the client sent no equip request) and without the view,
    /// which the switch republishes after recording its slot.
    /// </summary>
    /// <param name="session">The switching session.</param>
    /// <param name="user">The authenticated player.</param>
    /// <param name="target">The owned character-bag row the switched-to page names.</param>
    /// <param name="ct">Cancellation for the commit and publications.</param>
    /// <returns>The committed character bag, or null when the selection was refused or no persisted selection exists.</returns>
    internal async ValueTask<IReadOnlyList<InventorySlotRecord>?> WearForPresetAsync(ISession session, long user,
        InventorySlotRecord target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (inventory is null || push is null || selection is null || equippedView is null || user <= 0
            || target.UserNum != user || target.Bag != InventoryBag.CharacterBag)
            return null;
        var request = new EquipmentChangeRequested(target.ItemId, target.ItemId, true);
        var articleTarget = articles is not null && InventoryArticleIdentityResolver.Of(target) is not null ? target : null;
        try
        {
            if (events is not null)
                await events.SerialAsync(new EquipmentSelectionStarting(session), ct).ConfigureAwait(false);
            return await ApplySelectionAsync(session, request, user, checked((int)user), null, articleTarget, target.ItemId,
                selection, equippedView, push, ct, quiet: true).ConfigureAwait(false);
        }
        finally
        {
            if (events is not null)
                await events.SerialAsync(new EquipmentSelectionSettled(session), CancellationToken.None).ConfigureAwait(false);
        }
    }

    // Commits the selection (an owned Article through its entitlement transaction) and publishes it in event, result,
    // inventory and view order from one committed snapshot; the caller settles the write. A quiet commit (a preset
    // switch) sends neither the result nor the view.
    private async ValueTask<IReadOnlyList<InventorySlotRecord>?> ApplySelectionAsync(ISession session, EquipmentChangeRequested request,
        long user, int identity, PetEquipmentTarget? petTarget, InventorySlotRecord? articleTarget, int itemId,
        IEquipmentSelection selection, IEquippedViewPush equippedView, IInventoryPushService push, CancellationToken ct, bool quiet = false)
    {
        var started = Stopwatch.GetTimestamp();
        if ((pets is not null || articles is not null) && inventories is null)
            throw new InvalidOperationException("Article equipment requires complete inventory publication.");
        var articleSnapshot = articleTarget is not null
            ? await selection.ApplyArticleAsync(user, articleTarget, request.Worn, ct).ConfigureAwait(false) : null;
        var petSnapshot = articleTarget is not null ? null : petTarget is not null
            ? await selection.ApplyPetAsync(user, petTarget, request.Worn, ct).ConfigureAwait(false)
            : pets is not null || articles is not null ? await selection.ApplySnapshotAsync(user, itemId, request.Worn, ct).ConfigureAwait(false) : null;
        var bag = articleTarget is not null ? articleSnapshot?.CharacterBag : pets is null && articles is null
            ? await selection.ApplyAsync(user, itemId, request.Worn, ct).ConfigureAwait(false)
            : petSnapshot?.CharacterBag;
        var commitMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (bag is null)
        {
            if (!quiet) await ReplyAsync(false).ConfigureAwait(false);
            return null;
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
        {
            var attributes = await InventoryPresentations.EquippedAsync(events, replies.Protocol, snapshot, ids, ct).ConfigureAwait(false);
            await events.SerialAsync(new EquipmentSelectionCommitted(session, identity, character, ids)
                { ItemAttributes = attributes }, ct).ConfigureAwait(false);
        }
        var replyAfterInventory = replies is IEquipmentReplyPolicy { ReplyAfterInventory: true };
        if (!replyAfterInventory && !quiet)
            await ReplyAsync(true).ConfigureAwait(false);
        if (articleSnapshot is not null)
            await inventories!.SendAsync(session, new(articleSnapshot.CharacterBag, articleSnapshot.EquippedBag)
                { IsCompleteSnapshot = true }, ct).ConfigureAwait(false);
        else if (petSnapshot is null)
            await push.PushSlotUpdatesAsync(session, snapshot, ct).ConfigureAwait(false);
        else
            await inventories!.SendAsync(session, new(petSnapshot.CharacterBag, petSnapshot.EquippedBag)
                { IsCompleteSnapshot = true }, ct).ConfigureAwait(false);
        if (quiet) return snapshot;
        await equippedView.SendSnapshotAsync(session, identity, character, snapshot, ct).ConfigureAwait(false);
        if (replyAfterInventory)
            await ReplyAsync(true).ConfigureAwait(false);
        logger.LogInformation("Session {Session} player {User} equipment item {Item} worn={Worn}; inventory and view synchronized; commit={CommitMs:F1}ms total={TotalMs:F1}ms.",
            session.Id, user, itemId, request.Worn, commitMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return snapshot;

        ValueTask ReplyAsync(bool succeeded) => replies.SendAsync(session, new EquipmentChangeReplied(request, succeeded), ct);
    }

    private void RecordInSessionSet(ISession session, EquipmentChangeRequested request)
    {
        var set = equippedItems.GetOrAdd(session.Connection.Id);
        if (request.Worn) set.Equip(request.Reference);
        else set.Unequip(request.Reference);
    }
}
