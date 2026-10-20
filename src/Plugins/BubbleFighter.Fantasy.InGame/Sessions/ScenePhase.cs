using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>
/// Moves a player's <see cref="IPlayerSession"/> phase at the moment this row sends the packet that
/// changes the client's scene: an S2C 117 kind-3 plaza snapshot, an S2C 117 kind-2 room install,
/// or the S2C 510 match stage. The client applies the scene on receipt (<c>sub_9AC490</c> for 117),
/// so the send is where the server-side phase must follow.
/// </summary>
internal static class ScenePhase
{
    /// <summary>
    /// Records that <paramref name="session"/> was just sent into <paramref name="phase"/>. A move
    /// the transition table refuses is logged as a warning instead of thrown: it happens inside a
    /// broadcast to several players, and failing one recipient would leave the rest half-updated.
    /// The warning is the evidence that a scene packet reached a player in an unexpected phase.
    /// </summary>
    internal static void Entered(ISession session, SessionPhase phase, ILogger logger)
    {
        if (!session.TryFeature<IPlayerSession>(out var player))
            return;
        var from = player.Phase;
        try
        {
            player.MoveTo(phase);
        }
        catch (InvalidOperationException)
        {
            logger.LogWarning(
                "Session {Id} player {UserNum} was sent a {Phase} scene while in {From}; phase left unchanged.",
                session.Id, player.Identity.UserNum, phase, from);
        }
    }
}
