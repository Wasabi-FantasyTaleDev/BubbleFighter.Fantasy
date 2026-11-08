using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.InGame.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InGame.Handlers;

internal sealed partial class RoomFlow
{
    private IMessageSender<PlazaRecruitmentChanged>? _recruitmentChanged;
    private IMessageSender<PlazaRecruitmentTextReceived>? _recruitmentText;
    private IMessageSender<PlazaRecruitmentListReceived>? _recruitmentList;
    private IMessageSender<PlazaRecruitmentRejected>? _recruitmentRejected;

    // kr-1256 sub_9BDEA0 and cn-774 sub_BBC140 divide the filtered total by ten for the native page controls.
    private const int RecruitmentPageSize = 10;

    internal void ConfigurePlazaRecruitment(IMessageSender<PlazaRecruitmentChanged> changed,
        IMessageSender<PlazaRecruitmentTextReceived>? text, IMessageSender<PlazaRecruitmentListReceived>? list = null,
        IMessageSender<PlazaRecruitmentRejected>? rejected = null)
    {
        if (_parties is null || !PlazaBound)
            throw new ConfigException("Plaza recruitment requires party membership and an installed plaza scene.");
        _recruitmentChanged = changed;
        _recruitmentText = text;
        _recruitmentList = list;
        _recruitmentRejected = rejected;
    }

    // The scene executor owns the sign; the party worker only reads its immutable connection feature.
    private void PreparePlazaRecruitment(PlazaMemberState member)
    {
        if (_recruitmentChanged is null || member.Remote) return;
        var party = _parties!.PartyOf(member.UserNumber);
        var allowed = party is null || party.Leader.UserNum == member.UserNumber;
        member.Recruitment = new(allowed, false, string.Empty) { MemberLimit = _parties.MaximumMembers };
        member.RecruitmentFeature ??= member.Game!.RegisterFeature<IPlazaRecruitmentStatus>(member);
    }

    private static void ClearPlazaRecruitment(PlazaMemberState member)
    {
        member.Recruitment = null;
        member.RecruitmentFeature?.Dispose();
        member.RecruitmentFeature = null;
    }

    internal ValueTask HandlePlazaRecruitmentAsync(ISession session, PlazaRecruitmentRequested request, CancellationToken ct) =>
        new(_plaza.Executor.ExecuteAsync(async token =>
        {
            if (RecruitmentMember(session) is not { } member)
            {
                if (request.Active) await RejectPlazaRecruitmentAsync(session, token).ConfigureAwait(false);
                return;
            }
            var current = member.Recruitment!;
            var party = _parties!.PartyOf(member.UserNumber);
            var allowed = party is null || party.Leader.UserNum == member.UserNumber;
            var text = request.Text is null ? current.Text : NormalizeRecruitmentText(request.Text);
            var limit = request.MemberLimit ?? _parties.MaximumMembers;
            if (request.Active && (!allowed || string.IsNullOrEmpty(text) || limit < 2 || limit > _parties.MaximumMembers
                || limit < (party?.Members.Count ?? 1) || request.Category is < 1 or > 5))
            {
                await RejectPlazaRecruitmentAsync(session, token).ConfigureAwait(false);
                return;
            }
            // cn-279 sub_834B10 sends its text before the toggle; the start never discards that pending text.
            var active = request.Active && allowed && text is { Length: > 0 };
            var next = new PlazaRecruitmentState(allowed, active, active ? text! : string.Empty)
            {
                MemberLimit = limit, Category = request.Category,
            };
            if (next == current) return;
            member.Recruitment = next;
            await PublishPlazaRecruitmentAsync(member, token).ConfigureAwait(false);
        }, ct));

    private ValueTask RejectPlazaRecruitmentAsync(ISession session, CancellationToken ct) =>
        _recruitmentRejected is not null ? _recruitmentRejected.SendAsync(session, new(), ct) : ValueTask.CompletedTask;

    internal ValueTask HandlePlazaRecruitmentListAsync(ISession session, PlazaRecruitmentListRequested request, CancellationToken ct) =>
        new(_plaza.Executor.ExecuteAsync(async token =>
        {
            if (_recruitmentList is null || session.Connection.IsClosed
                || !session.TryFeature<IPlayerSession>(out var player)
                || player.Phase is not (SessionPhase.Plaza or SessionPhase.MainMenu)
                || _online is not null && !ReferenceEquals(_online.FindByUserNum(player.Identity.UserNum), session)) return;
            // Only local leaders are joinable through this connection; remote plaza presence conveys no party admission.
            var active = _plaza.Sessions().Where(member => member.IsRecruiting
                    && (request.Category == 0 || member.Recruitment!.Category == request.Category))
                .OrderBy(member => member.Slot)
                .Select(member => new PlazaRecruitmentListing(member.ToMember(), _parties!.PartyOf(member.UserNumber)?.Members.Count ?? 1))
                .ToArray();
            var page = active.Length == 0 ? 0u : Math.Min(request.Page, (uint)((active.Length - 1) / RecruitmentPageSize));
            await _recruitmentList.SendAsync(session, new(active.Skip(checked((int)page * RecruitmentPageSize))
                .Take(RecruitmentPageSize).ToArray(), active.Length, page), token).ConfigureAwait(false);
        }, ct));

    internal ValueTask HandlePlazaRecruitmentTextAsync(ISession session, PlazaRecruitmentTextRequested request, CancellationToken ct) =>
        new(_plaza.Executor.ExecuteAsync(async token =>
        {
            if (RecruitmentMember(session) is not { } member) return;
            var party = _parties!.PartyOf(member.UserNumber);
            if (party is not null && party.Leader.UserNum != member.UserNumber) return;
            if (NormalizeRecruitmentText(request.Text) is not { } text) return;
            member.Recruitment = member.Recruitment! with { Text = text };
            if (member.Recruitment.Active)
                await PublishPlazaRecruitmentAsync(member, token).ConfigureAwait(false);
        }, ct));

    internal ValueTask RefreshPlazaPartyMembershipAsync(PartyPlazaMembershipChanged change, CancellationToken ct) =>
        new(_plaza.Executor.ExecuteAsync(async token =>
        {
            if (_recruitmentChanged is null) return;
            foreach (var member in _plaza.Sessions())
            {
                if (member.Recruitment is not { } previous) continue;
                var party = _parties!.PartyOf(member.UserNumber);
                var allowed = party is null || party.Leader.UserNum == member.UserNumber;
                var departed = change.DepartedUsers.Contains(member.UserNumber);
                if (previous.Allowed == allowed && !departed) continue;
                member.Recruitment = previous with { Allowed = allowed, Active = previous.Active && allowed && !departed,
                    Text = allowed && !departed ? previous.Text : string.Empty };
                await PublishPlazaRecruitmentAsync(member, token).ConfigureAwait(false);
            }
        }, ct));

    private PlazaMemberState? RecruitmentMember(ISession session)
    {
        if (_recruitmentChanged is null || session.Connection.IsClosed
            || !session.TryFeature<IPlayerSession>(out var player) || player.Phase != SessionPhase.Plaza
            || _registry.TryGet(session.Id) is not null
            || _online is not null && !ReferenceEquals(_online.FindByUserNum(player.Identity.UserNum), session))
            return null;
        var member = _plaza.Find(session.Id);
        return member is { Reserved: false, Recruitment: not null }
            && ReferenceEquals(member.Game, session) && member.UserNumber == SessionPlayer.ResolveUserNum(session)
                ? member : null;
    }

    private string? NormalizeRecruitmentText(string text)
    {
        if (text.Length > (_options?.PlazaRecruitmentTextLength ?? new InGameOptions().PlazaRecruitmentTextLength)
            || text.Any(char.IsControl)) return null;
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private async ValueTask PublishPlazaRecruitmentAsync(PlazaMemberState member, CancellationToken ct, bool federate = true)
    {
        var shown = member.ToMember();
        if (federate && !member.Remote)
            PublishPlaza(new(PlazaFederationEventKind.Entered, shown.UserNumber, shown, default));
        foreach (var other in _plaza.Sessions())
        {
            if (_registry.TryGet(other.SessionId) is not null) continue;
            await NotifyScenePeerAsync(other.Game!, _recruitmentChanged!, new(shown), ct).ConfigureAwait(false);
            await SendPlazaRecruitmentTextAsync(other.Game!, shown, ct).ConfigureAwait(false);
        }
        _logger.LogInformation("Plaza recruitment changed for session {Session}: allowed={Allowed}, active={Active}.",
            member.SessionId, shown.Recruitment?.Allowed ?? false, shown.Recruitment?.Active ?? false);
    }

    private async ValueTask ReplayPlazaRecruitmentAsync(ISession session, CancellationToken ct)
    {
        if (_recruitmentChanged is null) return;
        foreach (var member in _plaza.Snapshot())
            await SendPlazaRecruitmentTextAsync(session, member.ToMember(), ct).ConfigureAwait(false);
    }

    private ValueTask SendPlazaRecruitmentTextAsync(ISession session, PlazaMember member, CancellationToken ct) =>
        _recruitmentText is not null && member.Recruitment is { Active: true, Text.Length: > 0 } state
            ? NotifyScenePeerAsync(session, _recruitmentText, new(member.Slot, state.Text), ct)
            : ValueTask.CompletedTask;

    internal sealed partial class PlazaMemberState : IPlazaRecruitmentStatus
    {
        private PlazaRecruitmentState? _recruitment;

        internal PlazaRecruitmentState? Recruitment
        {
            get => Volatile.Read(ref _recruitment);
            set => Volatile.Write(ref _recruitment, value);
        }

        internal IDisposable? RecruitmentFeature { get; set; }

        /// <inheritdoc />
        public bool IsRecruiting => Recruitment is { Allowed: true, Active: true }
            && Game is { Connection.IsClosed: false };

        /// <inheritdoc />
        public int MemberLimit => Recruitment?.MemberLimit ?? 0;
    }
}
