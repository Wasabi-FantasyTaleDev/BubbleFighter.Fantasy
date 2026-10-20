using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Players;
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
    /// <param name="replies">The build's reply codec.</param>
    /// <param name="session">The requesting connection; it must be a logged-in player.</param>
    /// <param name="request">The request.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static ValueTask HandleAsync(IOnlinePlayers online, IMessageSender<FighterInfoReplied> replies,
        ISession session, FighterInfoRequested request, CancellationToken ct)
    {
        if (!session.TryFeature<IPlayerSession>(out _))
            throw new InvalidOperationException("A fighter info request requires an authenticated player.");
        var target = online.FindByUserNum(request.TargetUserNum);
        var fighter = target is not null && target.TryFeature<IPlayerSession>(out var player) ? player.Identity : null;
        return replies.SendAsync(session, new FighterInfoReplied(fighter), ct);
    }
}
