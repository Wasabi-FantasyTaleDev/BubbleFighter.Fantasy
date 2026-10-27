using BubbleFighter.Fantasy.Contracts.Messenger;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>Process-local messenger accounts, friendships, categories and requests, bounded by the row's player limit; lost on restart.</summary>
public sealed class MemoryMessengerStore(int maximumUsers) : IMessengerStore
{
    private sealed record Relation(long Friend, int Category, bool Blocked);
    private readonly object _gate = new();
    private readonly Dictionary<long, MessengerUser> _users = [];
    private readonly Dictionary<(string Tenant, string Subject), long> _bySubject = [];
    private readonly Dictionary<long, Dictionary<long, Relation>> _relations = [];
    private readonly Dictionary<long, SortedDictionary<int, MessengerCategory>> _categories = [];
    private readonly Dictionary<int, MessengerStoredRequest> _requests = [];
    private long _nextUser = 1;
    private int _nextRequest = 1;

    /// <inheritdoc />
    public ValueTask<MessengerUser> UpsertUserAsync(MessengerIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_bySubject.TryGetValue((identity.Tenant, identity.Subject), out var id))
                return ValueTask.FromResult(_users[id] = _users[id] with { Nickname = identity.Nickname, GameCode = identity.GameCode });
            if (_users.Count >= maximumUsers) throw new InvalidOperationException("Messenger account storage is full.");
            var user = new MessengerUser(new(_nextUser++), identity.Tenant, identity.Subject, identity.Nickname, identity.GameCode);
            _users.Add(user.Id.Value, user);
            _bySubject.Add((identity.Tenant, identity.Subject), user.Id.Value);
            return ValueTask.FromResult(user);
        }
    }

    /// <inheritdoc />
    public ValueTask<MessengerUser?> FindUserAsync(MessengerUserId id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return ValueTask.FromResult(_users.GetValueOrDefault(id.Value));
    }

    /// <inheritdoc />
    public ValueTask<MessengerUser?> FindUserByNicknameAsync(string tenant, string nickname, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var matches = _users.Values.Where(u => u.Tenant == tenant && string.Equals(u.Nickname, nickname, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            return ValueTask.FromResult(matches.Length == 1 ? matches[0] : null);
        }
    }

    /// <inheritdoc />
    public ValueTask<MessengerRelations> LoadRelationsAsync(MessengerUserId owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var relations = Relations(owner.Value).Values.OrderBy(r => r.Friend)
                .Select(r => new MessengerRelation(_users[r.Friend], r.Category, r.Blocked, Relations(r.Friend).GetValueOrDefault(owner.Value)?.Blocked == true))
                .ToArray();
            return ValueTask.FromResult(new MessengerRelations(relations, Categories(owner.Value).Values.ToArray()));
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<MessengerStoredRequest>> ListIncomingRequestsAsync(MessengerUserId recipient, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<MessengerStoredRequest>>(_requests.Values.Where(r => r.To == recipient).OrderBy(r => r.Handle).ToArray());
    }

    /// <inheritdoc />
    public ValueTask<MessengerRequestOutcome> CreateRequestAsync(
        MessengerUserId from, MessengerUserId to, string message, DateTime now, MessengerLimits limits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (Relations(from.Value).ContainsKey(to.Value)) return Result(MessengerResult.AlreadyFriends, null);
            var pending = _requests.Values.FirstOrDefault(r => r.From == from && r.To == to || r.From == to && r.To == from);
            if (pending is not null) return Result(MessengerResult.AlreadyPending, pending);
            if (_requests.Values.Count(r => r.To == to) >= limits.MaxPendingRequests) return Result(MessengerResult.LimitReached, null);
            if (_nextRequest == int.MaxValue) throw new InvalidOperationException("Messenger request handles are exhausted.");
            var request = new MessengerStoredRequest(_nextRequest++, from, to, message, now);
            _requests.Add(request.Handle, request);
            return Result(MessengerResult.Ok, request);
        }
    }

    /// <inheritdoc />
    public ValueTask<MessengerRequestOutcome> ResolveRequestAsync(
        int handle, MessengerUserId recipient, bool accept, MessengerLimits limits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_requests.TryGetValue(handle, out var request) || request.To != recipient) return Result(MessengerResult.NotFound, null);
            if (accept)
            {
                if (Relations(request.From.Value).Count >= limits.MaxFriends || Relations(request.To.Value).Count >= limits.MaxFriends)
                    return Result(MessengerResult.LimitReached, request);
                Relations(request.From.Value)[request.To.Value] = new(request.To.Value, 0, false);
                Relations(request.To.Value)[request.From.Value] = new(request.From.Value, 0, false);
            }
            _requests.Remove(handle);
            return Result(MessengerResult.Ok, request);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteFriendshipAsync(MessengerUserId owner, MessengerUserId friend, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var removed = Relations(owner.Value).Remove(friend.Value);
            removed |= Relations(friend.Value).Remove(owner.Value);
            return ValueTask.FromResult(removed);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> SetBlockedAsync(MessengerUserId owner, MessengerUserId friend, bool blocked, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var relations = Relations(owner.Value);
            if (!relations.TryGetValue(friend.Value, out var relation)) return ValueTask.FromResult(false);
            relations[friend.Value] = relation with { Blocked = blocked };
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<MessengerResult> SetFriendCategoryAsync(MessengerUserId owner, MessengerUserId friend, int expected, int target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var relations = Relations(owner.Value);
            if (!relations.TryGetValue(friend.Value, out var relation) || relation.Category != expected ||
                target != 0 && !Categories(owner.Value).ContainsKey(target))
                return ValueTask.FromResult(MessengerResult.NotFound);
            relations[friend.Value] = relation with { Category = target };
            return ValueTask.FromResult(MessengerResult.Ok);
        }
    }

    /// <inheritdoc />
    public ValueTask<MessengerCategoryOutcome> AddCategoryAsync(
        MessengerUserId owner, string name, uint gameCode, MessengerLimits limits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var categories = Categories(owner.Value);
            if (categories.Count >= limits.MaxCategories) return ValueTask.FromResult(new MessengerCategoryOutcome(MessengerResult.LimitReached, null));
            var id = 1;
            while (categories.ContainsKey(id)) id++;
            var category = new MessengerCategory(id, name, gameCode);
            categories.Add(id, category);
            return ValueTask.FromResult(new MessengerCategoryOutcome(MessengerResult.Ok, category));
        }
    }

    /// <inheritdoc />
    public ValueTask<MessengerResult> RenameCategoryAsync(MessengerUserId owner, int categoryId, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var categories = Categories(owner.Value);
            if (!categories.TryGetValue(categoryId, out var category)) return ValueTask.FromResult(MessengerResult.NotFound);
            categories[categoryId] = category with { Name = name };
            return ValueTask.FromResult(MessengerResult.Ok);
        }
    }

    /// <inheritdoc />
    public ValueTask<MessengerResult> DeleteCategoryAsync(MessengerUserId owner, int categoryId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var categories = Categories(owner.Value);
            if (!categories.ContainsKey(categoryId)) return ValueTask.FromResult(MessengerResult.NotFound);
            if (Relations(owner.Value).Values.Any(r => r.Category == categoryId)) return ValueTask.FromResult(MessengerResult.NotEmpty);
            categories.Remove(categoryId);
            return ValueTask.FromResult(MessengerResult.Ok);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> PruneRequestsAsync(DateTime cutoff, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var stale = _requests.Values.Where(r => r.CreatedAt <= cutoff).Select(r => r.Handle).ToArray();
            foreach (var handle in stale) _requests.Remove(handle);
            return ValueTask.FromResult(stale.Length);
        }
    }

    private Dictionary<long, Relation> Relations(long owner) =>
        _relations.TryGetValue(owner, out var relations) ? relations : _relations[owner] = [];

    private SortedDictionary<int, MessengerCategory> Categories(long owner) =>
        _categories.TryGetValue(owner, out var categories) ? categories : _categories[owner] = [];

    private static ValueTask<MessengerRequestOutcome> Result(MessengerResult result, MessengerStoredRequest? request) =>
        ValueTask.FromResult(new MessengerRequestOutcome(result, request));
}
