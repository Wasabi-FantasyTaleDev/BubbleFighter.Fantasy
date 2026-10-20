using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>
/// Tracks which protocol sessions are match connections. A match connection is
/// a second TCP session a seated client opens toward the game listener: its
/// first frame is the relay authentication, and a following C2S 3 carries the
/// reconnect-shaped login body whose first wstring names the round's map key
/// (the builder copies it from session+232, the 117 MapMeta slot, at
/// sub_9E7080). Frames of that shape must never run the lobby login
/// chain - the login parser's rejection of this exact body was the live
/// start-of-round disconnect, and a lobby reply would flood the match
/// connection with frames its client never reads in-round.
/// </summary>
public interface IMatchConnectionTracker
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="session"/> has
    /// completed a relay authentication and is therefore a match connection.
    /// </summary>
    /// <param name="session">The session to classify.</param>
    /// <returns><see langword="true"/> when the session is match-bound.</returns>
    bool IsMatchBound(ISession session);
}
