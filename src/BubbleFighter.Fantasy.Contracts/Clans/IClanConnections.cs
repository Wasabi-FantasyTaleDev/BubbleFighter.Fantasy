using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Clans;

/// <summary>
/// The logged-in clan-server connections of this node, across every client build. A clan is one directory for all
/// builds, so a notice, a chat line or a whisper to a member is delivered through that member's own build: each entry
/// carries the <see cref="IClanDelivery"/> of the build row that logged the connection in. Members on another node are
/// not here: a notice for them goes to the clan relay when one is bound (<see cref="IClanRelayEndpoint"/>), and
/// otherwise they read the shared directory when their clan window next loads.
/// </summary>
public interface IClanConnections
{
    /// <summary>Records a clan connection that logged in. A newer connection of the same player replaces the older.</summary>
    /// <param name="userNum">The player's user number.</param>
    /// <param name="session">The logged-in clan-connection session.</param>
    /// <param name="delivery">The delivery of the build row that serves this connection.</param>
    void Attach(long userNum, ISession session, IClanDelivery delivery);

    /// <summary>Returns the player's live clan connection on this node and the delivery of its build, when there is one.</summary>
    /// <param name="userNum">The player's user number.</param>
    /// <param name="session">The live session, when found.</param>
    /// <param name="delivery">The delivery of the session's build, when found.</param>
    /// <returns>Whether the player has a live clan connection here.</returns>
    bool TryGet(long userNum, out ISession session, out IClanDelivery delivery);

    /// <summary>Keeps the last member report of a player whose attached connection sent it; a report from any other connection is ignored.</summary>
    /// <param name="userNum">The reporting player.</param>
    /// <param name="session">The connection the report arrived on.</param>
    /// <param name="report">The report.</param>
    void Report(long userNum, ISession session, ClanMemberReported report);

    /// <summary>Returns the last report of a player with a live clan connection here, or null.</summary>
    /// <param name="userNum">The player's user number.</param>
    /// <returns>The report, or null.</returns>
    ClanMemberReported? ReportOf(long userNum);

    /// <summary>
    /// Hands a notice for members who are not connected to this node to the clan relay. Returns false without doing
    /// anything when no relay is bound or the message is not one the relay carries.
    /// </summary>
    /// <typeparam name="T">The typed message.</typeparam>
    /// <param name="userNums">The members to tell elsewhere.</param>
    /// <param name="message">The message.</param>
    /// <returns>Whether the notice was handed to the relay.</returns>
    bool Forward<T>(IReadOnlyList<long> userNums, T message) where T : IMessage;

    /// <summary>Forgets every connection attached through <paramref name="delivery"/>, when its build row unloads.</summary>
    /// <param name="delivery">The retiring row's delivery.</param>
    void Release(IClanDelivery delivery);
}

/// <summary>Sends typed clan messages on the clan-server connections of one client build.</summary>
public interface IClanDelivery
{
    /// <summary>Gets the build's clan-connection protocol, for example <c>kr-812/clan</c>.</summary>
    string Protocol { get; }

    /// <summary>
    /// Sends a message on a connection of this build. Returns false without sending when the build's tables do not
    /// carry the message (a build without that clan feature), so a notice reaches every member whose client has it.
    /// </summary>
    /// <typeparam name="T">The typed message.</typeparam>
    /// <param name="session">A clan-connection session of this build.</param>
    /// <param name="message">The message.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether the build carries the message and it was sent.</returns>
    ValueTask<bool> SendAsync<T>(ISession session, T message, CancellationToken ct) where T : IMessage;
}
