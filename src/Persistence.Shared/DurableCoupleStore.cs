using BubbleFighter.Fantasy.Contracts.Couples;

namespace BubbleFighter.Fantasy.Persistence;

// Compiled into each durable provider: one invitation state machine, no dependency on another plugin's classes.
internal abstract class DurableCoupleStore : ICoupleStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, CoupleInvitation> _pending = [];
    private readonly Dictionary<Guid, CoupleStoreOutcome> _completed = [];
    private readonly Queue<Guid> _order = [];
    private readonly int _maxPending, _maxPerAccount, _maxCompleted, _maxMessage;

    protected DurableCoupleStore(CoupleInvitationLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        (_maxPending, _maxPerAccount, _maxCompleted, _maxMessage) =
            (limits.MaxPendingInvitations, limits.MaxPendingPerAccount, limits.MaxCompletedInvitations, limits.MaxMessageLength);
    }

    public bool PersistsState => true;

    protected abstract ValueTask<CoupleRelationship?> FindAsync(CoupleAccount member, CancellationToken ct);
    protected abstract ValueTask<CoupleStoreOutcome> FormAsync(CoupleInvitation invitation, DateTimeOffset now, CancellationToken ct);
    protected abstract ValueTask<CoupleStoreOutcome> ChangeAsync(CoupleAccount member, Guid id, int? ring, CancellationToken ct);

    public ValueTask<CoupleRelationship?> FindRelationshipAsync(CoupleAccount member, CancellationToken cancellationToken)
    {
        Validate(member);
        cancellationToken.ThrowIfCancellationRequested();
        return FindAsync(member, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<CoupleInvitation>> ListIncomingAsync(CoupleAccount recipient, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Validate(recipient);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Expire(now);
            return _pending.Values.Where(p => p.Recipient == recipient && p.CreatedAt <= now)
                .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<CoupleStoreOutcome> CreateInvitationAsync(CoupleAccount sender, CoupleInvitationDraft invitation,
        CancellationToken cancellationToken)
    {
        Validate(sender);
        ArgumentNullException.ThrowIfNull(invitation);
        Validate(invitation.Recipient);
        Validate(invitation.Id);
        ArgumentNullException.ThrowIfNull(invitation.Message);
        if (invitation.Message.Length > _maxMessage || invitation.ExpiresAt <= invitation.CreatedAt || invitation.RingItemId < 0)
            throw new ArgumentException("Invalid invitation text, lifetime or ring.", nameof(invitation));
        cancellationToken.ThrowIfCancellationRequested();
        if (sender == invitation.Recipient || sender.Authority != invitation.Recipient.Authority)
            return new(CoupleStoreResult.Forbidden);
        var pending = new CoupleInvitation(invitation.Id, sender, invitation.Recipient, invitation.Message,
            invitation.CreatedAt, invitation.ExpiresAt) { RingItemId = invitation.RingItemId };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Expire(invitation.CreatedAt);
            if (Lookup(invitation.Id) is { } prior)
                return prior.Invitation == pending ? await CurrentAsync(prior, cancellationToken).ConfigureAwait(false)
                    : new(CoupleStoreResult.Conflict);
            if (await FindAsync(sender, cancellationToken).ConfigureAwait(false) is not null
                || await FindAsync(invitation.Recipient, cancellationToken).ConfigureAwait(false) is not null)
                return new(CoupleStoreResult.AlreadyPaired);
            if (_pending.Count >= _maxPending || Count(sender) >= _maxPerAccount || Count(invitation.Recipient) >= _maxPerAccount)
                return new(CoupleStoreResult.CapacityReached);
            _pending.Add(pending.Id, pending);
            return new(CoupleStoreResult.Pending, pending);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<CoupleStoreOutcome> AnswerInvitationAsync(CoupleAccount recipient, Guid invitationId, bool accept,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        Validate(recipient);
        Validate(invitationId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var prior = Lookup(invitationId);
            if (prior?.Invitation is not { } invitation) return new(CoupleStoreResult.NotFound);
            if (invitation.Recipient != recipient) return new(CoupleStoreResult.Forbidden);
            if (prior.Result != CoupleStoreResult.Pending) return await CurrentAsync(prior, cancellationToken).ConfigureAwait(false);
            if (now < invitation.CreatedAt) return new(CoupleStoreResult.Conflict);
            if (now >= invitation.ExpiresAt) return Finish(new(CoupleStoreResult.Expired, invitation));
            if (!accept) return Finish(new(CoupleStoreResult.Rejected, invitation));
            // Retain the invitation until the database commits; an exception leaves it retryable.
            var formed = await FormAsync(invitation, now, cancellationToken).ConfigureAwait(false);
            return Finish(formed with { Invitation = invitation });
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<CoupleStoreOutcome> CancelInvitationAsync(CoupleAccount sender, Guid invitationId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Validate(sender);
        Validate(invitationId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var prior = Lookup(invitationId);
            if (prior?.Invitation is not { } invitation) return new(CoupleStoreResult.NotFound);
            if (invitation.Sender != sender) return new(CoupleStoreResult.Forbidden);
            if (prior.Result != CoupleStoreResult.Pending) return await CurrentAsync(prior, cancellationToken).ConfigureAwait(false);
            if (now < invitation.CreatedAt) return new(CoupleStoreResult.Conflict);
            return Finish(new(now >= invitation.ExpiresAt ? CoupleStoreResult.Expired : CoupleStoreResult.Cancelled, invitation));
        }
        finally { _gate.Release(); }
    }

    public ValueTask<CoupleStoreOutcome> BreakUpAsync(CoupleAccount member, Guid relationshipId, CancellationToken cancellationToken)
    {
        Validate(member);
        Validate(relationshipId);
        cancellationToken.ThrowIfCancellationRequested();
        return ChangeAsync(member, relationshipId, null, cancellationToken);
    }

    public ValueTask<CoupleStoreOutcome> ChangeRingAsync(CoupleAccount member, Guid relationshipId, int ringItemId,
        CancellationToken cancellationToken)
    {
        Validate(member);
        Validate(relationshipId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ringItemId);
        cancellationToken.ThrowIfCancellationRequested();
        return ChangeAsync(member, relationshipId, ringItemId, cancellationToken);
    }

    private async ValueTask<CoupleStoreOutcome> CurrentAsync(CoupleStoreOutcome prior, CancellationToken ct)
    {
        if (prior.Relationship is not { } relationship) return prior;
        var active = await FindAsync(relationship.First, ct).ConfigureAwait(false);
        return active?.Id == relationship.Id ? prior with { Relationship = active }
            : prior with { Result = CoupleStoreResult.BrokenUp, Relationship = null };
    }

    private CoupleStoreOutcome? Lookup(Guid id) => _pending.TryGetValue(id, out var pending)
        ? new(CoupleStoreResult.Pending, pending) : _completed.GetValueOrDefault(id);
    private int Count(CoupleAccount account) => _pending.Values.Count(p => p.Sender == account || p.Recipient == account);
    private CoupleStoreOutcome Finish(CoupleStoreOutcome outcome)
    {
        var id = outcome.Invitation!.Id;
        _pending.Remove(id);
        if (!_completed.ContainsKey(id))
        {
            while (_completed.Count >= _maxCompleted) _completed.Remove(_order.Dequeue());
            _order.Enqueue(id);
        }
        _completed[id] = outcome;
        return outcome;
    }

    private void Expire(DateTimeOffset now)
    {
        foreach (var invitation in _pending.Values.Where(p => p.ExpiresAt <= now).ToArray())
            Finish(new(CoupleStoreResult.Expired, invitation));
    }

    private static void Validate(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A server-created nonempty generation is required.", nameof(id));
    }

    private static void Validate(CoupleAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (string.IsNullOrWhiteSpace(account.Authority) || account.Authority.Length > 128
            || account.Authority != account.Authority.Trim() || account.Authority.Any(char.IsControl))
            throw new ArgumentException("A bounded, trimmed player-data authority is required.", nameof(account));
        ArgumentOutOfRangeException.ThrowIfLessThan(account.UserNum, 1);
    }
}
