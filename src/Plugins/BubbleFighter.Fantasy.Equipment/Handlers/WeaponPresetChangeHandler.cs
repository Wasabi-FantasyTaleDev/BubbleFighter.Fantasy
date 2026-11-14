using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Equipment.Handlers;

/// <summary>
/// Grants or refuses weapon preset switches. The published bar is server-derived from the worn
/// set (<see cref="WeaponPresetSlots"/>) until the player's preset pages were materialized
/// (<c>persist_preset_pages</c>), and materialized pages win: their first words become the bar's
/// binding, so pages 3-4 - dead in the derived bar, which publishes no record for them - are
/// grantable exactly when their page carries a weapon. The client's send gate
/// (<c>kr-812 sub_956300</c>) still suppresses requests for slots whose published flag is unset.
/// A granted slot persists through <see cref="IPlayerInventoryState"/> before the view republishes
/// it, matching the recorded order view, preset bar, status reply.
/// </summary>
/// <param name="playerState">Persists the active slot and the materialized preset pages across logins.</param>
/// <param name="selection">Reads the worn set the published bar derives from; may be null on
/// an ack-only row, which then refuses every switch.</param>
/// <param name="equippedView">Republishes the appearance and the bar after a grant.</param>
/// <param name="replies">Writes the build's status reply; it always runs so the client's
/// latched sender re-arms.</param>
/// <param name="logger">Reports refused and granted switches.</param>
/// <param name="presetPages">The build-scoped persisted-bar policy, absent when the build uses a derived bar.</param>
/// <param name="wear">Wears the granted page's weapon through the ordinary equipment commit; absent on an ack-only row.</param>
public sealed class WeaponPresetChangeHandler(
    IPlayerInventoryState playerState,
    IEquipmentSelection? selection,
    IEquippedViewPush? equippedView,
    IMessageSender<WeaponPresetChangeReplied> replies,
    ILogger logger,
    IWeaponPresetPageSource? presetPages = null,
    EquipmentHandlers? wear = null)
{
    /// <summary>Handles one decoded preset request.</summary>
    /// <param name="session">The requesting session and its authenticated player feature.</param>
    /// <param name="request">The requested slot and its opaque client context.</param>
    /// <param name="ct">Cancellation for the store write and the ordered publications.</param>
    public async ValueTask HandleAsync(ISession session, WeaponPresetChangeRequested request, CancellationToken ct)
    {
        var user = EquipmentPlugin.TryGetUserNum(session);
        if (user <= 0)
        {
            logger.LogError("Session {Session} preset switch to slot {Slot} refused: no authenticated player.",
                session.Id, request.Slot);
            await ReplyAsync(0, 1).ConfigureAwait(false);
            return;
        }

        var current = await playerState.GetActiveWeaponPresetSlotAsync(user, ct).ConfigureAwait(false);
        var worn = Array.Empty<int>();
        IReadOnlyList<InventorySlotRecord>? snapshot = null;
        if (selection is not null)
        {
            var bag = await selection.ReadAsync(user, ct).ConfigureAwait(false);
            if (bag.Count > InventoryLimits.MaxInventorySlotsPerBag)
                throw new InvalidOperationException("The preset equipment snapshot exceeds the inventory slot limit.");
            snapshot = Array.AsReadOnly(bag.ToArray());
            if (snapshot.Any(row => row is null || row.UserNum != user || row.Bag != InventoryBag.CharacterBag || row.ItemId <= 0))
                throw new InvalidOperationException("The preset equipment snapshot has an invalid owner, bag or item.");
            worn = snapshot.Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0)
                .Select(row => row.ItemId).Distinct().ToArray();
        }
        // Materialized pages are the published bar, so they are also the binding: a page with no weapon
        // (first word zero) is the unbound slot the client will not offer and the server refuses.
        var pages = presetPages is null ? null
            : await presetPages.ReadAsync(user, ct).ConfigureAwait(false);
        var slots = pages is null
            ? WeaponPresetSlots.FromWorn(worn)
            : pages.Select(page => page.Unknown0).ToArray();
        if (request.Slot > InventoryLimits.MaxWeaponPresetSlot
            || request.Slot >= slots.Length
            || slots[request.Slot] == 0)
        {
            logger.LogWarning("Session {Session} player {User} preset switch to slot {Slot} refused: unbound.",
                session.Id, user, request.Slot);
            await ReplyAsync(current, 1).ConfigureAwait(false);
            return;
        }

        await playerState.SetActiveWeaponPresetSlotAsync(user, request.Slot, ct).ConfigureAwait(false);
        // A persisted page is the weapon the player chose for it: wear it when it is owned and not already worn, so the
        // equipped view, the room and the round see the page's weapon. kr-812 sub_832BD0 and kr-1256 case 836 rebuild
        // the fighter from the record in-match; the menu draws the worn view, which only the server can change.
        if (pages is not null && wear is not null && snapshot is not null && !worn.Contains(slots[request.Slot])
            && snapshot.Where(row => row.ItemId == slots[request.Slot]).Take(2).ToArray() is [var owned])
        {
            var committed = await wear.WearForPresetAsync(session, user, owned, ct).ConfigureAwait(false);
            if (committed is not null)
            {
                snapshot = committed;
                worn = committed.Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0)
                    .Select(row => row.ItemId).Distinct().ToArray();
            }
            else
                logger.LogWarning("Session {Session} player {User} preset slot {Slot}: page weapon {Item} could not be worn; the worn set is kept.",
                    session.Id, user, request.Slot, slots[request.Slot]);
        }
        // The view republishes the worn set and reads the slot back from the store, so the
        // preset bar's leading byte always reflects the persisted grant.
        var character = worn.FirstOrDefault(id => id is >= 300001 and <= 300031);
        if (equippedView is not null && character != 0 && snapshot is not null)
        {
            await equippedView.SendPresetSwitchAsync(session, checked((int)user), character, snapshot, ct).ConfigureAwait(false);
        }
        await ReplyAsync(request.Slot, 0).ConfigureAwait(false);
        logger.LogInformation("Session {Session} player {User} weapon preset slot {Slot} granted and persisted.",
            session.Id, user, request.Slot);

        // kr-812 sub_832BD0 tests zero, not a particular failure code; one is our refusal value.
        ValueTask ReplyAsync(byte slot, int status) =>
            replies.SendAsync(session, new WeaponPresetChangeReplied(request, slot) { Status = status }, ct);
    }
}
