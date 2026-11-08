using BubbleFighter.Fantasy.Contracts.Couples;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>
/// Bounded, process-local relationship storage. One short synchronous critical section commits the pending
/// generation and both memberships together; it performs no I/O or callbacks. State resets on process restart.
/// Inventory and native game handlers are separate capabilities and are not enabled by this store.
/// </summary>
public sealed class MemoryCoupleStore : ICoupleStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<CoupleAccount, CoupleRelationship> _members = [];
    private readonly Dictionary<Guid, CoupleRelationship> _relationships = [];
    private readonly Dictionary<Guid, CoupleInvitation> _pending = [];
    private readonly Dictionary<Guid, CoupleStoreOutcome> _completed = [];
    private readonly Queue<Guid> _completionOrder = [];
    private readonly int _maxRelationships;
    private readonly int _maxPending;
    private readonly int _maxPendingPerAccount;
    private readonly int _maxCompleted;
    private readonly int _maxMessageLength;
    private readonly int _maxAuthorityLength;

    /// <summary>Creates a relationship store with explicit bounds on live state, replay receipts and invitation text.</summary>
    public MemoryCoupleStore(int maxRelationships, int maxPending, int maxPendingPerAccount, int maxCompleted,
        int maxMessageLength, int maxAuthorityLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRelationships, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPending, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPendingPerAccount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCompleted, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessageLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAuthorityLength, 1);
        _maxRelationships = maxRelationships;
        _maxPending = maxPending;
        _maxPendingPerAccount = maxPendingPerAccount;
        _maxCompleted = maxCompleted;
        _maxMessageLength = maxMessageLength;
        _maxAuthorityLength = maxAuthorityLength;
    }

    /// <inheritdoc />
    public bool PersistsState => false;

    /// <inheritdoc />
    public ValueTask<CoupleRelationship?> FindRelationshipAsync(CoupleAccount member, CancellationToken cancellationToken)
    {
        Validate(member);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult(_members.GetValueOrDefault(member));
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CoupleInvitation>> ListIncomingAsync(
        CoupleAccount recipient, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Validate(recipient);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Expire(now);
            return ValueTask.FromResult<IReadOnlyList<CoupleInvitation>>(_pending.Values
                .Where(invitation => invitation.Recipient == recipient && invitation.CreatedAt <= now)
                .OrderBy(invitation => invitation.CreatedAt).ThenBy(invitation => invitation.Id).ToArray());
        }
    }

    /// <inheritdoc />
    public ValueTask<CoupleStoreOutcome> CreateInvitationAsync(
        CoupleAccount sender, CoupleInvitationDraft invitation, CancellationToken cancellationToken)
    {
        Validate(sender);
        ArgumentNullException.ThrowIfNull(invitation);
        Validate(invitation.Recipient);
        Validate(invitation.Id);
        ArgumentNullException.ThrowIfNull(invitation.Message);
        if (invitation.Message.Length > _maxMessageLength)
            throw new ArgumentException("The invitation message exceeds the configured bound.", nameof(invitation));
        if (invitation.ExpiresAt <= invitation.CreatedAt)
            throw new ArgumentException("The invitation must expire after its creation time.", nameof(invitation));
        cancellationToken.ThrowIfCancellationRequested();
        if (sender == invitation.Recipient || !StringComparer.Ordinal.Equals(sender.Authority, invitation.Recipient.Authority))
            return ValueTask.FromResult(new CoupleStoreOutcome(CoupleStoreResult.Forbidden));
        var pending = new CoupleInvitation(invitation.Id, sender, invitation.Recipient,
            invitation.Message, invitation.CreatedAt, invitation.ExpiresAt);
        lock (_gate)
        {
            Expire(invitation.CreatedAt);
            if (Lookup(invitation.Id) is { } retained)
                return ValueTask.FromResult(retained.Invitation == pending
                    ? retained : new CoupleStoreOutcome(CoupleStoreResult.Conflict));
            if (_relationships.ContainsKey(invitation.Id))
                return ValueTask.FromResult(new CoupleStoreOutcome(CoupleStoreResult.Conflict));
            if (_members.ContainsKey(sender) || _members.ContainsKey(invitation.Recipient))
                return ValueTask.FromResult(new CoupleStoreOutcome(CoupleStoreResult.AlreadyPaired));
            if (_pending.Count >= _maxPending || PendingFor(sender) >= _maxPendingPerAccount
                || PendingFor(invitation.Recipient) >= _maxPendingPerAccount)
                return ValueTask.FromResult(new CoupleStoreOutcome(CoupleStoreResult.CapacityReached));
            _pending.Add(invitation.Id, pending);
            return ValueTask.FromResult(new CoupleStoreOutcome(CoupleStoreResult.Pending, pending));
        }
    }

    /// <inheritdoc />
    public ValueTask<CoupleStoreOutcome> AnswerInvitationAsync(
        CoupleAccount recipient, Guid invitationId, bool accept, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Validate(recipient);
        Validate(invitationId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var existing = Lookup(invitationId);
            if (existing?.Invitation is not { } invitation)
                return ValueTask.FromResult(new CoupleStoreOutcome(CoupleStoreResult.NotFound));
            if (invitation.Recipient != recipient)
                return ValueTask.FromResult(new CoupleStoreOutcome(CoupleStoreResult.Forbidden));
            if (existing.Result != CoupleStoreResult.Pending) return ValueTask.FromResult(Current(existing));
            if (now < invitation.CreatedAt)
                return ValueTask.FromResult(new CoupleStoreOutcome(CoupleStoreResult.Conflict));
            if (now >= invitation.ExpiresAt)
                return ValueTask.FromResult(Finish(new(CoupleStoreResult.Expired, invitation)));
            if (!accept) return ValueTask.FromResult(Finish(new(CoupleStoreResult.Rejected, invitation)));
            if (_members.ContainsKey(invitation.Sender) || _members.ContainsKey(recipient))
                return ValueTask.FromResult(Finish(new(CoupleStoreResult.AlreadyPaired, invitation)));
            if (_relationships.Count >= _maxRelationships)
                return ValueTask.FromResult(new CoupleStoreOutcome(CoupleStoreResult.CapacityReached, invitation));
            var relationship = new CoupleRelationship(invitation.Id, invitation.Sender, recipient, now);
            _relationships.Add(relationship.Id, relationship);
            _members.Add(relationship.First, relationship);
            _members.Add(relationship.Second, relationship);
            return ValueTask.FromResult(Finish(new(CoupleStoreResult.Accepted, invitation, relationship)));
        }
    }

    /// <inheritdoc />
    public ValueTask<CoupleStoreOutcome> CancelInvitationAsync(
        CoupleAccount sender, Guid invitationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Validate(sender);
        Validate(invitationId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var existing = Lookup(invitationId);
            if (existing?.Invitation is not { } invitation)
                return ValueTask.FromResult(new CoupleStoreOutcome(CoupleStoreResult.NotFound));
            if (invitation.Sender != sender)
                return ValueTask.FromResult(new CoupleStoreOutcome(CoupleStoreResult.Forbidden));
            if (existing.Result != CoupleStoreResult.Pending) return ValueTask.FromResult(Current(existing));
            if (now < invitation.CreatedAt)
                return ValueTask.FromResult(new CoupleStoreOutcome(CoupleStoreResult.Conflict));
            return ValueTask.FromResult(Finish(new(now >= invitation.ExpiresAt
                ? CoupleStoreResult.Expired : CoupleStoreResult.Cancelled, invitation)));
        }
    }

    /// <inheritdoc />
    public ValueTask<CoupleStoreOutcome> BreakUpAsync(
        CoupleAccount member, Guid relationshipId, CancellationToken cancellationToken)
    {
        Validate(member);
        Validate(relationshipId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_members.TryGetValue(member, out var relationship))
                return ValueTask.FromResult(new CoupleStoreOutcome(CoupleStoreResult.NotFound));
            if (relationship.Id != relationshipId)
                return ValueTask.FromResult(new CoupleStoreOutcome(CoupleStoreResult.Conflict));
            _members.Remove(relationship.First);
            _members.Remove(relationship.Second);
            _relationships.Remove(relationship.Id);
            var invitation = _completed.GetValueOrDefault(relationshipId)?.Invitation;
            var result = new CoupleStoreOutcome(CoupleStoreResult.BrokenUp, invitation);
            if (invitation is not null) Finish(result);
            return ValueTask.FromResult(result);
        }
    }

    private CoupleStoreOutcome? Lookup(Guid id) => _pending.TryGetValue(id, out var invitation)
        ? new(CoupleStoreResult.Pending, invitation) : _completed.GetValueOrDefault(id);

    private CoupleStoreOutcome Current(CoupleStoreOutcome outcome) =>
        outcome.Relationship is { } relationship
        && (!_members.TryGetValue(relationship.First, out var active) || active.Id != relationship.Id)
            ? outcome with { Result = CoupleStoreResult.BrokenUp, Relationship = null } : outcome;

    private CoupleStoreOutcome Finish(CoupleStoreOutcome outcome)
    {
        var id = outcome.Invitation!.Id;
        _pending.Remove(id);
        if (!_completed.ContainsKey(id))
        {
            while (_completed.Count >= _maxCompleted) _completed.Remove(_completionOrder.Dequeue());
            _completionOrder.Enqueue(id);
        }
        _completed[id] = outcome;
        return outcome;
    }

    private void Expire(DateTimeOffset now)
    {
        foreach (var invitation in _pending.Values.Where(invitation => now >= invitation.ExpiresAt).ToArray())
            Finish(new(CoupleStoreResult.Expired, invitation));
    }

    private int PendingFor(CoupleAccount member) => _pending.Values.Count(
        invitation => invitation.Sender == member || invitation.Recipient == member);

    private static void Validate(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A server-created nonempty generation is required.", nameof(id));
    }

    private void Validate(CoupleAccount member)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (string.IsNullOrWhiteSpace(member.Authority) || member.Authority.Length > _maxAuthorityLength)
            throw new ArgumentException("A bounded player-data authority is required.", nameof(member));
        ArgumentOutOfRangeException.ThrowIfLessThan(member.UserNum, 1);
    }
}
