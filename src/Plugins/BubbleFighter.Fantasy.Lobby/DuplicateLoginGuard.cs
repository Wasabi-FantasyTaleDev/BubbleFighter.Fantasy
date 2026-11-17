using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Lobby;

/// <summary>
/// Applies the row's <c>duplicate_login</c> policy when an account logs in while another live
/// connection already holds it: <see cref="DuplicateLoginBehavior.KickOld"/> closes the older
/// connection and lets the new login take the account, <see cref="DuplicateLoginBehavior.RejectNew"/>
/// leaves the older connection untouched and refuses the new login. One guard per activation,
/// sharing the row's <see cref="PlayerSessionScope"/>. With an <see cref="IAccountBuildLock"/> the same policy
/// covers the account's sessions on the server's other client builds.
/// </summary>
/// <param name="sessions">The row's player registrations.</param>
/// <param name="behavior">The configured policy.</param>
/// <param name="logger">The row's logger; both duplicate outcomes are logged as warnings.</param>
/// <param name="builds">The server-wide account lock, when the server mounts one.</param>
internal sealed class DuplicateLoginGuard(PlayerSessionScope sessions, DuplicateLoginBehavior behavior, ILogger logger,
    IAccountBuildLock? builds = null)
{
    /// <summary>Claims the account for a session and settles any previous holder per policy.</summary>
    /// <param name="session">The session the login arrived on.</param>
    /// <param name="identity">The authenticated identity.</param>
    /// <returns>The claim; <see cref="LoginClaimStatus.RefusedDuplicate"/> means the caller must
    /// refuse this login and leave the previous connection running.</returns>
    public async ValueTask<LoginClaim> ClaimAsync(ISession session, PlayerIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(session);
        var takeOver = behavior == DuplicateLoginBehavior.KickOld;
        IReadOnlyList<ISession> elsewhere = [];
        var claim = builds is null ? sessions.Claim(session, identity, takeOver)
            : builds.Claim(identity.UserNum, session.Profile.Id, live =>
            {
                elsewhere = live;
                return live.Count > 0 && !takeOver ? new LoginClaim(LoginClaimStatus.RefusedDuplicate, null, live[0])
                    : sessions.Claim(session, identity, takeOver);
            });
        if (claim.Status == LoginClaimStatus.RefusedDuplicate && claim.Replaced is { } other && other.Profile.Id != session.Profile.Id)
        {
            logger.LogWarning(
                "Session {Id} login refused: account {UserNum} is already live on build {Build} connection {Previous} (duplicate_login reject_new).",
                session.Id, identity.UserNum, other.Profile.Id, other.Connection.Id);
            return claim;
        }
        if (claim.Status == LoginClaimStatus.RefusedDuplicate)
        {
            logger.LogWarning(
                "Session {Id} login refused: account {UserNum} is already live on connection {Previous} (duplicate_login reject_new).",
                session.Id, identity.UserNum, claim.Replaced!.Connection.Id);
            return claim;
        }

        if (claim.Replaced is { } previous)
        {
            logger.LogWarning(
                "Session {Id} took over account {UserNum} from connection {Previous}; closing the older connection (duplicate_login kick_old).",
                session.Id, identity.UserNum, previous.Connection.Id);
            // The claim already released the older registration, so the account is never unowned.
            // Transports close without throwing (TcpConnection swallows socket errors); a failure
            // here propagates so a login that could not settle the older connection fails loudly.
            await previous.Connection.CloseAsync(
                $"duplicate login: account taken over by session {session.Id}").ConfigureAwait(false);
        }

        // The account leaves the other builds it was live on: their own close handling releases it there.
        if (claim.Status == LoginClaimStatus.Attached)
            foreach (var live in elsewhere)
            {
                logger.LogWarning(
                    "Session {Id} took over account {UserNum} from build {Build} connection {Previous}; closing it (duplicate_login kick_old).",
                    session.Id, identity.UserNum, live.Profile.Id, live.Connection.Id);
                await live.Connection.CloseAsync(
                    $"duplicate login: account taken over on build {session.Profile.Id} by session {session.Id}").ConfigureAwait(false);
            }

        return claim;
    }
}
