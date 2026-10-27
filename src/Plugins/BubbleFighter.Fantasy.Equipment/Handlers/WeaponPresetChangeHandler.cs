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
public sealed class WeaponPresetChangeHandler(
    IPlayerInventoryState playerState,
    IEquipmentSelection? selection,
    IEquippedViewPush? equippedView,
    IMessageSender<WeaponPresetChangeReplied> replies,
    ILogger logger,
    IWeaponPresetPageSource? presetPages = null)
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
        if (selection is not null)
        {
            var bag = await selection.ReadAsync(user, ct).ConfigureAwait(false);
            worn = bag.Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0)
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
        // The view republishes the worn set and reads the slot back from the store, so the
        // preset bar's leading byte always reflects the persisted grant.
        var character = worn.FirstOrDefault(id => id is >= 300001 and <= 300031);
        if (equippedView is not null && character != 0)
        {
            var ids = worn.Where(id => id != character).Prepend(character).ToArray();
            await equippedView.SendSelectionAsync(session, checked((int)user), character, ids, ct).ConfigureAwait(false);
        }
        await ReplyAsync(request.Slot, 0).ConfigureAwait(false);
        logger.LogInformation("Session {Session} player {User} weapon preset slot {Slot} granted and persisted.",
            session.Id, user, request.Slot);

        // kr-812 sub_832BD0 tests zero, not a particular failure code; one is our refusal value.
        ValueTask ReplyAsync(byte slot, int status) =>
            replies.SendAsync(session, new WeaponPresetChangeReplied(request, slot) { Status = status }, ct);
    }
}
