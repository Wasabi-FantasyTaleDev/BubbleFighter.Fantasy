using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// Clan chat and whispers between members, on this node or through the clan relay. Nothing is stored. The sender's name in every
/// outgoing frame is the server's own, never the string the client sent.
/// </summary>
internal sealed class ClanChatHandlers(
    IClanDirectory clans,
    ClanHub hub,
    IMessageSender<ClanChatReceived> chat,
    IMessageSender<ClanWhisperAnswered> results,
    IMessageSender<ClanWhisperReceived> whispers,
    ILogger logger)
{
    /// <summary>Tells a seated member's line to every seated member connected here, the sender included.</summary>
    public async ValueTask ChatAsync(ISession session, ClanChatRequested request, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        var row = await clans.FindMemberAsync(caller.UserNum, ct).ConfigureAwait(false);
        if (row is null || !ClanRole.IsMember(row.Role) || request.Text.Length == 0)
        {
            logger.LogDebug("Clan connection {Id} user={User} sent a clan chat line without a seat or text.", session.Id, caller.UserNum);
            return;
        }
        await hub.BroadcastAsync(await clans.ListMembersAsync(row.ClanId, ct).ConfigureAwait(false), chat,
            new ClanChatReceived(caller.Nickname, request.Text), seatedOnly: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Delivers a whisper to a seated member of the sender's clan whose clan connection is live on this node, on any
    /// build, or through the clan relay to a member elsewhere; a relayed whisper is answered as delivered.
    /// </summary>
    public async ValueTask WhisperAsync(ISession session, ClanWhisperRequested request, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        var row = await clans.FindMemberAsync(caller.UserNum, ct).ConfigureAwait(false);
        var members = row is not null && ClanRole.IsMember(row.Role) ? await clans.ListMembersAsync(row.ClanId, ct).ConfigureAwait(false) : [];
        var target = members.FirstOrDefault(member => ClanRole.IsMember(member.Role) && member.UserNum != caller.UserNum
            && string.Equals(member.Nickname, request.Target, StringComparison.OrdinalIgnoreCase));
        if (target is null || !await hub.SendToAsync(target.UserNum, whispers,
                new ClanWhisperReceived(request.Kind, caller.Nickname, target.Nickname, request.Text, request.Unknown3), ct).ConfigureAwait(false))
        {
            await results.SendAsync(session, new ClanWhisperAnswered(ClanWhisperAnswered.NotFound, request.Kind, caller.Nickname, request.Target, request.Text), ct)
                .ConfigureAwait(false);
            return;
        }
        await results.SendAsync(session, new ClanWhisperAnswered(ClanWhisperAnswered.Delivered, request.Kind, caller.Nickname, target.Nickname, request.Text), ct)
            .ConfigureAwait(false);
    }
}
