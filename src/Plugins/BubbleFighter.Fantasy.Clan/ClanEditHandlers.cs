using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Clan;

/// <summary>
/// The master's edits of the clan's introduction, notice, recruitment word and mark. Each edit is stored first and
/// then told the way the client expects it: the editor gets the result its dialog waits for, and the members get the
/// change notice that rewrites their own copy. A request from anyone but the master changes nothing and is logged;
/// the client offers these edits only in the master's dialogs.
/// </summary>
internal sealed class ClanEditHandlers(IClanDirectory clans, ClanHub hub, ClanEditHandlers.Senders senders, ILogger logger)
{
    /// <summary>The six frames the edits answer with.</summary>
    /// <param name="IntroductionResult">The introduction edit's result to the editor.</param>
    /// <param name="IntroductionChanged">The introduction notice to the other members.</param>
    /// <param name="NoticeResult">The notice edit's result to the editor.</param>
    /// <param name="NoticeChanged">The notice notice to every member.</param>
    /// <param name="RecruitmentResult">The recruitment edit's result to the editor.</param>
    /// <param name="MarkChanged">The mark notice to every member.</param>
    internal sealed record Senders(
        IMessageSender<ClanIntroductionEditAnswered> IntroductionResult,
        IMessageSender<ClanIntroductionChanged> IntroductionChanged,
        IMessageSender<ClanNoticeEditAnswered> NoticeResult,
        IMessageSender<ClanNoticeChanged> NoticeChanged,
        IMessageSender<ClanRecruitmentEditAnswered> RecruitmentResult,
        IMessageSender<ClanMarkChanged> MarkChanged);

    /// <summary>Stores a new introduction: the editor's result rewrites its copy, the other members get the notice.</summary>
    public async ValueTask IntroductionAsync(ISession session, ClanIntroductionEditRequested request, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        if (await MasteredClanAsync(session, caller, "introduction", ct).ConfigureAwait(false) is not { } clan
            || !await clans.UpdateAsync(clan with { Introduction = request.Introduction }, ct).ConfigureAwait(false))
            return;
        await senders.IntroductionResult.SendAsync(session, new ClanIntroductionEditAnswered(ClanIntroductionEditAnswered.Changed, request.Introduction), ct)
            .ConfigureAwait(false);
        var others = (await clans.ListMembersAsync(clan.ClanId, ct).ConfigureAwait(false)).Where(row => row.UserNum != caller.UserNum).ToArray();
        await hub.BroadcastAsync(others, senders.IntroductionChanged, new ClanIntroductionChanged(0, request.Introduction), seatedOnly: true, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Stores a new notice. The notice result changes nothing on the client (kr-1256 sub_52D7A0), so every member,
    /// the editor included, gets the notice that rewrites its copy.
    /// </summary>
    public async ValueTask NoticeAsync(ISession session, ClanNoticeEditRequested request, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        if (await MasteredClanAsync(session, caller, "notice", ct).ConfigureAwait(false) is not { } clan
            || !await clans.UpdateAsync(clan with { Notice = request.Notice }, ct).ConfigureAwait(false))
            return;
        await senders.NoticeResult.SendAsync(session, new ClanNoticeEditAnswered(ClanNoticeEditAnswered.Changed), ct).ConfigureAwait(false);
        await hub.BroadcastAsync(await clans.ListMembersAsync(clan.ClanId, ct).ConfigureAwait(false), senders.NoticeChanged,
            new ClanNoticeChanged(request.Notice), seatedOnly: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores one of the three recruitment words for the sender's own clan and answers only the editor: turning
    /// recruitment off makes the receiving client turn every applicant away itself, which is the master's to do.
    /// </summary>
    public async ValueTask RecruitmentAsync(ISession session, ClanRecruitmentEditRequested request, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        if (await MasteredClanAsync(session, caller, "recruitment", ct).ConfigureAwait(false) is not { } clan)
            return;
        var valid = request.ClanId == clan.ClanId
            && request.Recruitment is ClanRecruitment.Closed or ClanRecruitment.ByApplication or ClanRecruitment.Open;
        var stored = valid && await clans.UpdateAsync(clan with { Recruitment = request.Recruitment }, ct).ConfigureAwait(false);
        await senders.RecruitmentResult.SendAsync(session,
            new ClanRecruitmentEditAnswered(checked((int)clan.ClanId), stored ? 1 : 0, stored ? request.Recruitment : clan.Recruitment), ct).ConfigureAwait(false);
    }

    /// <summary>Stores the mark of the sender's own clan and tells every member; another clan's id changes nothing.</summary>
    public async ValueTask MarkAsync(ISession session, ClanMarkEditRequested request, CancellationToken ct)
    {
        var caller = ClanCaller.Require(session);
        if (await MasteredClanAsync(session, caller, "mark", ct).ConfigureAwait(false) is not { } clan || request.ClanId != clan.ClanId
            || !await clans.UpdateAsync(clan with { Mark = request.Mark }, ct).ConfigureAwait(false))
            return;
        await hub.BroadcastAsync(await clans.ListMembersAsync(clan.ClanId, ct).ConfigureAwait(false), senders.MarkChanged,
            new ClanMarkChanged(request.Mark), seatedOnly: true, ct).ConfigureAwait(false);
    }

    // The clan the caller is the master of, or null (logged) for anyone else.
    private async ValueTask<ClanRecord?> MasteredClanAsync(ISession session, ClanCaller caller, string what, CancellationToken ct)
    {
        var row = await clans.FindMemberAsync(caller.UserNum, ct).ConfigureAwait(false);
        var clan = row is { Role: ClanRole.Master } ? await clans.FindAsync(row.ClanId, ct).ConfigureAwait(false) : null;
        if (clan is null)
            logger.LogInformation("Clan connection {Id} user={User} with role {Role} asked to edit the clan {What}; nothing changed.",
                session.Id, caller.UserNum, row?.Role ?? 0, what);
        return clan;
    }
}
