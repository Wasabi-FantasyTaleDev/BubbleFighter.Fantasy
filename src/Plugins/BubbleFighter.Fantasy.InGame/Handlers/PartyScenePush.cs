using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.InGame.Dialects;
using BubbleFighter.Fantasy.InGame.Wire;

namespace BubbleFighter.Fantasy.InGame.Handlers;

/// <summary>Preserves shared membership when an existing scene path refreshes its recipient's appearance.</summary>
internal static class PartyScenePush
{
    internal static ValueTask SendAsync(ISession session, int userNum, string nickname, int characterId,
        IReadOnlyList<int>? equipment, IMessageSender<PartyUpdated>? sender, CancellationToken ct)
    {
        var party = session.TryFeature<IPartyMembership>(out var membership)
            ? membership.Party : new PartySnapshot([new(userNum, nickname)]);
        if (sender is not null)
            return sender.SendAsync(session, new PartyUpdated(party,
                new(userNum, characterId, equipment ?? [])), ct);
        if (party.Members.Count != 1 || party.Members[0].UserNum != userNum)
            throw new InvalidOperationException("A grouped scene refresh requires a party message sender.");
        return session.SendAsync(InGameOpcodes.S2CUpdateParty,
            RoomSessionBlob.UpdatePartySolo(userNum, nickname, characterId, equipment), ct);
    }
}
