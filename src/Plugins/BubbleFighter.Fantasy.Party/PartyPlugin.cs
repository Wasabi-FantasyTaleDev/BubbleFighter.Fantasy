using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Party;

/// <summary>
/// Parties for every build: invitations, visits, joining, leaving, kicks and leader handover, on typed messages from the
/// build's dialect. The party list is the client's own S2C gspUpdateParty, leader first; parties live in memory while
/// their members are logged in, and a disconnect is a leave.
/// </summary>
[Plugin("party", Description = "Party invitations, membership and leadership shared by every client build.",
    Requires = [typeof(IMessageRouter), typeof(IOnlinePlayers)],
    OptionalMany = [typeof(IInboundMessageBinding<PartyInviteRequested>), typeof(IInboundMessageBinding<PartyInviteAccepted>),
        typeof(IInboundMessageBinding<PartyInviteAnswered>), typeof(IInboundMessageBinding<PartyInviteTokenRefused>),
        typeof(IInboundMessageBinding<PartyVisitRequested>), typeof(IInboundMessageBinding<PartyVisitAnswered>),
        typeof(IInboundMessageBinding<PartyRecruitmentJoinRequested>),
        typeof(IInboundMessageBinding<PartyVisitTokenAnswered>),
        typeof(IInboundMessageBinding<PartyLeaveRequested>),
        typeof(IInboundMessageBinding<PartyKickRequested>), typeof(IInboundMessageBinding<PartyLeaderChangeRequested>),
        typeof(IInboundMessageBinding<PartyChatSent>), typeof(IMessageSender<PartyChatReceived>),
        typeof(IInboundMessageBinding<PartyQuickStartRequested>), typeof(IMessageSender<PartyQuickStartAnswered>),
        typeof(IMessageSender<PartyInviteReplied>), typeof(IMessageSender<PartyInviteNotified>), typeof(IMessageSender<PartyUpdated>),
        typeof(IMessageSender<PartyLeaderReplaced>), typeof(IMessageSender<PartyLeft>), typeof(IMessageSender<PartyMemberLeft>),
        typeof(IMessageSender<PartyMemberJoined>), typeof(IMessageSender<PartyKickReplied>), typeof(IMessageSender<PartyKicked>),
        typeof(IMessageSender<PartyMemberKicked>), typeof(IMessageSender<PartyLeaderChanged>),
        typeof(IMessageSender<PartyVisitAsked>), typeof(IMessageSender<PartyVisitFailed>), typeof(IMessageSender<PartyVisitGranted>)],
    Provides = [typeof(IPartyDirectory)])]
public sealed class PartyPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<PartyOptions>();
        var protocol = options.Protocol;
        var online = context.Services.Get<IOnlinePlayers>();
        var send = new PartySenders(context, protocol);
        var book = new PartyBook(options.MaxMembers, options.MaxPendingInvites,
            TimeSpan.FromMilliseconds(options.InviteTimeoutMs), TimeProvider.System);
        // A visit shares the invitation bounds: once accepted it becomes an invitation from the visited player.
        var visits = new PartyVisitBook(options.MaxPendingInvites, TimeSpan.FromMilliseconds(options.InviteTimeoutMs), TimeProvider.System);
        var connections = new Dictionary<int, (ISession Session, PartyMember Member)>();
        var memberships = new PartyMemberships();
        context.Effect(memberships);
        var work = new PartyWorkQueue(options.MaxPendingRequests);
        context.Effect(work);
        await context.RunTaskAsync("party-requests", ct => work.RunAsync(SweepAsync, ct), cancellationToken).ConfigureAwait(false);
        context.Effect(context.Services.Register<IPartyDirectory>(new PartyDirectory(book, RejoinAsync)));
        var messages = context.Services.Get<IMessageRouter>().For(context);

        On<PartyInviteRequested>(async (session, request, ct) =>
        {
            var inviter = Track(session);
            // Room invitations (is_party false) move the target into a waiting room; only the party path is served.
            if (!request.IsParty)
            {
                await SendAsync(session, new PartyInviteReplied(PartyInviteResult.Refused, request.Target), ct).ConfigureAwait(false);
                return;
            }
            var target = online.FindByNickname(request.Target);
            if (target is null || !target.TryFeature<IPlayerSession>(out var targetPlayer))
            {
                await SendAsync(session, new PartyInviteReplied(PartyInviteResult.Offline, request.Target), ct).ConfigureAwait(false);
                return;
            }
            var token = 0u;
            // The invitation is bound to the target's current connection: an answer from a replacement login is stale.
            var result = targetPlayer.Phase is SessionPhase.Match ? PartyInviteResult.Playing
                : book.Invite(inviter, Track(target), target.Connection.Id, out token);
            if (result == PartyInviteResult.Sent
                && !await SendAsync(target, new PartyInviteNotified(true, inviter.Nickname, token), ct).ConfigureAwait(false))
            {
                book.TakeInvite(inviter.UserNum, targetPlayer.Identity.UserNum, target.Connection.Id);
                result = PartyInviteResult.Offline;
            }
            await SendAsync(session, new PartyInviteReplied(result, request.Target), ct).ConfigureAwait(false);
        });

        On<PartyInviteAccepted>(async (session, accept, ct) =>
        {
            var joiner = Track(session);
            if (!accept.IsParty)
            {
                context.Logger.LogInformation("Room invitation accept from {User} is not a party join.", joiner.UserNum);
                return;
            }
            var inviterSession = online.FindByNickname(accept.Inviter);
            if (inviterSession is null || !inviterSession.TryFeature<IPlayerSession>(out var inviterPlayer)
                || !book.TakeInvite(inviterPlayer.Identity.UserNum, joiner.UserNum, session.Connection.Id))
            {
                // A prompt answered after the inviter left or after the invitation ran out: nothing to join.
                context.Logger.LogInformation("Party accept from {User} for {Inviter} has no pending invitation.", joiner.UserNum, accept.Inviter);
                return;
            }
            var inviter = Track(inviterSession);
            var notices = new List<PartyNotice>();
            var result = RecruitmentFull(inviterSession, inviter.UserNum) ? PartyInviteResult.PartyFull
                : book.Join(inviter, joiner, notices);
            if (result != PartyInviteResult.Sent)
                await SendAsync(inviterSession, new PartyInviteReplied(result, joiner.Nickname), ct).ConfigureAwait(false);
            await DeliverAsync(notices, ct).ConfigureAwait(false);
            if (result == PartyInviteResult.Sent)
                await FollowLeaderAsync(session, ct).ConfigureAwait(false);
        });

        On<PartyInviteAnswered>(async (session, answer, ct) =>
        {
            var target = Track(session);
            if (answer.Answer != 3)
                throw new InvalidDataException($"Unsupported party invitation answer {answer.Answer}.");
            var inviterSession = online.FindByNickname(answer.Inviter);
            if (inviterSession is null || !inviterSession.TryFeature<IPlayerSession>(out var inviterPlayer)) return;
            // kr-812 sub_81DEA0 sets refusal answer 3 before kr-812 sub_839E50; the pending row clears on code 14.
            if (book.TakeInvite(inviterPlayer.Identity.UserNum, target.UserNum, session.Connection.Id))
                await SendAsync(inviterSession, new PartyInviteReplied(PartyInviteResult.Refused, target.Nickname), ct).ConfigureAwait(false);
        });

        On<PartyInviteTokenRefused>(async (session, refusal, ct) =>
        {
            var target = Track(session);
            // The token was issued for one pending invitation on this connection; anything else is a stale or foreign word.
            if (!book.TakeInviteByToken(refusal.Token, target.UserNum, session.Connection.Id, out var inviterUserNum))
            {
                context.Logger.LogInformation("Party refusal token {Token} from {User} names no pending invitation; ignored.", refusal.Token, target.UserNum);
                return;
            }
            var inviterSession = online.FindByUserNum(inviterUserNum);
            if (inviterSession is not null)
                await SendAsync(inviterSession, new PartyInviteReplied(PartyInviteResult.Refused, target.Nickname), ct).ConfigureAwait(false);
        });

        On<PartyVisitRequested>(async (session, request, ct) =>
        {
            var visitor = Track(session);
            var failure = VisitFailure(session, visitor, request.Target, out var hostSession, out var host);
            if (failure is null && !request.RequiresApproval)
            {
                var notices = new List<PartyNotice>();
                var result = book.Join(host!, visitor, notices);
                if (result == PartyInviteResult.Sent)
                {
                    await DeliverAsync(notices, ct).ConfigureAwait(false);
                    await FollowLeaderAsync(session, ct, acknowledgeVisit: true).ConfigureAwait(false);
                    return;
                }
                failure = result == PartyInviteResult.PartyFull ? PartyVisitFailure.PartyFull : PartyVisitFailure.Unavailable;
            }
            if (failure is null && hostSession!.TryFeature<IPlazaRecruitmentStatus>(out var recruitment)
                && recruitment.IsRecruiting)
            {
                await AnswerVisitAsync(host!, session, true, ct).ConfigureAwait(false);
                return;
            }
            var token = 0u;
            if (failure is null && !visits.Ask(visitor.UserNum, host!.UserNum, hostSession!.Connection.Id, out token))
                failure = PartyVisitFailure.Unavailable;
            if (failure is null && !await SendAsync(hostSession!, new PartyVisitAsked(visitor.Nickname, token), ct).ConfigureAwait(false))
            {
                visits.Take(visitor.UserNum, host!.UserNum, hostSession!.Connection.Id);
                failure = PartyVisitFailure.Offline;
            }
            if (failure is { } reason)
                await SendAsync(session, new PartyVisitFailed(reason, request.Target), ct).ConfigureAwait(false);
        });

        On<PartyRecruitmentJoinRequested>(async (session, request, ct) =>
        {
            var visitor = Track(session);
            var failure = VisitFailure(session, visitor, request.Target, out var hostSession, out var host);
            if (failure is null)
            {
                if (!hostSession!.TryFeature<IPlazaRecruitmentStatus>(out var recruitment) || !recruitment.IsRecruiting
                    || book.PartyOf(host!.UserNum) is { } party && party.Leader.UserNum != host.UserNum)
                    failure = PartyVisitFailure.Unavailable;
                else if ((book.PartyOf(host!.UserNum)?.Members.Count ?? 1) >= recruitment.MemberLimit)
                    failure = PartyVisitFailure.PartyFull;
            }
            if (failure is null)
            {
                var notices = new List<PartyNotice>();
                var result = book.Join(host!, visitor, notices);
                if (result == PartyInviteResult.Sent)
                {
                    await DeliverAsync(notices, ct).ConfigureAwait(false);
                    await FollowLeaderAsync(session, ct).ConfigureAwait(false);
                    return;
                }
                failure = result == PartyInviteResult.PartyFull ? PartyVisitFailure.PartyFull : PartyVisitFailure.Unavailable;
            }
            await SendAsync(session, new PartyVisitFailed(failure.Value, request.Target), ct).ConfigureAwait(false);
        });

        On<PartyVisitAnswered>(async (session, answer, ct) =>
        {
            var host = Track(session);
            var visitorSession = online.FindByNickname(answer.Visitor);
            if (visitorSession is null || !visitorSession.TryFeature<IPlayerSession>(out var visitorPlayer)
                || !visits.Take(visitorPlayer.Identity.UserNum, host.UserNum, session.Connection.Id))
            {
                context.Logger.LogInformation("Visit answer from {User} for {Visitor} has no pending visit.", host.UserNum, answer.Visitor);
                return;
            }
            await AnswerVisitAsync(host, visitorSession, answer.Accepted, ct).ConfigureAwait(false);
        });

        On<PartyVisitTokenAnswered>(async (session, answer, ct) =>
        {
            var host = Track(session);
            if (!visits.TakeByToken(answer.Token, host.UserNum, session.Connection.Id, out var visitorUserNum)
                || online.FindByUserNum(visitorUserNum) is not { } visitorSession)
            {
                context.Logger.LogInformation("Visit token {Token} from {User} names no pending visit; ignored.", answer.Token, host.UserNum);
                return;
            }
            await AnswerVisitAsync(host, visitorSession, answer.Accepted, ct).ConfigureAwait(false);
        });

        On<PartyLeaveRequested>(async (session, _, ct) =>
        {
            var leaver = Track(session);
            var notices = new List<PartyNotice>();
            if (!book.Leave(leaver, notices))
                notices.Add(new(leaver.UserNum, new PartyUpdated(PartyBook.Solo(leaver))));
            await DeliverAsync(notices, ct).ConfigureAwait(false);
        });

        On<PartyKickRequested>(async (session, kick, ct) =>
        {
            var leader = Track(session);
            var notices = new List<PartyNotice>();
            var result = book.Kick(leader, kick.Target, notices);
            await SendAsync(session, new PartyKickReplied(result, kick.Target), ct).ConfigureAwait(false);
            await DeliverAsync(notices, ct).ConfigureAwait(false);
        });

        On<PartyLeaderChangeRequested>(async (session, change, ct) =>
        {
            var leader = Track(session);
            var notices = new List<PartyNotice>();
            var result = book.ChangeLeader(leader, change.Target, notices);
            if (result != PartyLeaderChangeResult.Changed)
                await SendAsync(session, new PartyLeaderChanged(leader.Nickname, change.Target, result), ct).ConfigureAwait(false);
            await DeliverAsync(notices, ct).ConfigureAwait(false);
        });

        // The party's chat line reaches every member this node tracks, the speaker included: the client draws nothing
        // until it hears its own line back (cn-717 sub_9CB6E0 names it after the member with the sender's number).
        // A player outside any party is a party of one and hears only themselves.
        if (send.ChatReceived is not null)
            On<PartyChatSent>(async (session, line, ct) =>
            {
                var speaker = Track(session);
                var message = new PartyChatReceived(speaker.UserNum, line.Text);
                var members = book.PartyOf(speaker.UserNum)?.Members.Select(member => member.UserNum) ?? [speaker.UserNum];
                await DeliverAsync([.. members.Select(userNum => new PartyNotice(userNum, message))], ct).ConfigureAwait(false);
            });

        // The quick-start answer goes to every member so each client runs its stage start (kr-812 sub_68EF10); the
        // leader's client then sends the ordinary room-create request, which the room flow and the party follow
        // serve. The client's validator kr-812 sub_812AD0 only lets the leader send kinds 1 and 2, so another
        // member's mode request is refused with kind 0, the form kr-812 sub_6875C0 exits a stage on. Kind 4 is the
        // wait dialog and is answered to its requester as-is.
        if (send.QuickStartAnswered is not null)
            On<PartyQuickStartRequested>(async (session, request, ct) =>
            {
                var requester = Track(session);
                var members = book.PartyOf(requester.UserNum) is { } party
                    && party.Leader.UserNum == requester.UserNum && request.Kind is 1 or 2
                        ? party.Members.Select(member => member.UserNum)
                        : null;
                if (members is null)
                {
                    await SendAsync(session, new PartyQuickStartAnswered(request.Kind == 4 ? 4 : 0), ct).ConfigureAwait(false);
                    return;
                }
                await DeliverAsync([.. members.Select(userNum => new PartyNotice(userNum, new PartyQuickStartAnswered(request.Kind)))], ct).ConfigureAwait(false);
            });

        // A close only signals the owned worker. Sweeping tracked sessions also catches login replacement.
        context.Effect(context.Events.On<ConnectionClosedEvent>(_ => work.Wake()));
        return;

        void On<T>(MessageHandler<T> handler) where T : IMessage =>
            context.Effect(messages.On<T>(protocol, (session, message, ct) =>
                work.ExecuteAsync(token => handler(session, message, token), ct)));

        async ValueTask FollowLeaderAsync(ISession session, CancellationToken ct, bool acknowledgeVisit = false)
        {
            var party = book.PartyOf(Track(session).UserNum)
                ?? throw new InvalidOperationException("Committed party membership is missing.");
            if (!await context.Services.Get<IMessageRouter>().DispatchAsync(protocol, session,
                new PartySceneFollowRequested(party.Leader.UserNum, acknowledgeVisit), ct).ConfigureAwait(false))
                throw new InvalidOperationException("Party scene following requires a scene handler for this protocol.");
        }

        // The checks a visit passes before the visited player is asked; the book repeats the party checks on accept.
        PartyVisitFailure? VisitFailure(ISession session, PartyMember visitor, string target, out ISession? hostSession, out PartyMember? host)
        {
            host = null;
            hostSession = online.FindByNickname(target);
            if (hostSession is null || !hostSession.TryFeature<IPlayerSession>(out var hostPlayer)) return PartyVisitFailure.Offline;
            if (hostPlayer.Identity.UserNum == visitor.UserNum) return PartyVisitFailure.Self;
            if (session.TryFeature<IPlayerSession>(out var visitorPlayer) && visitorPlayer.Phase is SessionPhase.Match)
                return PartyVisitFailure.VisitorPlaying;
            if (hostPlayer.Phase is SessionPhase.Match) return PartyVisitFailure.TargetPlaying;
            host = Track(hostSession);
            if (RecruitmentFull(hostSession, host.UserNum)) return PartyVisitFailure.PartyFull;
            if (book.PartyOf(host.UserNum) is { } party)
            {
                if (party.Members.Any(member => member.UserNum == visitor.UserNum)) return PartyVisitFailure.SameParty;
                if (party.Members.Count >= options.MaxMembers) return PartyVisitFailure.PartyFull;
            }
            // The visitor joins the visited player's party, which the book allows only from outside any party.
            return book.PartyOf(visitor.UserNum) is null ? null : PartyVisitFailure.Unavailable;
        }

        bool RecruitmentFull(ISession hostSession, int userNum) =>
            hostSession.TryFeature<IPlazaRecruitmentStatus>(out var recruitment) && recruitment.IsRecruiting
            && (book.PartyOf(userNum)?.Members.Count ?? 1) >= recruitment.MemberLimit;

        // An accepted visit becomes an invitation from the visited player that the visitor's client accepts by itself.
        async ValueTask AnswerVisitAsync(PartyMember host, ISession visitorSession, bool accepted, CancellationToken ct)
        {
            var visitor = Track(visitorSession);
            if (!accepted)
            {
                await SendAsync(visitorSession, new PartyVisitFailed(PartyVisitFailure.Declined, host.Nickname), ct).ConfigureAwait(false);
                return;
            }
            var hostSession = online.FindByUserNum(host.UserNum);
            var result = hostSession is null ? PartyInviteResult.Offline
                : RecruitmentFull(hostSession, host.UserNum) ? PartyInviteResult.PartyFull
                : book.Invite(host, visitor, visitorSession.Connection.Id);
            if (result != PartyInviteResult.Sent)
            {
                await SendAsync(visitorSession, new PartyVisitFailed(result switch
                {
                    PartyInviteResult.SameParty => PartyVisitFailure.SameParty,
                    PartyInviteResult.PartyFull => PartyVisitFailure.PartyFull,
                    _ => PartyVisitFailure.Unavailable,
                }, host.Nickname), ct).ConfigureAwait(false);
                return;
            }
            if (!await SendAsync(visitorSession, new PartyVisitGranted(host.Nickname), ct).ConfigureAwait(false))
                book.TakeInvite(host.UserNum, visitor.UserNum, visitorSession.Connection.Id);
        }

        // A party that crossed nodes is rebuilt as a join without an invitation: both members already chose to travel.
        async ValueTask<bool> RejoinAsync(ISession leaderSession, ISession memberSession, CancellationToken ct)
        {
            var joined = false;
            await work.ExecuteAsync(async token =>
            {
                // A connection closed by the move itself must leave before its replacement can be tracked.
                await SweepAsync(token).ConfigureAwait(false);
                if (!IsCurrent(leaderSession) || !IsCurrent(memberSession)) return;
                var leader = Track(leaderSession);
                var member = Track(memberSession);
                if (book.PartyOf(leader.UserNum) is { } party && party.Leader.UserNum != leader.UserNum) return;
                var notices = new List<PartyNotice>();
                joined = book.Join(leader, member, notices) == PartyInviteResult.Sent;
                await DeliverAsync(notices, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            return joined;
        }

        bool IsCurrent(ISession session) => !session.Connection.IsClosed && session.TryFeature<IPlayerSession>(out var player)
            && online.FindByUserNum(player.Identity.UserNum)?.Connection.Id == session.Connection.Id
            && (!connections.TryGetValue(player.Identity.UserNum, out var previous)
                || previous.Session.Connection.Id == session.Connection.Id);

        PartyMember Track(ISession session)
        {
            if (!session.TryFeature<IPlayerSession>(out var player))
                throw new InvalidOperationException("Party requests require an authenticated player.");
            var member = Member(player);
            if (session.Connection.IsClosed || online.FindByUserNum(member.UserNum)?.Connection.Id != session.Connection.Id)
                throw new InvalidOperationException("Party requests require the current live player connection.");
            if (connections.TryGetValue(member.UserNum, out var previous)
                && previous.Session.Connection.Id != session.Connection.Id)
                throw new InvalidOperationException("The previous party connection must leave before its replacement participates.");
            connections[member.UserNum] = (session, member);
            return member;
        }

        async ValueTask SweepAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (userNum, tracked) in connections.ToArray())
            {
                if (!tracked.Session.Connection.IsClosed
                    && online.FindByUserNum(userNum)?.Connection.Id == tracked.Session.Connection.Id) continue;
                connections.Remove(userNum);
                memberships.Forget(tracked.Session.Connection.Id);
                book.ForgetInvites(userNum);
                visits.ForgetPlayer(userNum);
                var notices = new List<PartyNotice>();
                book.Leave(tracked.Member, notices);
                await DeliverAsync(notices, ct).ConfigureAwait(false);
            }
        }

        async ValueTask DeliverAsync(IReadOnlyList<PartyNotice> notices, CancellationToken ct)
        {
            foreach (var notice in notices)
            {
                // A member who logged out between the change and the send has nobody to tell.
                ct.ThrowIfCancellationRequested();
                if (!connections.TryGetValue(notice.UserNum, out var tracked)) continue;
                var recipient = tracked.Session;
                if (recipient.Connection.IsClosed
                    || online.FindByUserNum(notice.UserNum)?.Connection.Id != recipient.Connection.Id) continue;
                await SendAsync(recipient, notice.Message, ct).ConfigureAwait(false);
            }
            if (notices.Any(notice => notice.Message is PartyUpdated))
                await context.Events.SerialAsync(new PartyPlazaMembershipChanged(protocol)
                {
                    DepartedUsers = notices.Where(notice => notice.Message is PartyLeft or PartyKicked)
                        .Select(notice => notice.UserNum).Distinct().ToArray(),
                }, ct).ConfigureAwait(false);
        }

        async ValueTask<bool> SendAsync(ISession recipient, object message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (recipient.Connection.IsClosed) return false;
            try
            {
                if (message is PartyUpdated updated) memberships.Set(recipient, updated.Party);
                await send.SendAsync(recipient, message, ct).ConfigureAwait(false);
                return true;
            }
            catch (IOException ex)
            {
                // A failed recipient must not prevent the remaining members from learning a committed change.
                context.Logger.LogWarning(ex, "Party send failed on connection {Connection}; closing it.", recipient.Connection.Id);
                await recipient.Connection.CloseAsync("party send failed").ConfigureAwait(false);
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && recipient.Connection.IsClosed)
            {
                context.Logger.LogDebug(ex, "Party send to closed connection {Connection} dropped.", recipient.Connection.Id);
                return false;
            }
        }
    }

    private static PartyMember Member(IPlayerSession player) => new(player.Identity.UserNum, player.Identity.Nickname, player.Identity.Level);
}

/// <summary>
/// The <see cref="IPartyMembership"/> features this row registers: one per member connection, replaced on every party
/// list the member is sent and removed when the member is alone again, the connection closes or the row unloads.
/// </summary>
// The registered directory: the book's reads and reservations, plus the rebuild that must run on the party worker.
internal sealed class PartyDirectory(PartyBook book, Func<ISession, ISession, CancellationToken, ValueTask<bool>> rejoin) : IPartyDirectory
{
    public int MaximumMembers => book.MaximumMembers;

    public bool SupportsSoloReservation => book.SupportsSoloReservation;

    public IDisposable? TryReserveSolo(int userNum) => book.TryReserveSolo(userNum);

    public PartySnapshot? PartyOf(int userNum) => book.PartyOf(userNum);

    public ValueTask<bool> RejoinAsync(ISession leader, ISession member, CancellationToken cancellationToken) =>
        rejoin(leader, member, cancellationToken);
}

internal sealed class PartyMemberships : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, IDisposable> _registered = [];
    private bool _disposed;

    public void Set(ISession session, PartySnapshot party)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registered.Remove(session.Connection.Id, out var previous)) previous.Dispose();
            if (party.Members.Count > 1)
                _registered[session.Connection.Id] = session.RegisterFeature<IPartyMembership>(new Membership(party));
        }
    }

    public void Forget(long connectionId)
    {
        lock (_gate)
            if (_registered.Remove(connectionId, out var previous)) previous.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var registration in _registered.Values) registration.Dispose();
            _registered.Clear();
        }
    }

    private sealed record Membership(PartySnapshot Party) : IPartyMembership;
}

/// <summary>The build's party senders, each required once the row activates.</summary>
internal sealed class PartySenders(IPluginContext context, string protocol)
{
    public IMessageSender<PartyInviteReplied> InviteReplied { get; } = Sender<PartyInviteReplied>(context, protocol);
    public IMessageSender<PartyInviteNotified> InviteNotified { get; } = Sender<PartyInviteNotified>(context, protocol);
    // Kicks and leader handover are only produced by builds that bind those requests, so a build may lack their
    // senders; a notice that needs a missing one fails loudly instead of being dropped.
    public IMessageSender<PartyKickReplied>? KickReplied { get; } = OptionalSender<PartyKickReplied>(context, protocol);
    public IMessageSender<PartyLeaderChanged>? LeaderChanged { get; } = OptionalSender<PartyLeaderChanged>(context, protocol);
    // Party chat exists where the build's dialect binds it; a build without it publishes no sender.
    public IMessageSender<PartyChatReceived>? ChatReceived { get; } = OptionalSender<PartyChatReceived>(context, protocol);
    private readonly IMessageSender<PartyUpdated> _updated = Sender<PartyUpdated>(context, protocol);
    private readonly IMessageSender<PartyLeaderReplaced> _replaced = Sender<PartyLeaderReplaced>(context, protocol);
    private readonly IMessageSender<PartyLeft> _left = Sender<PartyLeft>(context, protocol);
    private readonly IMessageSender<PartyMemberLeft> _memberLeft = Sender<PartyMemberLeft>(context, protocol);
    private readonly IMessageSender<PartyMemberJoined> _joined = Sender<PartyMemberJoined>(context, protocol);
    private readonly IMessageSender<PartyKicked>? _kicked = OptionalSender<PartyKicked>(context, protocol);
    private readonly IMessageSender<PartyMemberKicked>? _memberKicked = OptionalSender<PartyMemberKicked>(context, protocol);
    // Builds without a visit request (cn-279, cn-324) have no visit senders either.
    private readonly IMessageSender<PartyVisitAsked>? _visitAsked = OptionalSender<PartyVisitAsked>(context, protocol);
    private readonly IMessageSender<PartyVisitFailed>? _visitFailed = OptionalSender<PartyVisitFailed>(context, protocol);
    private readonly IMessageSender<PartyVisitGranted>? _visitGranted = OptionalSender<PartyVisitGranted>(context, protocol);
    // The quick-start answer exists where the build states both one-kind-word bodies (kr-812 only so far).
    public IMessageSender<PartyQuickStartAnswered>? QuickStartAnswered { get; } =
        OptionalSender<PartyQuickStartAnswered>(context, protocol);

    /// <summary>Sends one of the party notices the book decides.</summary>
    public ValueTask SendAsync(ISession session, object message, CancellationToken ct) => message switch
    {
        PartyInviteReplied m => InviteReplied.SendAsync(session, m, ct),
        PartyInviteNotified m => InviteNotified.SendAsync(session, m, ct),
        PartyKickReplied m => Required(KickReplied).SendAsync(session, m, ct),
        PartyUpdated m => _updated.SendAsync(session, m, ct),
        PartyLeaderReplaced m => _replaced.SendAsync(session, m, ct),
        PartyLeft m => _left.SendAsync(session, m, ct),
        PartyMemberLeft m => _memberLeft.SendAsync(session, m, ct),
        PartyMemberJoined m => _joined.SendAsync(session, m, ct),
        PartyKicked m => Required(_kicked).SendAsync(session, m, ct),
        PartyMemberKicked m => Required(_memberKicked).SendAsync(session, m, ct),
        PartyLeaderChanged m => Required(LeaderChanged).SendAsync(session, m, ct),
        PartyVisitAsked m => Required(_visitAsked).SendAsync(session, m, ct),
        PartyVisitFailed m => Required(_visitFailed).SendAsync(session, m, ct),
        PartyVisitGranted m => Required(_visitGranted).SendAsync(session, m, ct),
        PartyQuickStartAnswered m => Required(QuickStartAnswered).SendAsync(session, m, ct),
        PartyChatReceived m => Required(ChatReceived).SendAsync(session, m, ct),
        _ => throw new InvalidOperationException($"No party sender for {message.GetType().Name}."),
    };

    private static IMessageSender<T> Required<T>(IMessageSender<T>? sender) where T : IMessage =>
        sender ?? throw new InvalidOperationException($"The build has no {typeof(T).Name} sender.");

    private static IMessageSender<T> Sender<T>(IPluginContext context, string protocol) where T : IMessage =>
        OptionalSender<T>(context, protocol) ?? throw new ConfigException($"Party requires exactly one sender for {typeof(T).Name} on {protocol}.");

    private static IMessageSender<T>? OptionalSender<T>(IPluginContext context, string protocol) where T : IMessage
    {
        var senders = context.Services.GetAll<IMessageSender<T>>().Where(sender => sender.Protocol == protocol).ToArray();
        return senders.Length switch
        {
            0 => null,
            1 => senders[0],
            _ => throw new ConfigException($"Party requires at most one sender for {typeof(T).Name} on {protocol}."),
        };
    }
}

/// <summary>Configures the parties of one build.</summary>
public sealed class PartyOptions : IValidatableOptions
{
    /// <summary>Gets or sets the build's game protocol.</summary>
    [Required] public string Protocol { get; set; } = "";

    /// <summary>Gets or sets the party size: 8 in kr-812 (its member-count checks), 12 in kr-1256.</summary>
    public int MaxMembers { get; set; } = 8;

    /// <summary>Gets or sets how long an invitation can be accepted; the inviter's client drops its row after 30 s.</summary>
    public int InviteTimeoutMs { get; set; } = 30000;

    /// <summary>Gets or sets the bound on invitations waiting for an answer.</summary>
    public int MaxPendingInvites { get; set; } = 1024;

    /// <summary>Gets or sets the maximum queued requests; one additional request may be executing.</summary>
    public int MaxPendingRequests { get; set; } = 1024;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        if (MaxPendingRequests is < 1 or > 65536)
            yield return new ValidationResult("max_pending_requests must be 1..65536.", [nameof(MaxPendingRequests)]);
        if (MaxMembers is < 2 or > 16)
            yield return new ValidationResult("max_members must be 2..16.", [nameof(MaxMembers)]);
        if (InviteTimeoutMs is < 1000 or > 600000)
            yield return new ValidationResult("invite_timeout_ms must be 1000..600000.", [nameof(InviteTimeoutMs)]);
        if (MaxPendingInvites is < 1 or > 65536)
            yield return new ValidationResult("max_pending_invites must be 1..65536.", [nameof(MaxPendingInvites)]);
    }
}
