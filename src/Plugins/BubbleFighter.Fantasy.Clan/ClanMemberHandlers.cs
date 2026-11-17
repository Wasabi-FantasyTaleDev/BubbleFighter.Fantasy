using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// Changes to a clan's rows by its master or deputy (approve, turn away, promote, demote, remove) and a member's
/// own leave. The stored rows decide every rule: the role the client lists for a row is never trusted. The actor
/// gets the change result; every seated member connected here, and the player the change is about, gets the
/// notice, read from the member list as it stood before a removal so the removed player hears it too.
/// </summary>
internal sealed class ClanMemberHandlers(
    IClanDirectory clans,
    ClanHub hub,
    IMessageSender<ClanMemberChangeAnswered> results,
    IMessageSender<ClanMemberChanged> changed,
    IMessageSender<ClanMemberLeft> left,
    ILogger logger)
{
    /// <summary>Applies one change to another row of the actor's clan.</summary>
    public async ValueTask ChangeAsync(ISession session, ClanMemberChangeRequested request, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        var answer = await ApplyAsync(caller, request, ct).ConfigureAwait(false);
        logger.LogInformation("Clan connection {Id} user={User} changed user {Target} to {Role}: result {Result}.",
            session.Id, caller.UserNum, request.UserNum, request.NewRole, answer.Result);
        await results.SendAsync(session, answer, ct).ConfigureAwait(false);
    }

    /// <summary>Removes the sender's seat; the master cannot leave and is sent nothing (the client offers no leave to a master).</summary>
    public async ValueTask LeaveAsync(ISession session, ClanLeaveRequested _, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        var row = await clans.FindMemberAsync(caller.UserNum, ct).ConfigureAwait(false);
        if (row is null || !ClanRole.IsMember(row.Role) || row.Role == ClanRole.Master)
        {
            logger.LogInformation("Clan connection {Id} user={User} asked to leave with role {Role}; nothing changed.",
                session.Id, caller.UserNum, row?.Role ?? 0);
            return;
        }
        var clan = await clans.FindAsync(row.ClanId, ct).ConfigureAwait(false);
        var before = await clans.ListMembersAsync(row.ClanId, ct).ConfigureAwait(false);
        if (clan is null || !await clans.RemoveMemberAsync(row.ClanId, caller.UserNum, ct).ConfigureAwait(false))
            return;
        logger.LogInformation("User {User} left clan {Clan} '{Name}'.", caller.UserNum, clan.ClanId, clan.Name);
        await hub.BroadcastAsync(before, left, new ClanMemberLeft(clan.Name, caller.UserNum, row.Nickname, 0, ClanMemberEvent.Left),
            seatedOnly: true, ct).ConfigureAwait(false);
    }

    private async ValueTask<ClanMemberChangeAnswered> ApplyAsync(ClanCaller caller, ClanMemberChangeRequested request, CancellationToken ct)
    {
        var failed = new ClanMemberChangeAnswered(ClanMemberChangeAnswered.Failed, request.Nickname, request.NewRole, 0);
        var actor = await clans.FindMemberAsync(caller.UserNum, ct).ConfigureAwait(false);
        if (actor is not { Role: ClanRole.Master or ClanRole.Deputy } || request.UserNum == caller.UserNum)
            return failed;
        var clan = await clans.FindAsync(actor.ClanId, ct).ConfigureAwait(false);
        if (clan is null)
            return failed;
        var target = await clans.FindMemberAsync(request.UserNum, ct).ConfigureAwait(false);
        if (target is null || target.ClanId != clan.ClanId)
        {
            // An applicant who withdrew while the list was open: the client has a notice for exactly this.
            return request.NewRole is ClanRole.Member or ClanMemberEvent.Rejected
                ? new ClanMemberChangeAnswered(ClanMemberChangeAnswered.Changed, request.Nickname, ClanMemberEvent.Withdrawn, 0)
                : failed;
        }
        if (target.Role == ClanRole.Applicant)
            return await AnswerApplicantAsync(clan, target, request.NewRole, ct).ConfigureAwait(false) ?? failed;
        if (target.Role == ClanRole.Master)
            return failed;
        var members = await clans.ListMembersAsync(clan.ClanId, ct).ConfigureAwait(false);
        switch (request.NewRole)
        {
            case ClanRole.Deputy when actor.Role == ClanRole.Master && target.Role == ClanRole.Member:
                // The profile has one deputy slot (kr-1256 sub_5268A0), and the client has a refusal for a second.
                if (members.Any(row => row.Role == ClanRole.Deputy))
                    return new ClanMemberChangeAnswered(ClanMemberChangeAnswered.DeputyTaken, target.Nickname, ClanRole.Deputy, 0);
                return await SetRoleAsync(clan, target, ClanRole.Deputy, ct).ConfigureAwait(false) ?? failed;
            case ClanRole.Member when actor.Role == ClanRole.Master && target.Role == ClanRole.Deputy:
                return await SetRoleAsync(clan, target, ClanRole.Member, ct).ConfigureAwait(false) ?? failed;
            case ClanMemberEvent.Removed when actor.Role == ClanRole.Master || target.Role == ClanRole.Member:
                if (!await clans.RemoveMemberAsync(clan.ClanId, target.UserNum, ct).ConfigureAwait(false))
                    return failed;
                await hub.BroadcastAsync(members, changed, new ClanMemberChanged(clan.Name, target.UserNum, target.Nickname, ClanMemberEvent.Removed, 0),
                    seatedOnly: true, ct).ConfigureAwait(false);
                return new ClanMemberChangeAnswered(ClanMemberChangeAnswered.Changed, target.Nickname, ClanMemberEvent.Removed, 0);
            default:
                return failed;
        }
    }

    // Seats or turns away an applicant; null when the word is neither.
    private async ValueTask<ClanMemberChangeAnswered?> AnswerApplicantAsync(ClanRecord clan, ClanMemberRecord applicant, int newRole, CancellationToken ct)
    {
        if (newRole == ClanRole.Member)
        {
            if (!await clans.SeatAsync(clan.ClanId, applicant.UserNum, ClanRole.Member, ct).ConfigureAwait(false))
                return new ClanMemberChangeAnswered(ClanMemberChangeAnswered.Full, applicant.Nickname, ClanRole.Member, 0);
            var members = await clans.ListMembersAsync(clan.ClanId, ct).ConfigureAwait(false);
            await hub.BroadcastAsync(members, changed, new ClanMemberChanged(clan.Name, applicant.UserNum, applicant.Nickname, ClanRole.Member, 1),
                seatedOnly: true, ct).ConfigureAwait(false);
            return new ClanMemberChangeAnswered(ClanMemberChangeAnswered.Changed, applicant.Nickname, ClanRole.Member, 1);
        }
        if (newRole != ClanMemberEvent.Rejected)
            return null;
        if (!await clans.RemoveMemberAsync(clan.ClanId, applicant.UserNum, ct).ConfigureAwait(false))
            return new ClanMemberChangeAnswered(ClanMemberChangeAnswered.Changed, applicant.Nickname, ClanMemberEvent.Withdrawn, 0);
        // The applicant holds no seat, so it is told directly rather than through the seated members' notice.
        await hub.SendToAsync(applicant.UserNum, changed,
            new ClanMemberChanged(clan.Name, applicant.UserNum, applicant.Nickname, ClanMemberEvent.Rejected, 0), ct).ConfigureAwait(false);
        return new ClanMemberChangeAnswered(ClanMemberChangeAnswered.Changed, applicant.Nickname, ClanMemberEvent.Rejected, 0);
    }

    // Moves a seated row between member and deputy and tells the clan; null when the row was gone.
    private async ValueTask<ClanMemberChangeAnswered?> SetRoleAsync(ClanRecord clan, ClanMemberRecord target, int role, CancellationToken ct)
    {
        if (!await clans.SetRoleAsync(clan.ClanId, target.UserNum, role, ct).ConfigureAwait(false))
            return null;
        var members = await clans.ListMembersAsync(clan.ClanId, ct).ConfigureAwait(false);
        await hub.BroadcastAsync(members, changed, new ClanMemberChanged(clan.Name, target.UserNum, target.Nickname, role, 0),
            seatedOnly: true, ct).ConfigureAwait(false);
        return new ClanMemberChangeAnswered(ClanMemberChangeAnswered.Changed, target.Nickname, role, 0);
    }
}
