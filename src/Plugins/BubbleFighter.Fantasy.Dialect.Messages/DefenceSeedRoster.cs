using BubbleFighter.Fantasy.Contracts.Match;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>Selects the actors whose defence tables one seat's round request covers.</summary>
internal static class DefenceSeedRoster
{
    /// <summary>
    /// Returns the round's occupants, or the requesting seat alone when the request carries no roster. A roster must
    /// name the requesting seat with the same worn set the request carries.
    /// </summary>
    /// <param name="request">The seat's round request.</param>
    /// <returns>The occupants in seat order.</returns>
    /// <exception cref="InvalidOperationException">The roster omits the requesting seat or disagrees with it.</exception>
    internal static IReadOnlyList<MatchSeedOccupant> Occupants(MatchSeedTablesRequested request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Roster is not { } roster)
            return [new MatchSeedOccupant(request.EntityId, request.Equipment)];
        var own = roster.Occupants.Where(occupant => occupant.EntityId == request.EntityId).ToArray();
        if (own.Length != 1 || !Equals(own[0].Equipment, request.Equipment))
            throw new InvalidOperationException("The seed roster must carry the requesting seat with its own worn set.");
        return roster.Occupants;
    }
}
