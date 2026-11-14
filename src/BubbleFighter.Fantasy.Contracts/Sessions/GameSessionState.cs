using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Sessions;

/// <summary>A read-only, conservative snapshot of one authenticated player's current gameplay scene.</summary>
/// <param name="State">The canonical scene name; unknown means that no current owner could establish it.</param>
/// <param name="InMatch">Whether the player belongs to an ordinary round that is currently playing.</param>
/// <param name="CanLeaveMatch">Whether ordinary early departure is currently available; execution must check again.</param>
/// <param name="IsTutorial">Whether the room is an admitted tutorial, independent of its mode or player count.</param>
/// <param name="ModeId">The current room's native mode identifier, or null outside a proven room.</param>
public sealed record GameSessionState(string State, bool InMatch, bool CanLeaveMatch, bool IsTutorial, int? ModeId)
{
    /// <summary>Gets the conservative result for an absent, retired or inconsistent session.</summary>
    public static GameSessionState Unavailable { get; } = new("unknown", false, false, false, null);
}

/// <summary>Reads an exact session's room state without inspecting the client or changing game state.</summary>
public interface IGameSessionStateReader
{
    /// <summary>Returns a snapshot when this owner holds the exact room session, otherwise null.</summary>
    GameSessionState? Describe(ISession session);
}

/// <summary>
/// Raised after something that <see cref="IGameSessionStateReader"/> or the session's phase reports may have changed:
/// a scene move, joining or leaving a room, a room's phase, countdown or mode. It carries no state, so a listener reads
/// the current snapshot when it handles the change and a late or repeated signal can never report a stale scene. It is
/// raised while the owner may hold its own locks, so a handler only schedules work.
/// </summary>
/// <param name="Session">The game session whose scene may have changed.</param>
public sealed record GameSessionStateChanged(ISession Session) : IEvent;
