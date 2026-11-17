using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// The clan-server connection's handshake. The client opens the connection at startup, long before it has an
/// account, and can only log it in after its game connection has: the hello makes it ask the game connection
/// for a passport (kr-1256 sub_525170 case 0x3EA) and it forwards whatever comes back as its login
/// (kr-1256 sub_525DF0). So the hello is repeated on every heartbeat until a login is accepted, and after that
/// the heartbeat is where the game session behind the connection is checked.
/// </summary>
internal sealed class ClanConnectionHandlers(
    ClanPassports passports,
    ClanHub hub,
    IMessageSender<ClanServerHello> hello,
    IMessageSender<ClanServerLoginAnswered> answer,
    ILogger logger)
{
    /// <summary>Greets a connection that has not logged in; ends a logged-in one whose game session is gone.</summary>
    public async ValueTask HeartbeatAsync(ISession session, ClanServerHeartbeatReceived _, CancellationToken ct)
    {
        if (!session.TryFeature<ClanConnectionIdentity>(out var identity))
        {
            await hello.SendAsync(session, new ClanServerHello(), ct).ConfigureAwait(false);
            return;
        }
        if (identity.Game.Connection.IsClosed)
        {
            logger.LogInformation("Clan connection {Id} of user={User} closed: its game session ended.", session.Id, identity.UserNumber);
            await session.Connection.CloseAsync("game session ended").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Checks the echoed passport and binds the connection to its game session. A refused login is answered with
    /// the client's own notice code and the connection is closed: the client reconnects by itself 15000 ms after
    /// its socket closes (kr-1256 sub_525050) and is greeted afresh, where staying connected would repeat the
    /// hello, and so the notice, every heartbeat.
    /// </summary>
    public async ValueTask LoginAsync(ISession session, ClanServerLoginRequested login, CancellationToken ct)
    {
        if (session.TryFeature<ClanConnectionIdentity>(out var bound))
        {
            // A second login on a bound connection is the client answering a hello that crossed its first login.
            var same = bound.UserNumber == login.UserNumber;
            await answer.SendAsync(session, new ClanServerLoginAnswered(same ? ClanServerLoginResult.Accepted : ClanServerLoginResult.Failed), ct).ConfigureAwait(false);
            if (!same)
                await session.Connection.CloseAsync("clan login for another account").ConfigureAwait(false);
            return;
        }

        var result = passports.Redeem(login.UserNumber, login.Ticket.Span, out var game);
        if (result != ClanServerLoginResult.Accepted || game is null)
        {
            logger.LogWarning("Clan connection {Id} login for user={User} refused: {Result}.", session.Id, login.UserNumber, result);
            await answer.SendAsync(session, new ClanServerLoginAnswered(result), ct).ConfigureAwait(false);
            await session.Connection.CloseAsync($"clan login refused: {result}").ConfigureAwait(false);
            return;
        }

        // The feature lives as long as the session: a clan connection logs in once and is replaced, not re-bound.
        _ = session.RegisterFeature(new ClanConnectionIdentity(login.UserNumber, login.Nickname, game));
        hub.Attach(login.UserNumber, session);
        logger.LogInformation("Clan connection {Id} logged in as user={User} (game session {Game}).", session.Id, login.UserNumber, game.Id);
        await answer.SendAsync(session, new ClanServerLoginAnswered(ClanServerLoginResult.Accepted), ct).ConfigureAwait(false);
    }
}
