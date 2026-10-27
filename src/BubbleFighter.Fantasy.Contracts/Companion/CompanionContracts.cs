using System.Text.Json;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Companion;

/// <summary>
/// One companion connection grant: where the launcher connects, the one-time ticket it presents in the upgrade's
/// <c>Authorization</c> header, and the launch it belongs to. The launch id is not secret; the ticket is.
/// </summary>
/// <param name="Url">WebSocket URL of the companion listener, <c>ws</c> or <c>wss</c>, with <c>{host}</c> standing for the host the launcher used.</param>
/// <param name="Ticket">One-time connection ticket.</param>
/// <param name="LaunchId">Launch identity echoed in the companion hello and carried by the game ticket.</param>
/// <param name="ExpiresAt">When the ticket stops being accepted.</param>
public sealed record CompanionGrant(string Url, string Ticket, string LaunchId, DateTimeOffset ExpiresAt);

/// <summary>
/// Opens companion launches for launcher accounts and renews their connection tickets. A launch is bound to a game
/// session when the game login redeems the launch's game ticket.
/// </summary>
public interface ICompanionTicketIssuer
{
    /// <summary>Opens a launch for <paramref name="account"/> playing <paramref name="build"/> and grants its first connection.</summary>
    /// <exception cref="InvalidOperationException">The bounded launch table is full.</exception>
    CompanionGrant Open(string account, string build);

    /// <summary>Grants a fresh connection ticket for a live launch owned by <paramref name="account"/>, or null when there is none.</summary>
    CompanionGrant? Renew(string account, string launchId);
}

/// <summary>The launcher account and game session a companion message came from.</summary>
/// <param name="Account">Launcher account that opened the launch.</param>
/// <param name="Build">Client build of the launch.</param>
/// <param name="LaunchId">The launch's identity.</param>
/// <param name="Session">The game session the launch is bound to.</param>
/// <param name="Locale">The launcher's display language: zh-CN, zh-TW, en, ko, id or vi.</param>
public sealed record CompanionCaller(string Account, string Build, string LaunchId, ISession Session, string Locale);

/// <summary>Handles one client-to-server companion event or request; a request's reply is the returned payload.</summary>
/// <param name="caller">Who sent it.</param>
/// <param name="payload">The message payload, owned by the caller for the duration of the call.</param>
/// <param name="ct">Cancelled when the connection ends.</param>
public delegate ValueTask<object?> CompanionHandler(CompanionCaller caller, JsonElement payload, CancellationToken ct);

/// <summary>
/// The server side of the companion channel for server plugins. Message types and schema versions a plugin routes
/// or offers become server capabilities; a launcher plugin that requires them is offered only where they exist.
/// </summary>
public interface ICompanionHub
{
    /// <summary>Handles client-to-server messages of one type and schema; dispose to stop.</summary>
    IDisposable Route(string type, int version, CompanionHandler handler);

    /// <summary>Declares a server-to-client message type and schema this server sends; dispose to withdraw it.</summary>
    IDisposable Offer(string type, int version);

    /// <summary>True when <paramref name="session"/> has a bound companion connection that negotiated the type and schema.</summary>
    bool Supports(ISession session, string type, int version);

    /// <summary>Queues a server-to-client event; false when the session has no bound connection that negotiated it or its queue is full.</summary>
    bool Send(ISession session, string type, int version, object? payload);

    /// <summary>Returns a detached snapshot of the game sessions whose launch has a bound companion connection.</summary>
    IReadOnlyList<ISession> BoundSessions();
}
