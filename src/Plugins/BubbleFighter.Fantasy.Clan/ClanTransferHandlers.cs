using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// Handing a clan to a seated member, in the client's three steps: the master's offer reaches the connected member,
/// the member's acceptance hands the clan over in one directory step, and the old master's client then confirms,
/// which tells the clan. An offer lives on this node only, one per clan, and binds the two connections it was made
/// between: a newer offer replaces it, and an answer from another connection, or after the master's connection was
/// replaced, finds none. Roles never change on the offer alone.
/// </summary>
internal sealed class ClanTransferHandlers(IClanDirectory clans, ClanHub hub, ClanTransferHandlers.Senders senders, ILogger logger)
{
    private readonly Dictionary<long, Offer> _offers = [];
    private readonly Lock _gate = new();

    /// <summary>The five frames of a transfer.</summary>
    /// <param name="RequestResult">The offer's result to the master.</param>
    /// <param name="Offered">The offer to the member.</param>
    /// <param name="AnswerResult">The acceptance's result to the member.</param>
    /// <param name="Answered">The member's answer to the master.</param>
    /// <param name="MasterChanged">The new master, to the clan.</param>
    internal sealed record Senders(
        IMessageSender<ClanTransferRequestAnswered> RequestResult,
        IMessageSender<ClanTransferOffered> Offered,
        IMessageSender<ClanTransferAnswerAnswered> AnswerResult,
        IMessageSender<ClanTransferAnswered> Answered,
        IMessageSender<ClanMasterChanged> MasterChanged);

    private sealed record Offer(long ClanId, long From, long FromConnection, long To, string ToNickname, long ToConnection, bool Accepted);

    /// <summary>Offers the master's clan to a seated member whose clan connection is live here.</summary>
    public async ValueTask RequestAsync(ISession session, ClanTransferRequested request, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        var master = await clans.FindMemberAsync(caller.UserNum, ct).ConfigureAwait(false);
        var target = await clans.FindMemberAsync(request.UserNum, ct).ConfigureAwait(false);
        if (master is not { Role: ClanRole.Master } || target is null || target.ClanId != master.ClanId
            || target.Role is not (ClanRole.Member or ClanRole.Deputy) || !hub.TryGetLocal(target.UserNum, out var member))
        {
            logger.LogInformation("Clan connection {Id} user={User} could not offer the clan to user {Target}.", session.Id, caller.UserNum, request.UserNum);
            await senders.RequestResult.SendAsync(session, new ClanTransferRequestAnswered(0, 2), ct).ConfigureAwait(false);
            return;
        }
        lock (_gate)
            _offers[master.ClanId] = new Offer(master.ClanId, caller.UserNum, session.Id, target.UserNum, target.Nickname, member.Id, false);
        await senders.RequestResult.SendAsync(session, new ClanTransferRequestAnswered(1, 1), ct).ConfigureAwait(false);
        await senders.Offered.SendAsync(member, new ClanTransferOffered(master.Nickname, checked((int)caller.UserNum)), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The member's answer. A decline drops the offer and tells the master; an acceptance hands the clan over and
    /// tells both sides, the master's client then confirming with <see cref="ConfirmAsync"/>.
    /// </summary>
    public async ValueTask AnswerAsync(ISession session, ClanTransferAnswerRequested request, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        Offer? offer;
        lock (_gate)
        {
            offer = _offers.Values.FirstOrDefault(o => !o.Accepted && o.To == caller.UserNum && o.From == request.Unknown1 && o.ToConnection == session.Id);
            if (offer is not null && !request.Accepted)
                _offers.Remove(offer.ClanId);
        }
        var masterLive = offer is not null && hub.TryGetLocal(offer.From, out var masterSession) && masterSession.Id == offer.FromConnection;
        hub.TryGetLocal(offer?.From ?? 0, out var master);
        if (offer is null || !request.Accepted)
        {
            if (offer is not null && masterLive)
                await senders.Answered.SendAsync(master, new ClanTransferAnswered(false, checked((int)offer.To), offer.ToNickname), ct).ConfigureAwait(false);
            if (offer is null && request.Accepted)
                await senders.AnswerResult.SendAsync(session, new ClanTransferAnswerAnswered(0), ct).ConfigureAwait(false);
            return;
        }
        var handed = masterLive && await clans.TransferMasterAsync(offer.ClanId, offer.From, offer.To, ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (handed)
                _offers[offer.ClanId] = offer with { Accepted = true };
            else
                _offers.Remove(offer.ClanId);
        }
        logger.LogInformation("Clan {Clan}: user {To} accepted the clan from user {From}: {Result}.", offer.ClanId, offer.To, offer.From,
            handed ? "handed over" : "refused");
        await senders.AnswerResult.SendAsync(session, new ClanTransferAnswerAnswered(handed ? 1 : 0), ct).ConfigureAwait(false);
        if (handed)
            await senders.Answered.SendAsync(master, new ClanTransferAnswered(true, checked((int)offer.To), offer.ToNickname), ct).ConfigureAwait(false);
    }

    /// <summary>The old master's confirmation of a handed-over clan: tells every seated member who the master is now.</summary>
    public async ValueTask ConfirmAsync(ISession session, ClanTransferConfirmed request, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        Offer? offer;
        lock (_gate)
        {
            offer = _offers.Values.FirstOrDefault(o => o.Accepted && o.From == caller.UserNum && o.To == request.UserNum);
            if (offer is not null)
                _offers.Remove(offer.ClanId);
        }
        if (offer is null)
        {
            logger.LogInformation("Clan connection {Id} user={User} confirmed a transfer to user {Target} that was not handed over.",
                session.Id, caller.UserNum, request.UserNum);
            return;
        }
        var members = await clans.ListMembersAsync(offer.ClanId, ct).ConfigureAwait(false);
        var oldName = members.FirstOrDefault(row => row.UserNum == offer.From)?.Nickname ?? caller.Nickname;
        var newName = members.FirstOrDefault(row => row.UserNum == offer.To)?.Nickname ?? offer.ToNickname;
        await hub.BroadcastAsync(members, senders.MasterChanged, new ClanMasterChanged(0, offer.From, oldName, offer.To, newName), seatedOnly: true, ct)
            .ConfigureAwait(false);
    }
}
