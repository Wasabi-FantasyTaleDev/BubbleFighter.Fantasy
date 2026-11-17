using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// The member lists of the player's own clan and the self-report that fills them. The directory says who is
/// seated; the hub adds what each connected member's client last reported about itself (kr-1256 sub_517E70), the
/// same two words the client puts into its own roster row.
/// </summary>
internal sealed class ClanRosterHandlers(
    IClanDirectory clans,
    ClanHub hub,
    IMessageSender<ClanRosterSent> roster,
    IMessageSender<ClanMemberDetailsSent> details,
    ILogger logger)
{
    /// <summary>Answers the member list; a player without a seat gets an empty list.</summary>
    public async ValueTask RosterAsync(ISession session, ClanRosterRequested _, CancellationToken ct) =>
        await roster.SendAsync(session, new ClanRosterSent(await SeatedRowsAsync(session, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);

    /// <summary>Answers the detailed member list; a player without a seat gets an empty list.</summary>
    public async ValueTask DetailsAsync(ISession session, ClanMemberDetailsRequested _, CancellationToken ct) =>
        await details.SendAsync(session, new ClanMemberDetailsSent(await SeatedRowsAsync(session, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);

    /// <summary>Keeps the caller's report for the rosters other members read. The client expects no answer.</summary>
    public ValueTask ReportAsync(ISession session, ClanMemberReported report, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        hub.Report(caller.UserNum, session, report);
        return ValueTask.CompletedTask;
    }

    // Applicants are left out: they have no seat, and the applicant list is a separate request.
    private async ValueTask<IReadOnlyList<ClanRosterRow>> SeatedRowsAsync(ISession session, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        var member = await clans.FindMemberAsync(caller.UserNum, ct).ConfigureAwait(false);
        if (member is null || !ClanRole.IsMember(member.Role))
        {
            logger.LogDebug("Clan connection {Id} user={User} asked for a roster without a seat; sending an empty list.", session.Id, caller.UserNum);
            return [];
        }
        var members = await clans.ListMembersAsync(member.ClanId, ct).ConfigureAwait(false);
        return members.Where(row => ClanRole.IsMember(row.Role))
            .Select(row => new ClanRosterRow(row.UserNum, row.Nickname, hub.ReportOf(row.UserNum), row.Role)).ToArray();
    }
}
