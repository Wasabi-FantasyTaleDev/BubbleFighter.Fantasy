using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Rooms;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    private async ValueTask OnPlazaEquipmentPublishedAsync(EquipmentViewPublished publication, CancellationToken ct)
    {
        var view = publication.View;
        var snapshot = new RoundEquipmentSnapshot(view.EquippedItemIds, view.ItemAttributes);
        await _plaza.Executor.ExecuteAsync(async token =>
        {
            PlazaMember shown;
            PlazaPositionReceived? position;
            ISession[] observers;
            lock (_plaza.Gate)
            {
                var member = _plaza.Find(publication.Session.Id);
                if (member is null || member.Remote || member.Reserved || member.UserNumber != view.UserNum
                    || !ReferenceEquals(member.Game, publication.Session) || _registry.TryGet(publication.Session.Id) is not null)
                    return;
                if (member.CharacterItemId == view.CharacterId && member.EquippedItemIds.SequenceEqual(snapshot.WornItemIds)
                    && Equal(member.ItemAttributes, snapshot.ItemAttributes)) return;
                member.CharacterItemId = view.CharacterId;
                member.EquippedItemIds = snapshot.WornItemIds;
                member.ItemAttributes = snapshot.ItemAttributes;
                shown = member.ToMember();
                position = member.LastPosition.IsEmpty ? null
                    : new(member.UserNumber, member.PositionSequence, member.LastPosition, member.EntrySeed);
                observers = _plaza.Sessions().Where(other => other.SessionId != member.SessionId
                    && _registry.TryGet(other.SessionId) is null).Select(other => other.Game!).ToArray();
            }
            if (_plazaMemberEntered is null) throw new InvalidOperationException("Plaza appearance changes require a member sender.");
            foreach (var observer in observers)
            {
                await NotifyScenePeerAsync(observer, _plazaMemberEntered, new PlazaMemberEntered(shown), token).ConfigureAwait(false);
                if (_plazaPositionReceived is not null && position is not null)
                    await NotifyScenePeerAsync(observer, _plazaPositionReceived, position, token).ConfigureAwait(false);
            }
            PublishPlaza(new(PlazaFederationEventKind.Entered, shown.UserNumber, shown, default));
        }, ct).ConfigureAwait(false);
    }
}
