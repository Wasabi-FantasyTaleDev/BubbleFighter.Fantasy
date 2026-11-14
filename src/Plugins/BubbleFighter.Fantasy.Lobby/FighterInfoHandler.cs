using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Lobby;

/// <summary>
/// Answers the player menu's "view info" item. The target is looked up among the logged-in players of the build, by the
/// account id the client names; an account that is not logged in gets the build's failure answer, never another
/// player's record and never the requester's own.
/// </summary>
internal static class FighterInfoHandler
{
    /// <summary>Answers one request.</summary>
    /// <param name="online">The build's logged-in players.</param>
    /// <param name="inventory">The same data domain's stored worn state; inspection never repairs or seeds it.</param>
    /// <param name="replies">The build's reply codec.</param>
    /// <param name="session">The requesting connection; it must be a logged-in player.</param>
    /// <param name="request">The request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="records">Optional battle store; when supplied, missing history gets the native failure reply.</param>
    /// <param name="events">Optional modifier projection over the inspected bag, without another inventory read.</param>
    /// <param name="loadouts">Read-only shared ownership projection when the deployment publishes a canonical build scope.</param>
    /// <param name="scope">The inventory row's canonical scope; inspection never materializes or repairs its selection.</param>
    /// <param name="petEntitlements">Optional native catalog used to recognize temporarily unavailable pet metadata without initializing it.</param>
    internal static async ValueTask HandleAsync(IOnlinePlayers online, IInventoryRepository inventory, IMessageSender<FighterInfoReplied> replies,
        ISession session, FighterInfoRequested request, CancellationToken ct, IPlayerBattleRecordsRepository? records = null,
        IEventBus? events = null, IInventoryLoadoutStore? loadouts = null, InventoryLoadoutScope? scope = null,
        IPetEntitlements? petEntitlements = null)
    {
        if ((loadouts is null) != (scope is null))
            throw new ArgumentException("Fighter inspection requires both the read-only loadout store and its canonical scope.");
        if (!session.TryFeature<IPlayerSession>(out var requester))
            throw new InvalidOperationException("A fighter info request requires an authenticated player.");
        var target = online.FindByUserNum(request.TargetUserNum);
        FighterInfoReplied reply = new(null);
        if (target is not null && target.TryFeature<IPlayerSession>(out var player)
            && player.Identity.UserNum == request.TargetUserNum)
        {
            var rows = loadouts is null
                ? await inventory.ListSlotsAsync(request.TargetUserNum, InventoryBag.CharacterBag,
                    InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false)
                : await loadouts.ListLoadoutSlotsAsync(request.TargetUserNum, scope!, InventoryBag.CharacterBag,
                    InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
            if (rows.Count > InventoryLimits.MaxInventorySlotsPerBag
                || rows.Any(row => row.UserNum != request.TargetUserNum || row.Bag != InventoryBag.CharacterBag || row.ItemId <= 0))
                throw new InvalidOperationException("The fighter inventory snapshot exceeds its bound or contains foreign rows.");
            var now = DateTimeOffset.UtcNow;
            // The native inventory map keeps the last stored row for an item identity.
            var selected = rows.GroupBy(row => row.ItemId).Select(group => group.Last())
                .Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0
                    && !InventoryExpiry.IsExpired(row, now)).ToArray();
            // An authenticated target can be visible before login commits its native pet metadata.
            // Inspection leaves that authoritative appearance unavailable until its owner initializes it.
            var unavailablePet = petEntitlements is not null && selected.Any(row => petEntitlements.IsPetItem(row.ItemId)
                && (row.PetIdentity is null || row.Pet?.Find(petEntitlements.BuildKey) is null));
            if (!unavailablePet)
            {
                var worn = selected.Select(row => row.ItemId).ToArray();
                var characters = worn.Where(id => id is >= 300001 and <= 300031).ToArray();
                var attributes = events is null ? []
                    : await InventoryPresentations.EquippedAsync(events, replies.Protocol, selected, worn, ct).ConfigureAwait(false);
                var history = records is null ? null
                    : await records.ListBattleRecordsAsync(request.TargetUserNum, ct).ConfigureAwait(false);
                // A second login may have replaced the owner while storage was read. Never pair its identity with
                // the departed session's snapshot, or silently manufacture a character for an incomplete bag.
                if ((records is null || history is not null) && characters.Length == 1
                    && ReferenceEquals(online.FindByUserNum(request.TargetUserNum), target)
                    && target.TryFeature<IPlayerSession>(out var current) && ReferenceEquals(current, player)
                    && current.Identity.UserNum == request.TargetUserNum)
                {
                    var character = characters[0];
                    var appearance = new FighterAppearance(character, [character, .. worn.Where(id => id != character)], attributes);
                    reply = new(current.Identity, appearance, history);
                }
            }
        }
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(online.FindByUserNum(requester.Identity.UserNum), session)
            || !session.TryFeature<IPlayerSession>(out var currentRequester) || !ReferenceEquals(requester, currentRequester))
            return;
        await replies.SendAsync(session, reply, ct).ConfigureAwait(false);
    }
}
