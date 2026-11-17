using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Couples;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Lobby;

/// <summary>The couple replies of one build's game connection.</summary>
/// <param name="Delivered">The invitation shown to the recipient.</param>
/// <param name="Replied">The proposer's result.</param>
/// <param name="Answered">The answer shown to both fighters.</param>
/// <param name="BrokeUp">The breakup reply.</param>
/// <param name="Partner">The partner notice.</param>
/// <param name="Ring">The ring change reply, absent where the native build has no separate ring-change request.</param>
/// <param name="Profile">The fighter's own relationship state, retained by later user-stat refreshes.</param>
/// <param name="InviterStatus">The optional native eligibility reply before target selection.</param>
/// <param name="Confirmation">The optional reply opening the final ring-and-message confirmation.</param>
internal sealed record CoupleSenders(IMessageSender<CoupleProposalDelivered> Delivered, IMessageSender<CoupleProposalReplied> Replied,
    IMessageSender<CoupleAnswerPublished> Answered, IMessageSender<CoupleBreakUpReplied> BrokeUp,
    IMessageSender<CouplePartnerPublished> Partner, IMessageSender<CoupleRingChangeReplied>? Ring,
    IMessageSender<CoupleProfilePublished>? Profile = null,
    IMessageSender<CoupleInviterStatusPublished>? InviterStatus = null,
    IMessageSender<CoupleConfirmationReady>? Confirmation = null);

/// <summary>
/// Proposals, answers, breakups and ring changes between logged-in fighters of one build. The proposer and the recipient
/// are the server's own sessions, never the names a client sends; the relationship lives in <see cref="ICoupleStore"/>,
/// which forms and dissolves both memberships atomically. A proposal waits in the store until it is answered, lapses
/// or expires; the flow remembers only which pending proposal belongs to which logged-in fighter.
/// </summary>
internal sealed partial class CoupleFlow(ICoupleStore store, IOnlinePlayers online, IInventoryRepository inventory,
    IPlayerRepository players, CoupleSenders send, LobbyOptions options, TimeProvider clock,
    ICoupleRingCatalog? rings = null, IInventoryLoadoutStore? loadouts = null, IInventoryBuildScope? buildScope = null,
    IEventBus? events = null, Func<ISession, long, CancellationToken, ValueTask>? publishStat = null,
    ILogger? logger = null, CancellationToken lifetime = default) : IDisposable
{
    // The store compares accounts inside one authority; every lobby of this deployment shares the player data.
    private const string Authority = "players";
    private readonly Lock _gate = new();
    // Pending proposals by proposer and by recipient, with their expiry; an expired entry is dropped on every access,
    // so a fighter who left without answering never blocks later proposals and the maps hold only live proposals.
    private sealed record Pending(Guid Id, DateTimeOffset Expires, long Sender, long Recipient,
        long SenderConnection, long RecipientConnection, string SenderName, string RecipientName, int RingItemId)
    {
        public bool Invalidated { get; set; }
    }
    private readonly Dictionary<long, Pending> _sent = [];
    private readonly Dictionary<long, Pending> _received = [];

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _sent.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray()) _sent.Remove(key);
        foreach (var key in _received.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray()) _received.Remove(key);
    }

    private static CoupleAccount Account(long userNum) => new(Authority, userNum);

    private static IPlayerSession? Player(ISession? session) =>
        session is not null && session.TryFeature<IPlayerSession>(out var player) ? player : null;

    private static bool Available(IPlayerSession? player) =>
        player?.Phase is SessionPhase.MainMenu or SessionPhase.Plaza or SessionPhase.Room;

    /// <summary>Invalidates consent tied to a departed connection; a replacement login cannot answer its old dialog.</summary>
    public void RemoveConnection(long connectionId)
    {
        lock (_gate)
        {
            foreach (var pending in _sent.Values.Where(p => p.SenderConnection == connectionId || p.RecipientConnection == connectionId).ToArray())
                pending.Invalidated = true;
        }
    }

    /// <summary>Moving to another scene invalidates the invitation dialog opened in the previous scene.</summary>
    public void SceneChanged(ISession session) => RemoveConnection(session.Connection.Id);

    /// <summary>A proposal: checks both fighters, the ring and the message, then shows the recipient the invitation.</summary>
    public async ValueTask ProposeAsync(ISession session, CoupleProposed request, CancellationToken ct)
    {
        var self = Player(session) ?? throw new InvalidOperationException("A couple proposal requires a logged-in player.");
        var me = self.Identity;
        if (!request.Confirmed)
        {
            await WithdrawAsync(me.UserNum, ct).ConfigureAwait(false);
            return;
        }
        var target = request.Target.Trim();
        async ValueTask Reply(CoupleProposalResult result) =>
            await send.Replied.SendAsync(session, new(result, target), ct).ConfigureAwait(false);
        if (string.Equals(target, me.Nickname, StringComparison.Ordinal))
        {
            await Reply(CoupleProposalResult.Self).ConfigureAwait(false);
            return;
        }
        var recipientSession = online.FindByNickname(target);
        var recipient = Player(recipientSession);
        if (recipientSession is null || recipient is null)
        {
            await Reply(CoupleProposalResult.NotFound).ConfigureAwait(false);
            return;
        }
        await ClearInvalidatedAsync(me.UserNum, recipient.Identity.UserNum, ct).ConfigureAwait(false);
        if (recipient.Phase is SessionPhase.Match)
        {
            await Reply(CoupleProposalResult.Playing).ConfigureAwait(false);
            return;
        }
        if (self.Phase is not (SessionPhase.MainMenu or SessionPhase.Plaza or SessionPhase.Room)
            || recipient.Phase is not (SessionPhase.MainMenu or SessionPhase.Plaza or SessionPhase.Room))
        {
            await Reply(CoupleProposalResult.Unavailable).ConfigureAwait(false);
            return;
        }
        if (await store.FindRelationshipAsync(Account(me.UserNum), ct).ConfigureAwait(false) is not null)
        {
            await Reply(CoupleProposalResult.SenderPaired).ConfigureAwait(false);
            return;
        }
        if (await store.FindRelationshipAsync(Account(recipient.Identity.UserNum), ct).ConfigureAwait(false) is not null)
        {
            await Reply(CoupleProposalResult.RecipientPaired).ConfigureAwait(false);
            return;
        }
        if (request.Message.Length > options.CoupleMessageLength || request.Message.Any(char.IsControl))
        {
            await Reply(CoupleProposalResult.BadMessage).ConfigureAwait(false);
            return;
        }
        var worn = await WornAsync(me.UserNum, ct).ConfigureAwait(false);
        if (rings is null || !rings.TryGetDisplayRing(request.RingItemId, out _) || !worn.Owned.Contains(request.RingItemId) || worn.Appearance is null)
        {
            await Reply(CoupleProposalResult.NoRing).ConfigureAwait(false);
            return;
        }
        var now = clock.GetUtcNow();
        var expires = now.AddSeconds(options.CoupleProposalSeconds);
        var id = Guid.NewGuid();
        lock (_gate)
        {
            Prune(now);
            if (_sent.ContainsKey(me.UserNum) || _received.ContainsKey(me.UserNum)
                || _sent.ContainsKey(recipient.Identity.UserNum) || _received.ContainsKey(recipient.Identity.UserNum))
                id = Guid.Empty;
            else
            {
                var pending = new Pending(id, expires, me.UserNum, recipient.Identity.UserNum, session.Connection.Id,
                    recipientSession.Connection.Id, me.Nickname, recipient.Identity.Nickname, request.RingItemId);
                _sent[me.UserNum] = pending;
                _received[recipient.Identity.UserNum] = pending;
            }
        }
        if (id == Guid.Empty)
        {
            await Reply(CoupleProposalResult.Busy).ConfigureAwait(false);
            return;
        }
        try
        {
            var outcome = await store.CreateInvitationAsync(Account(me.UserNum), new(id, Account(recipient.Identity.UserNum),
                request.Message, now, expires) { RingItemId = request.RingItemId }, ct).ConfigureAwait(false);
            if (outcome.Result != CoupleStoreResult.Pending)
            {
                Forget(me.UserNum, recipient.Identity.UserNum, id);
                await Reply(outcome.Result == CoupleStoreResult.AlreadyPaired ? CoupleProposalResult.RecipientPaired
                    : CoupleProposalResult.Busy).ConfigureAwait(false);
                return;
            }
            await send.Delivered.SendAsync(recipientSession, new(me, worn.Appearance, recipient.Identity.Nickname, request.Message), ct)
                .ConfigureAwait(false);
            await send.Replied.SendAsync(session, new(CoupleProposalResult.Sent, recipient.Identity.Nickname), ct).ConfigureAwait(false);
        }
        catch
        {
            Forget(me.UserNum, recipient.Identity.UserNum, id);
            // The failed publication must not keep either fighter busy until the timeout.
            await store.CancelInvitationAsync(Account(me.UserNum), id, clock.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>A lapsed proposal box: the recipient's lapse declines, the proposer's withdraws.</summary>
    public async ValueTask LapseAsync(ISession session, CoupleProposalLapsed request, CancellationToken ct)
    {
        var me = (Player(session) ?? throw new InvalidOperationException("A couple lapse requires a logged-in player.")).Identity;
        bool received;
        lock (_gate)
        {
            Prune(clock.GetUtcNow());
            received = _received.ContainsKey(me.UserNum);
        }
        if (received)
            await ResolveAnswerAsync(session, new CoupleAnswered(false, "", me.Nickname), false, ct).ConfigureAwait(false);
        else
            await WithdrawAsync(me.UserNum, ct).ConfigureAwait(false);
    }

    /// <summary>The recipient's answer: accepting forms the couple in the store, and both fighters see the outcome.</summary>
    public ValueTask AnswerAsync(ISession session, CoupleAnswered request, CancellationToken ct) =>
        ResolveAnswerAsync(session, request, true, ct);

    private async ValueTask ResolveAnswerAsync(ISession session, CoupleAnswered request, bool checkNames, CancellationToken ct)
    {
        var me = (Player(session) ?? throw new InvalidOperationException("A couple answer requires a logged-in player.")).Identity;
        Pending pending;
        lock (_gate)
        {
            Prune(clock.GetUtcNow());
            if (!_received.TryGetValue(me.UserNum, out var found))
                return;
            pending = found;
        }
        if (pending.RecipientConnection != session.Connection.Id
            || (checkNames && (request.Inviter != pending.SenderName || request.Recipient != pending.RecipientName)))
            return;
        var id = pending.Id;
        var proposer = online.FindByUserNum(checked((int)pending.Sender));
        var canAccept = request.Accepted && !pending.Invalidated && proposer?.Connection.Id == pending.SenderConnection
            && Available(Player(proposer)) && Available(Player(session))
            && ReferenceEquals(online.FindByUserNum(me.UserNum), session);
        if (canAccept)
            canAccept = rings is not null && rings.TryGetDisplayRing(pending.RingItemId, out _)
                && (await WornAsync(pending.Sender, ct).ConfigureAwait(false)).Owned.Contains(pending.RingItemId);
        lock (_gate)
        {
            if (!_received.TryGetValue(me.UserNum, out var active) || active.Id != id) return;
            canAccept &= !active.Invalidated;
        }
        var outcome = await store.AnswerInvitationAsync(Account(me.UserNum), id, canAccept, clock.GetUtcNow(), ct)
            .ConfigureAwait(false);
        var sender = outcome.Invitation?.Sender.UserNum ?? 0;
        Forget(sender, me.UserNum, id);
        var senderSession = sender == 0 ? null : online.FindByUserNum(checked((int)sender));
        var senderName = Player(senderSession)?.Identity.Nickname ?? (sender == 0 ? "" : pending.SenderName);
        var accepted = outcome.Result == CoupleStoreResult.Accepted;
        var notice = new CoupleAnswerPublished(accepted, senderName, me.Nickname);
        if (accepted)
        {
            // kr-1256 sub_E83610/sub_E82D60 preserve an existing own profile and build their UI from it.
            List<ProfileDelivery> deliveries = [new(session, me.UserNum,
                After: token => send.Answered.SendAsync(session, notice, token))];
            if (senderSession is not null)
                deliveries.Add(new(senderSession, sender, After: token => send.Answered.SendAsync(senderSession, notice, token)));
            await PublishProfilesAsync(deliveries, lifetime).ConfigureAwait(false);
            return;
        }
        await send.Answered.SendAsync(session, notice, ct).ConfigureAwait(false);
        if (senderSession is not null)
            await send.Answered.SendAsync(senderSession, notice, ct).ConfigureAwait(false);
    }

    /// <summary>A breakup: refused without a partner of that name or within the wait after forming; both are told.</summary>
    public async ValueTask BreakUpAsync(ISession session, CoupleBreakUpRequested request, CancellationToken ct)
    {
        var me = (Player(session) ?? throw new InvalidOperationException("A breakup requires a logged-in player.")).Identity;
        var relationship = await store.FindRelationshipAsync(Account(me.UserNum), ct).ConfigureAwait(false);
        var partner = relationship is null ? 0 : relationship.First.UserNum == me.UserNum ? relationship.Second.UserNum : relationship.First.UserNum;
        var partnerName = partner == 0 ? "" : (await players.FindByUserNumAsync(partner, ct).ConfigureAwait(false))?.Nickname ?? "";
        var refused = relationship is null
            || !string.Equals(partnerName, request.Partner.Trim(), StringComparison.Ordinal)
            || clock.GetUtcNow() - relationship.CreatedAt < TimeSpan.FromHours(options.CoupleBreakUpWaitHours)
            || (await store.BreakUpAsync(Account(me.UserNum), relationship.Id, ct).ConfigureAwait(false)).Result != CoupleStoreResult.BrokenUp;
        if (refused)
        {
            await send.BrokeUp.SendAsync(session, new(CoupleBreakUpStatus.Refused, me.Nickname, request.Partner), ct).ConfigureAwait(false);
            return;
        }
        List<ProfileDelivery> deliveries = [new(session, me.UserNum, Before: token =>
            send.BrokeUp.SendAsync(session, new(CoupleBreakUpStatus.Done, me.Nickname, partnerName), token))];
        if (online.FindByUserNum(checked((int)partner)) is { } partnerSession)
            deliveries.Add(new(partnerSession, partner, Before: token =>
                send.BrokeUp.SendAsync(partnerSession, new(CoupleBreakUpStatus.Left, me.Nickname, partnerName), token)));
        await PublishProfilesAsync(deliveries, lifetime).ConfigureAwait(false);
    }

    /// <summary>A ring change: the fighter must own the ring; both partners see the new one.</summary>
    public async ValueTask ChangeRingAsync(ISession session, CoupleRingChangeRequested request, CancellationToken ct)
    {
        var ringSender = send.Ring ?? throw new InvalidOperationException("This couple build has no native ring-change exchange.");
        var me = (Player(session) ?? throw new InvalidOperationException("A ring change requires a logged-in player.")).Identity;
        var relationship = await store.FindRelationshipAsync(Account(me.UserNum), ct).ConfigureAwait(false);
        var partner = relationship is null ? 0 : relationship.First.UserNum == me.UserNum ? relationship.Second.UserNum : relationship.First.UserNum;
        var partnerSession = partner == 0 ? null : online.FindByUserNum(checked((int)partner));
        var owned = Available(Player(session)) && Available(Player(partnerSession))
            && rings is not null && rings.TryGetDisplayRing(request.RingItemId, out _)
            && (await WornAsync(me.UserNum, ct).ConfigureAwait(false)).Owned.Contains(request.RingItemId);
        var outcome = relationship is null || !owned ? null
            : await store.ChangeRingAsync(Account(me.UserNum), relationship.Id, request.RingItemId, ct).ConfigureAwait(false);
        if (outcome?.Result != CoupleStoreResult.Accepted || relationship is null)
        {
            await ringSender.SendAsync(session, new(false, 0), ct).ConfigureAwait(false);
            return;
        }
        // kr-1256 sub_9BF1D0 refreshes ring panels but does not apply the result's item word.
        List<ProfileDelivery> deliveries = [new(session, me.UserNum,
            After: token => ringSender.SendAsync(session, new(true, request.RingItemId), token))];
        if (partnerSession is not null)
            deliveries.Add(new(partnerSession, partner,
                After: token => ringSender.SendAsync(partnerSession, new(true, request.RingItemId), token)));
        await PublishProfilesAsync(deliveries, lifetime).ConfigureAwait(false);
    }

    /// <summary>Restores the relationship and the partner's online indicator after login.</summary>
    public async ValueTask RestoreAsync(ISession session, long userNum, CancellationToken ct)
    {
        var relationship = await store.FindRelationshipAsync(Account(userNum), ct).ConfigureAwait(false);
        List<ProfileDelivery> deliveries = [new(session, userNum)];
        if (relationship is not null)
        {
            var partner = relationship.First.UserNum == userNum ? relationship.Second.UserNum : relationship.First.UserNum;
            if (online.FindByUserNum(checked((int)partner)) is { } partnerSession)
                deliveries.Add(new(partnerSession, partner));
        }
        await PublishProfilesAsync(deliveries, ct).ConfigureAwait(false);
    }

    private async ValueTask WithdrawAsync(long userNum, CancellationToken ct)
    {
        Guid id;
        lock (_gate)
        {
            if (!_sent.TryGetValue(userNum, out var pending))
                return;
            id = pending.Id;
        }
        var outcome = await store.CancelInvitationAsync(Account(userNum), id, clock.GetUtcNow(), ct).ConfigureAwait(false);
        Forget(userNum, outcome.Invitation?.Recipient.UserNum ?? 0, id);
    }

    private async ValueTask ClearInvalidatedAsync(long first, long second, CancellationToken ct)
    {
        Pending[] abandoned;
        lock (_gate)
        {
            Prune(clock.GetUtcNow());
            abandoned = _sent.Values.Where(p => p.Invalidated
                && (p.Sender == first || p.Recipient == first || p.Sender == second || p.Recipient == second)).ToArray();
        }
        foreach (var pending in abandoned)
        {
            await store.CancelInvitationAsync(Account(pending.Sender), pending.Id, clock.GetUtcNow(), ct).ConfigureAwait(false);
            Forget(pending.Sender, pending.Recipient, pending.Id);
        }
    }

    private void Forget(long sender, long recipient, Guid id)
    {
        lock (_gate)
        {
            if (_sent.TryGetValue(sender, out var sent) && sent.Id == id) _sent.Remove(sender);
            if (_received.TryGetValue(recipient, out var received) && received.Id == id) _received.Remove(recipient);
        }
    }

    // The fighter's owned, unexpired items and the worn appearance the invitation shows (one character first).
    private async ValueTask<(HashSet<int> Owned, FighterAppearance? Appearance)> WornAsync(long userNum, CancellationToken ct)
    {
        if ((loadouts is null) != (buildScope is null))
            throw new InvalidOperationException("Couple appearance requires both the read-only loadout store and its canonical build scope.");
        var rows = loadouts is null
            ? await inventory.ListSlotsAsync(userNum, InventoryBag.CharacterBag, InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false)
            : await loadouts.ListLoadoutSlotsAsync(userNum, buildScope!.Scope, InventoryBag.CharacterBag,
                InventoryLimits.MaxInventorySlotsPerBag, ct).ConfigureAwait(false);
        if (rows.Count > InventoryLimits.MaxInventorySlotsPerBag
            || rows.Any(row => row.UserNum != userNum || row.Bag != InventoryBag.CharacterBag || row.ItemId <= 0))
            throw new InvalidOperationException("The couple inventory snapshot exceeds its bound or contains foreign rows.");
        var now = clock.GetUtcNow();
        var live = rows.GroupBy(row => row.ItemId).Select(group => group.Last())
            .Where(row => row.Quantity > 0 && !InventoryExpiry.IsExpired(row, now)).ToArray();
        var selected = live.Where(row => (row.Flags & InventorySlotRecord.WornFlagBit) != 0).ToArray();
        var worn = selected.Select(row => row.ItemId).ToArray();
        var characters = worn.Where(id => id is >= 300001 and <= 300031).ToArray();
        var attributes = events is null || characters.Length != 1 ? []
            : await InventoryPresentations.EquippedAsync(events, send.Delivered.Protocol, selected, worn, ct).ConfigureAwait(false);
        var appearance = characters.Length == 1
            ? new FighterAppearance(characters[0], [characters[0], .. worn.Where(id => id != characters[0])], attributes) : null;
        return (live.Select(row => row.ItemId).ToHashSet(), appearance);
    }
}
