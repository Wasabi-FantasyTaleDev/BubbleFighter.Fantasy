using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Inventory;
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
    internal static async ValueTask HandleAsync(IOnlinePlayers online, IInventoryRepository inventory, IMessageSender<FighterInfoReplied> replies,
        ISession session, FighterInfoRequested request, CancellationToken ct, IPlayerBattleRecordsRepository? records = null)
    {
        if (!session.TryFeature<IPlayerSession>(out var requester))
            throw new InvalidOperationException("A fighter info request requires an authenticated player.");
        var target = online.FindByUserNum(request.TargetUserNum);
        FighterInfoReplied reply = new(null);
        if (target is not null && target.TryFeature<IPlayerSession>(out var player)
            && player.Identity.UserNum == request.TargetUserNum)
        {
            var rows = await inventory.ListSlotsAsync(request.TargetUserNum, InventoryBag.CharacterBag,
                InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
            if (rows.Count > InventoryLimits.MaxInventorySlotsPerBag
                || rows.Any(row => row.UserNum != request.TargetUserNum || row.Bag != InventoryBag.CharacterBag || row.ItemId <= 0))
                throw new InvalidOperationException("The fighter inventory snapshot exceeds its bound or contains foreign rows.");
            var now = DateTimeOffset.UtcNow;
            // The native inventory map keeps the last stored row for an item identity.
            var worn = rows.GroupBy(row => row.ItemId).Select(group => group.Last())
                .Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0
                    && !InventoryExpiry.IsExpired(row, now))
                .Select(row => row.ItemId).ToArray();
            var characters = worn.Where(id => id is >= 300001 and <= 300031).ToArray();
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
                var appearance = new FighterAppearance(character, [character, .. worn.Where(id => id != character)]);
                reply = new(current.Identity, appearance, history);
            }
        }
        ct.ThrowIfCancellationRequested();
        if (!ReferenceEquals(online.FindByUserNum(requester.Identity.UserNum), session)
            || !session.TryFeature<IPlayerSession>(out var currentRequester) || !ReferenceEquals(requester, currentRequester))
            return;
        await replies.SendAsync(session, reply, ct).ConfigureAwait(false);
    }
}
