using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// One build row's view of the node's clan connections (<see cref="IClanConnections"/>). The directory is the truth
/// about who belongs to which clan; the hub only answers "which of these players can be told right now", so a notice
/// to a clan is the directory's member list filtered through here. A member on this build is sent to with this
/// row's sender; a member playing another build is sent to through that build's delivery, which writes the same
/// typed message in its own layout or skips it when that client has no such message. A member who is not connected
/// here is handed to the clan relay when the node has one bound (the node holding that member's connection tells
/// it); without a relay, what that player sees is whatever the next request reads from the directory.
/// </summary>
internal sealed class ClanHub(IClanConnections connections, IClanDelivery own)
{
    /// <summary>Records a clan connection of this build that logged in.</summary>
    public void Attach(long userNum, ISession session) => connections.Attach(userNum, session, own);

    /// <summary>Keeps the last report of a player whose clan connection is attached; a report from any other connection is ignored.</summary>
    public void Report(long userNum, ISession session, ClanMemberReported report) => connections.Report(userNum, session, report);

    /// <summary>Returns the last report of a player with a live clan connection on this node, or null.</summary>
    public ClanMemberReported? ReportOf(long userNum) => connections.ReportOf(userNum);

    /// <summary>
    /// Returns the player's live clan connection when it is attached through this build. Used where a conversation
    /// spans several requests whose state this row keeps (the master transfer), which therefore stays within one build.
    /// </summary>
    public bool TryGetLocal(long userNum, out ISession session)
    {
        if (connections.TryGet(userNum, out session, out var delivery) && ReferenceEquals(delivery, own))
            return true;
        session = null!;
        return false;
    }

    /// <summary>
    /// Sends one message to one player's live clan connection on this node, whichever build it plays, or hands it to
    /// the clan relay when the player is not connected here. Returns false when the player is neither here nor
    /// reachable through a relay, or when the player's build has no such message. A relayed message counts as sent:
    /// whether the player is connected to another node is known only there.
    /// </summary>
    public async ValueTask<bool> SendToAsync<T>(long userNum, IMessageSender<T> sender, T message, CancellationToken ct) where T : IMessage
    {
        ArgumentNullException.ThrowIfNull(sender);
        if (!connections.TryGet(userNum, out var session, out var delivery))
            return connections.Forward([userNum], message);
        if (ReferenceEquals(delivery, own))
        {
            await sender.SendAsync(session, message, ct).ConfigureAwait(false);
            return true;
        }
        return await delivery.SendAsync(session, message, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one message to every listed member who is connected to this node, optionally only to seated members.
    /// A member whose send fails is skipped: one broken socket must not keep the others from hearing the notice.
    /// Members not connected here are handed to the clan relay in one notice, when the node has one.
    /// </summary>
    public async ValueTask BroadcastAsync<T>(IReadOnlyList<ClanMemberRecord> members, IMessageSender<T> sender, T message,
        bool seatedOnly, CancellationToken ct) where T : IMessage
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(sender);
        List<long>? elsewhere = null;
        foreach (var member in members)
        {
            if (seatedOnly && !ClanRole.IsMember(member.Role))
                continue;
            if (!connections.TryGet(member.UserNum, out _, out _))
            {
                (elsewhere ??= []).Add(member.UserNum);
                continue;
            }
            try
            {
                await SendToAsync(member.UserNum, sender, message, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The connection is going away; its close event detaches it.
            }
        }
        if (elsewhere is not null)
            connections.Forward(elsewhere, message);
    }
}

/// <summary>
/// The <see cref="IClanDelivery"/> of one build's clan row: it writes a typed message with the sender this build's
/// dialect registered for the row's clan protocol, and declines a message the build's tables do not carry.
/// </summary>
internal sealed class ClanDelivery(IPluginContext context, string protocol) : IClanDelivery
{
    /// <inheritdoc />
    public string Protocol { get; } = protocol;

    /// <inheritdoc />
    public async ValueTask<bool> SendAsync<T>(ISession session, T message, CancellationToken ct) where T : IMessage
    {
        ArgumentNullException.ThrowIfNull(session);
        var sender = context.Services.GetAll<IMessageSender<T>>().FirstOrDefault(candidate => candidate.Protocol == Protocol);
        if (sender is null)
            return false;
        await sender.SendAsync(session, message, ct).ConfigureAwait(false);
        return true;
    }
}
