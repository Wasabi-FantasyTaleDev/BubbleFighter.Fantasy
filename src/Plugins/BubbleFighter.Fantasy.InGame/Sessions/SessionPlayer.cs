using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.InGame.Sessions;

/// <summary>
/// Reads the player state the authenticating row registers as <see cref="IPlayerSession"/>.
/// Missing identity is a hard error on create and join.
/// </summary>
internal static class SessionPlayer
{
    /// <summary>Whether the login burst has finished, so plaza and room requests are acceptable.</summary>
    internal static bool IsPastLoginBurst(ISession session) =>
        session.TryFeature<IPlayerSession>(out var player) && player.Phase != SessionPhase.Authenticated;

    internal static (int UserNum, string Nickname) RequireIdentity(ISession session)
    {
        if (!session.TryFeature<IPlayerSession>(out var player))
            throw new InvalidOperationException($"Session {session.Id} has no player; room create/join requires a finished login.");
        var identity = player.Identity;
        if (string.IsNullOrWhiteSpace(identity.Nickname))
            throw new InvalidOperationException($"Session {session.Id} player {identity.UserNum} has an empty nickname.");
        return (identity.UserNum, identity.Nickname);
    }

    /// <summary>
    /// Resolves the login user_num, or 0 when the session has no player. Per-player best-effort
    /// writes that must not fail a packet flow (the weapon-preset slot persist behind an
    /// already-sent swap reply) skip on 0; flows that cannot proceed without an identity use
    /// <see cref="RequireIdentity"/>.
    /// </summary>
    internal static int ResolveUserNum(ISession session) =>
        session.TryFeature<IPlayerSession>(out var player) ? player.Identity.UserNum : 0;

    /// <summary>
    /// Resolves the player level, or 1 when there is no player or the level is not positive. The
    /// S2C 118 row's second field is this level and <c>sub_EE9550</c> builds the row icon from
    /// <c>level - 1</c>, so a non-positive value makes the member row invisible.
    /// </summary>
    internal static int ResolveLevel(ISession session) =>
        session.TryFeature<IPlayerSession>(out var player) && player.Identity.Level > 0 ? player.Identity.Level : 1;

    internal static ushort ResolveColorIndex(ISession session) =>
        session.TryFeature<IPlayerSession>(out var player) ? player.Identity.ColorIndex : (ushort)0;
}
