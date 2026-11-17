using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// Applying to a clan from the search list, withdrawing the application, and the applicant list of the clan's
/// master or deputy. Each request is answered with the one frame its reader expects; the client asks for its home
/// again afterwards, which states the row the request left.
/// </summary>
internal sealed class ClanApplicationHandlers(
    IClanDirectory clans,
    IMessageSender<ClanApplyAnswered> applied,
    IMessageSender<ClanCancelApplicationAnswered> cancelled,
    IMessageSender<ClanApplicantsSent> applicants,
    ILogger logger)
{
    /// <summary>
    /// The recruitment word of a clan that seats applicants at once: the search dialog sends a join request instead
    /// of an application for it (kr-1256 sub_57BA90), and the client's refusal -6 names it.
    /// </summary>
    internal const int OpenRecruitment = ClanRecruitment.Open;

    /// <summary>Stores an application to a recruiting clan.</summary>
    public async ValueTask ApplyAsync(ISession session, ClanApplyRequested request, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        var answer = await AdmitAsync(caller, request.ClanId, openOnly: false, ct).ConfigureAwait(false);
        await applied.SendAsync(session, answer, ct).ConfigureAwait(false);
    }

    /// <summary>Seats the sender at once in a clan whose recruitment word is <see cref="OpenRecruitment"/>.</summary>
    public async ValueTask JoinAsync(ISession session, ClanJoinRequested request, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        var answer = await AdmitAsync(caller, request.ClanId, openOnly: true, ct).ConfigureAwait(false);
        await applied.SendAsync(session, answer, ct).ConfigureAwait(false);
    }

    /// <summary>Withdraws the sender's application to the named clan; any other row is left as it is.</summary>
    public async ValueTask CancelAsync(ISession session, ClanCancelApplicationRequested request, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        var member = await clans.FindMemberAsync(caller.UserNum, ct).ConfigureAwait(false);
        var result = member is { Role: ClanRole.Applicant } && member.ClanId == request.ClanId
            && await clans.RemoveMemberAsync(member.ClanId, caller.UserNum, ct).ConfigureAwait(false)
            ? ClanCancelApplicationAnswered.Cancelled
            : ClanCancelApplicationAnswered.Failed;
        logger.LogInformation("Clan connection {Id} user={User} withdrew the application to clan {Clan}: result {Result}.",
            session.Id, caller.UserNum, request.ClanId, result);
        await cancelled.SendAsync(session, new ClanCancelApplicationAnswered(result, request.ClanId), ct).ConfigureAwait(false);
    }

    /// <summary>Lists the applicants of the sender's clan to its master or deputy; anyone else gets an empty list.</summary>
    public async ValueTask ApplicantsAsync(ISession session, ClanApplicantsRequested _, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        var member = await clans.FindMemberAsync(caller.UserNum, ct).ConfigureAwait(false);
        if (member is not { Role: ClanRole.Master or ClanRole.Deputy })
        {
            await applicants.SendAsync(session, new ClanApplicantsSent([]), ct).ConfigureAwait(false);
            return;
        }
        var rows = await clans.ListMembersAsync(member.ClanId, ct).ConfigureAwait(false);
        await applicants.SendAsync(session, new ClanApplicantsSent(rows.Where(row => row.Role == ClanRole.Applicant)
            .Select(row => new ClanApplicantRow(row.UserNum, row.Nickname, row.Role)).ToArray()), ct).ConfigureAwait(false);
    }

    // The answer to an application or join request. The client stores the role word of every result as its own,
    // so a refusal states the role the sender keeps (kr-1256 sub_51F920).
    private async ValueTask<ClanApplyAnswered> AdmitAsync(ClanCaller caller, int clanId, bool openOnly, CancellationToken ct)
    {
        var existing = await clans.FindMemberAsync(caller.UserNum, ct).ConfigureAwait(false);
        if (existing is not null)
            return Refusal(ClanRole.IsMember(existing.Role) ? ClanApplyAnswered.AlreadyInClan : ClanApplyAnswered.Failed, existing.Role);
        var clan = clanId > 0 ? await clans.FindAsync(clanId, ct).ConfigureAwait(false) : null;
        if (clan is null)
            return Refusal(ClanApplyAnswered.Failed, 0);
        if (clan.DisbandAt is not null)
            return Refusal(ClanApplyAnswered.Disbanding, 0);
        if (clan.Recruitment == 0)
            return Refusal(ClanApplyAnswered.NotRecruiting, 0);
        if (openOnly && clan.Recruitment != OpenRecruitment)
            return Refusal(ClanApplyAnswered.NotOpenRecruiting, 0);
        if (!await clans.AddMemberAsync(clan.ClanId, caller.UserNum, caller.Nickname, caller.Level, ClanRole.Applicant, ct).ConfigureAwait(false))
        {
            // Another request of the same player, or the clan's deletion, came first.
            var now = await clans.FindMemberAsync(caller.UserNum, ct).ConfigureAwait(false);
            return Refusal(now is not null && ClanRole.IsMember(now.Role) ? ClanApplyAnswered.AlreadyInClan : ClanApplyAnswered.Failed, now?.Role ?? 0);
        }
        if (!openOnly)
        {
            logger.LogInformation("User {User} applied to clan {Clan} '{Name}'.", caller.UserNum, clan.ClanId, clan.Name);
            return new ClanApplyAnswered(ClanApplyAnswered.Applied, ClanRole.Applicant, checked((int)clan.ClanId), clan.Name);
        }
        if (!await clans.SeatAsync(clan.ClanId, caller.UserNum, ClanRole.Member, ct).ConfigureAwait(false))
        {
            // The clan is full: the application row would otherwise stay behind a refused join.
            await clans.RemoveMemberAsync(clan.ClanId, caller.UserNum, ct).ConfigureAwait(false);
            logger.LogInformation("User {User} could not join clan {Clan}: no free seat.", caller.UserNum, clan.ClanId);
            return Refusal(ClanApplyAnswered.Failed, 0);
        }
        logger.LogInformation("User {User} joined clan {Clan} '{Name}'.", caller.UserNum, clan.ClanId, clan.Name);
        // The client has no notice for an immediate seat other than the training clan's, so the join is answered
        // as the application it replaces, with the seated role; the home that follows shows the clan.
        return new ClanApplyAnswered(ClanApplyAnswered.Applied, ClanRole.Member, checked((int)clan.ClanId), clan.Name);
    }

    private static ClanApplyAnswered Refusal(int result, int role) => new(result, role, 0, "");
}
