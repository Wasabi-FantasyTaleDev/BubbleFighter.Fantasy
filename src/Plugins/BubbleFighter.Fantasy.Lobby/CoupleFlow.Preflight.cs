using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Contracts.Sessions;

namespace BubbleFighter.Fantasy.Lobby;

internal sealed partial class CoupleFlow
{
    /// <summary>Answers native selector eligibility without creating an invitation or claiming a recipient.</summary>
    public async ValueTask InviterStatusAsync(ISession session, CoupleInviterStatusRequested request, CancellationToken ct)
    {
        var reply = send.InviterStatus ?? throw new InvalidOperationException("This couple build has no native invitation preflight.");
        var self = Player(session) ?? throw new InvalidOperationException("Couple preflight requires a logged-in player.");
        var status = CoupleInviterStatus.Ready;
        if (!ReferenceEquals(online.FindByUserNum(self.Identity.UserNum), session) || !Available(self))
            status = CoupleInviterStatus.Unavailable;
        else if (await store.FindRelationshipAsync(Account(self.Identity.UserNum), ct).ConfigureAwait(false) is not null)
            status = CoupleInviterStatus.Paired;
        else
        {
            lock (_gate)
            {
                Prune(clock.GetUtcNow());
                if (_sent.TryGetValue(self.Identity.UserNum, out var pending) && !pending.Invalidated)
                    status = CoupleInviterStatus.Busy;
            }
        }
        await reply.SendAsync(session, new(status), ct).ConfigureAwait(false);
    }

    /// <summary>Checks the selected live target and opens final confirmation; the actual proposal revalidates everything.</summary>
    public async ValueTask InitialInviteAsync(ISession session, CoupleInitialInviteRequested request, CancellationToken ct)
    {
        var confirmation = send.Confirmation ?? throw new InvalidOperationException("This couple build has no native target preflight.");
        var self = Player(session) ?? throw new InvalidOperationException("Couple target selection requires a logged-in player.");
        if (!request.Selected) return;
        var target = request.Target.Trim();
        async ValueTask Reply(CoupleProposalResult result) =>
            await send.Replied.SendAsync(session, new(result, target), ct).ConfigureAwait(false);
        if (!ReferenceEquals(online.FindByUserNum(self.Identity.UserNum), session) || !Available(self))
        {
            await Reply(CoupleProposalResult.Unavailable).ConfigureAwait(false);
            return;
        }
        if (string.Equals(target, self.Identity.Nickname, StringComparison.Ordinal))
        {
            await Reply(CoupleProposalResult.Self).ConfigureAwait(false);
            return;
        }
        var recipientSession = online.FindByNickname(target);
        var recipient = Player(recipientSession);
        if (recipientSession is null || recipient is null
            || !ReferenceEquals(online.FindByUserNum(recipient.Identity.UserNum), recipientSession)
            || !string.Equals(target, recipient.Identity.Nickname, StringComparison.Ordinal))
        {
            await Reply(await players.NicknameExistsAsync(target, ct).ConfigureAwait(false)
                ? CoupleProposalResult.Offline : CoupleProposalResult.NotFound).ConfigureAwait(false);
            return;
        }
        if (recipient.Phase == SessionPhase.Match)
        {
            await Reply(CoupleProposalResult.Playing).ConfigureAwait(false);
            return;
        }
        if (!Available(recipient))
        {
            await Reply(CoupleProposalResult.Unavailable).ConfigureAwait(false);
            return;
        }
        if (await store.FindRelationshipAsync(Account(self.Identity.UserNum), ct).ConfigureAwait(false) is not null)
        {
            await Reply(CoupleProposalResult.SenderPaired).ConfigureAwait(false);
            return;
        }
        if (await store.FindRelationshipAsync(Account(recipient.Identity.UserNum), ct).ConfigureAwait(false) is not null)
        {
            await Reply(CoupleProposalResult.RecipientPaired).ConfigureAwait(false);
            return;
        }
        bool busy;
        lock (_gate)
        {
            Prune(clock.GetUtcNow());
            busy = _sent.Values.Any(pending => !pending.Invalidated
                && (pending.Sender == self.Identity.UserNum || pending.Recipient == self.Identity.UserNum
                    || pending.Sender == recipient.Identity.UserNum || pending.Recipient == recipient.Identity.UserNum));
        }
        if (busy)
        {
            await Reply(CoupleProposalResult.Busy).ConfigureAwait(false);
            return;
        }
        // No ring or message exists in this request. Final confirmation is the existing authoritative ProposeAsync.
        await confirmation.SendAsync(session, new(recipient.Identity.Nickname), ct).ConfigureAwait(false);
    }
}
