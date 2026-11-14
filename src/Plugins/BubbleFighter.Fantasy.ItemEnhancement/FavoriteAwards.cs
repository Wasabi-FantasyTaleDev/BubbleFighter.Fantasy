using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.ItemEnhancement.Data;

namespace BubbleFighter.Fantasy.ItemEnhancement;

/// <summary>Announces what a committed Favorite award changed once the inventory it belongs to has been published, then raises the progress event.</summary>
internal sealed class FavoriteAwards(FavoriteWeaponCatalog catalog, IMessageSender<FavoriteWeaponGradeChanged> notices,
    IEventBus events, string protocol, string scopeKey)
{
    /// <summary>
    /// Sends the grade notice when <paramref name="points"/> carried <paramref name="target"/> across a grade threshold. The grade is
    /// derived from the row as it stood before the award, so a stale plan that awarded nothing is never announced.
    /// </summary>
    internal async ValueTask NotifyAsync(ISession session, InventorySlotRecord target, int points, CancellationToken ct)
    {
        if (points <= 0) return;
        var before = target.Favorites?.Find(scopeKey) ?? 0;
        var oldGrade = catalog.GradeOf(before);
        var newGrade = catalog.GradeOf(checked(before + points));
        if (oldGrade != newGrade)
            await notices.SendAsync(session, new(target.ItemId, (int)oldGrade, (int)newGrade), ct).ConfigureAwait(false);
        // Rules that depend on the standing, such as the Favorite titles, re-read the committed Articles.
        await events.SerialAsync(new FavoriteProgressCommitted(protocol, session), ct).ConfigureAwait(false);
    }
}