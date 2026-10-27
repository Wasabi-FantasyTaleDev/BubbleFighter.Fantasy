using System.Security.Cryptography;
using BubbleFighter.Fantasy.Contracts.Messenger;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Messenger;

/// <summary>
/// The messenger core. Durable relations live in <see cref="IMessengerStore"/>; sessions, presence, the bounded
/// per-account event queues, used ticket ids and mutation request keys are process-local and bounded by
/// <see cref="MessengerOptions"/>. Relations never cross tenants: lookups are tenant-scoped and every
/// friendship starts from a same-tenant request.
/// </summary>
internal sealed class MessengerService : IMessengerService
{
    private const int MaxRequestKeyLength = 64;
    private const int MaxSubjectLength = 128;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PrunePeriod = TimeSpan.FromHours(1);

    private readonly MessengerOptions _options;
    private readonly IMessengerStore _store;
    private readonly IReadOnlyDictionary<string, ECDsa> _tenants;
    private readonly TimeProvider _clock;
    private readonly ILogger? _logger;
    private readonly MessengerLimits _limits;
    private readonly MessengerGameCatalog _catalog;
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Account> _accounts = [];
    private readonly Dictionary<string, long> _usedTickets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset At, Task<object> Result)> _requestKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _requestKeyOrder = new();
    private readonly Dictionary<int, Chat> _chats = [];
    private readonly Dictionary<(long, long), int> _chatByPair = [];
    private int _lastChatId;
    private DateTimeOffset _lastPrune;

    private sealed class Session(string token, MessengerUser user, uint gameCode, DateTimeOffset now)
    {
        public string Token { get; } = token;
        public MessengerUser User { get; set; } = user;
        public uint GameCode { get; } = gameCode;
        public DateTimeOffset Created { get; } = now;
        public DateTimeOffset ExpiresAt { get; set; }
        public DateTimeOffset LastSeen { get; set; } = now;
        public bool Online { get; set; }
    }

    private sealed class Account(MessengerUser user)
    {
        public MessengerUser User { get; set; } = user;
        public List<Session> Sessions { get; } = [];
        public int Level { get; set; }
        public bool VisibleOnline { get; set; }
        public long NextSequence { get; set; } = 1;
        public long DroppedThrough { get; set; }
        public LinkedList<MessengerEvent> Events { get; } = new();
        public TaskCompletionSource Signal { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // A chat between two accounts; Open holds the members whose client shows its window.
    private sealed class Chat(int id, MessengerUser first, MessengerUser second)
    {
        public int Id { get; } = id;
        public MessengerUser[] Members { get; } = [first, second];
        public HashSet<long> Open { get; } = [];
        public (long, long) Pair => PairOf(Members[0].Id, Members[1].Id);
    }

    internal MessengerService(MessengerOptions options, IMessengerStore store, IReadOnlyDictionary<string, ECDsa> tenants, TimeProvider? clock = null, ILogger? logger = null)
    {
        _options = options;
        _store = store;
        _tenants = tenants;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
        _limits = new(options.MaxFriends, options.MaxCategories, options.MaxPendingRequests);
        _catalog = new(options.Games);
        _lastPrune = _clock.GetUtcNow();
    }

    /// <inheritdoc />
    public async ValueTask<MessengerSession?> LoginAuthAsync(string ticket, uint? gameCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var claims = MessengerTicketCodec.Verify(ticket, KeyFor);
        var now = _clock.GetUtcNow();
        if (claims is null || claims.ExpiresAt <= now.ToUnixTimeSeconds() || DateTimeOffset.FromUnixTimeSeconds(claims.IssuedAt) > now + ClockSkew ||
            claims.Subject.Length is 0 or > MaxSubjectLength || !ValidText(claims.Nickname, 1, _options.MaxNickname) || claims.Id.Length is 0 or > 64)
            return null;
        // The announced code only chooses how records are shown; it never changes whose account the ticket names.
        var shown = gameCode ?? claims.GameCode;
        if (_catalog.Canonical(shown) != _catalog.Canonical(claims.GameCode))
        {
            _logger?.LogWarning("Messenger sign-in refused: client game code {Code:X8} is not an alias of ticket game {Ticket:X8} (build {Build}).",
                shown, claims.GameCode, claims.Build);
            return null;
        }
        lock (_gate)
        {
            foreach (var expired in _usedTickets.Where(p => p.Value <= now.ToUnixTimeSeconds()).Select(p => p.Key).ToArray()) _usedTickets.Remove(expired);
            if (_usedTickets.ContainsKey(claims.Id)) return null;
            if (_usedTickets.Count >= _options.MaxRememberedTickets)
            {
                _logger?.LogWarning("Messenger ticket replay memory is full; sign-in refused until older tickets expire.");
                return null;
            }
            _usedTickets.Add(claims.Id, claims.ExpiresAt);
        }
        var user = await _store.UpsertUserAsync(new(claims.Tenant, claims.Subject, claims.Nickname, _catalog.Canonical(claims.GameCode)), cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            DropExpiredSessions(now);
            if (_accounts.TryGetValue(user.Id.Value, out var account))
            {
                account.User = user;
                foreach (var other in account.Sessions) other.User = user;
                // Keep the account alive while making room: the newest session replaces the oldest.
                while (account.Sessions.Count >= _options.MaxSessionsPerAccount)
                {
                    var oldest = account.Sessions.MinBy(s => s.Created)!;
                    _sessions.Remove(oldest.Token);
                    account.Sessions.Remove(oldest);
                }
            }
            if (_sessions.Count >= _options.MaxSessions) throw new InvalidOperationException("Messenger session capacity reached.");
            if (account is null) _accounts.Add(user.Id.Value, account = new(user));
            var session = new Session(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'), user, shown, now)
            {
                ExpiresAt = now.AddSeconds(_options.SessionSeconds),
            };
            _sessions.Add(session.Token, session);
            account.Sessions.Add(session);
            return new(session.Token, Show(user, shown), session.ExpiresAt, shown);
        }
    }

    /// <inheritdoc />
    public async ValueTask LogoutAuthAsync(string session, CancellationToken cancellationToken)
    {
        MessengerUser? user = null;
        var changed = false;
        lock (_gate)
        {
            if (_sessions.TryGetValue(session ?? "", out var current))
            {
                user = current.User;
                var account = _accounts[user.Id.Value];
                RemoveSession(current);
                changed = !_accounts.ContainsKey(user.Id.Value) ? account.VisibleOnline : RefreshPresence(account, _clock.GetUtcNow());
            }
        }
        if (changed) await NotifyPresenceAsync(user!, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask LoginMessengerAsync(string session, CancellationToken cancellationToken)
    {
        var (current, _) = await AuthorizeAsync(session, s => s.Online = true, cancellationToken).ConfigureAwait(false);
        foreach (var request in await _store.ListIncomingRequestsAsync(current.User.Id, cancellationToken).ConfigureAwait(false))
        {
            var from = await _store.FindUserAsync(request.From, cancellationToken).ConfigureAwait(false);
            if (from is not null) Enqueue(current.User.Id, sequence => new MessengerFriendRequestEvent(sequence, ToRequest(request, from, current.User)));
        }
    }

    /// <inheritdoc />
    public async ValueTask LogoutMessengerAsync(string session, CancellationToken cancellationToken) =>
        await AuthorizeAsync(session, s => s.Online = false, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask ChangeMyLevelAsync(string session, int level, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 1_000_000);
        var (current, _) = await AuthorizeAsync(session, null, cancellationToken).ConfigureAwait(false);
        bool notify;
        lock (_gate)
        {
            var account = _accounts[current.User.Id.Value];
            notify = account.Level != level && account.VisibleOnline;
            account.Level = level;
        }
        if (notify) await NotifyPresenceAsync(current.User, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<MessengerFriendListPage> GetFriendListAsync(string session, int page, CancellationToken cancellationToken)
    {
        var (current, _) = await AuthorizeAsync(session, null, cancellationToken).ConfigureAwait(false);
        var relations = await _store.LoadRelationsAsync(current.User.Id, cancellationToken).ConfigureAwait(false);
        var total = relations.Relations.Count;
        var pages = Math.Max(1, (total + _options.PageSize - 1) / _options.PageSize);
        if (page < 0 || page >= pages) throw new ArgumentOutOfRangeException(nameof(page), "The page is outside the friend list.");
        MessengerFriend[] friends;
        lock (_gate)
            friends = relations.Relations.Skip(page * _options.PageSize).Take(_options.PageSize)
                .Select(r => new MessengerFriend(Show(r.Friend, current.GameCode), r.CategoryId, r.Blocked,
                    r.BlockedByFriend ? Hidden(r.Friend.Id) : PresenceOf(r.Friend.Id))).ToArray();
        return new(relations.Categories.Select(c => Show(c, current.GameCode)).ToArray(), friends, page, pages, total);
    }

    /// <inheritdoc />
    public async ValueTask<MessengerResult> RequestNewFriendAsync(string session, string? requestKey, string nickname, string message, CancellationToken cancellationToken)
    {
        if (!ValidText(nickname, 1, _options.MaxNickname) || !ValidText(message, 0, _options.MaxRequestMessage)) return MessengerResult.Invalid;
        var (current, _) = await AuthorizeAsync(session, null, cancellationToken).ConfigureAwait(false);
        return await ReplayAsync(current.User, "request", requestKey, async () =>
        {
            var target = await _store.FindUserByNicknameAsync(current.User.Tenant, nickname, cancellationToken).ConfigureAwait(false);
            if (target is null) return MessengerResult.NotFound;
            if (target.Id == current.User.Id) return MessengerResult.Invalid;
            if (target.Tenant != current.User.Tenant) return MessengerResult.CrossTenant;
            var (result, request) = await _store.CreateRequestAsync(current.User.Id, target.Id, message, _clock.GetUtcNow().UtcDateTime, _limits, cancellationToken).ConfigureAwait(false);
            if (result == MessengerResult.Ok)
                Enqueue(target.Id, sequence => new MessengerFriendRequestEvent(sequence, ToRequest(request!, current.User, target)));
            return result;
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<MessengerResult> ConfirmNewFriendAsync(string session, string? requestKey, int handle, MessengerFriendDecision decision, CancellationToken cancellationToken)
    {
        if (handle <= 0 || !Enum.IsDefined(decision)) return MessengerResult.Invalid;
        var (current, _) = await AuthorizeAsync(session, null, cancellationToken).ConfigureAwait(false);
        return await ReplayAsync(current.User, "confirm", requestKey, async () =>
        {
            if (decision == MessengerFriendDecision.Dismiss)
                return (await _store.ListIncomingRequestsAsync(current.User.Id, cancellationToken).ConfigureAwait(false)).Any(r => r.Handle == handle)
                    ? MessengerResult.Ok : MessengerResult.NotFound;
            var accept = decision == MessengerFriendDecision.Accept;
            var (result, request) = await _store.ResolveRequestAsync(handle, current.User.Id, accept, _limits, cancellationToken).ConfigureAwait(false);
            if (result == MessengerResult.Ok && accept)
                foreach (var id in new[] { request!.From, request.To }) Enqueue(id, sequence => new MessengerFriendListChangedEvent(sequence));
            return result;
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<MessengerResult> BlockFriendAsync(string session, string? requestKey, MessengerUserId friend, bool blocked, CancellationToken cancellationToken)
    {
        var (current, _) = await AuthorizeAsync(session, null, cancellationToken).ConfigureAwait(false);
        return await ReplayAsync(current.User, "block", requestKey, async () =>
        {
            if (!await _store.SetBlockedAsync(current.User.Id, friend, blocked, cancellationToken).ConfigureAwait(false)) return MessengerResult.NotFound;
            Enqueue(current.User.Id, sequence => new MessengerFriendListChangedEvent(sequence));
            MessengerPresence visible;
            lock (_gate) visible = blocked ? Hidden(current.User.Id) : PresenceOf(current.User.Id);
            Enqueue(friend, sequence => new MessengerPresenceChangedEvent(sequence, current.User.Id, visible));
            return MessengerResult.Ok;
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<MessengerResult> AddFriendToCategoryAsync(string session, string? requestKey, MessengerUserId friend, int categoryId, CancellationToken cancellationToken)
    {
        if (categoryId <= 0) return MessengerResult.Invalid;
        return await MoveAsync(session, requestKey, "category-add", friend, 0, categoryId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<MessengerResult> MoveFriendCategoryAsync(string session, string? requestKey, MessengerUserId friend, int fromCategoryId, int toCategoryId, CancellationToken cancellationToken)
    {
        if (fromCategoryId < 0 || toCategoryId < 0) return MessengerResult.Invalid;
        return await MoveAsync(session, requestKey, "category-move", friend, fromCategoryId, toCategoryId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<MessengerResult> DeleteFriendFromCategoryAsync(string session, string? requestKey, MessengerUserId friend, int categoryId, CancellationToken cancellationToken)
    {
        if (categoryId < 0) return MessengerResult.Invalid;
        var (current, _) = await AuthorizeAsync(session, null, cancellationToken).ConfigureAwait(false);
        return await ReplayAsync(current.User, "delete", requestKey, async () =>
        {
            var relations = await _store.LoadRelationsAsync(current.User.Id, cancellationToken).ConfigureAwait(false);
            if (!relations.Relations.Any(r => r.Friend.Id == friend && r.CategoryId == categoryId)) return MessengerResult.NotFound;
            if (!await _store.DeleteFriendshipAsync(current.User.Id, friend, cancellationToken).ConfigureAwait(false)) return MessengerResult.NotFound;
            foreach (var id in new[] { current.User.Id, friend }) Enqueue(id, sequence => new MessengerFriendListChangedEvent(sequence));
            return MessengerResult.Ok;
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<MessengerCategoryOutcome> AddCategoryAsync(string session, string? requestKey, string name, CancellationToken cancellationToken)
    {
        if (!ValidText(name, 1, _options.MaxCategoryName) || string.IsNullOrWhiteSpace(name)) return new MessengerCategoryOutcome(MessengerResult.Invalid, null);
        var (current, _) = await AuthorizeAsync(session, null, cancellationToken).ConfigureAwait(false);
        return await ReplayAsync(current.User, "category-create", requestKey, async () =>
        {
            var outcome = await _store.AddCategoryAsync(current.User.Id, name, _catalog.Canonical(current.GameCode), _limits, cancellationToken).ConfigureAwait(false);
            if (outcome.Result == MessengerResult.Ok) Enqueue(current.User.Id, sequence => new MessengerFriendListChangedEvent(sequence));
            return outcome.Category is null ? outcome : outcome with { Category = Show(outcome.Category, current.GameCode) };
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<MessengerResult> DeleteCategoryAsync(string session, string? requestKey, int categoryId, CancellationToken cancellationToken)
    {
        if (categoryId <= 0) return MessengerResult.Invalid;
        var (current, _) = await AuthorizeAsync(session, null, cancellationToken).ConfigureAwait(false);
        return await ReplayAsync(current.User, "category-delete", requestKey, async () =>
        {
            var result = await _store.DeleteCategoryAsync(current.User.Id, categoryId, cancellationToken).ConfigureAwait(false);
            if (result == MessengerResult.Ok) Enqueue(current.User.Id, sequence => new MessengerFriendListChangedEvent(sequence));
            return result;
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<MessengerResult> ChangeCategoryNameAsync(string session, string? requestKey, int categoryId, string name, CancellationToken cancellationToken)
    {
        if (categoryId <= 0 || !ValidText(name, 1, _options.MaxCategoryName) || string.IsNullOrWhiteSpace(name)) return MessengerResult.Invalid;
        var (current, _) = await AuthorizeAsync(session, null, cancellationToken).ConfigureAwait(false);
        return await ReplayAsync(current.User, "category-rename", requestKey, async () =>
        {
            var result = await _store.RenameCategoryAsync(current.User.Id, categoryId, name, cancellationToken).ConfigureAwait(false);
            if (result == MessengerResult.Ok) Enqueue(current.User.Id, sequence => new MessengerFriendListChangedEvent(sequence));
            return result;
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MessengerGame>> GetSupportGameListAsync(string session, CancellationToken cancellationToken)
    {
        var (current, _) = await AuthorizeAsync(session, null, cancellationToken).ConfigureAwait(false);
        return _catalog.Games.Select(g => g with { Code = _catalog.Present(g.Code, current.GameCode) }).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<MessengerGame?> FindGameAsync(string session, uint code, CancellationToken cancellationToken)
    {
        var (current, _) = await AuthorizeAsync(session, null, cancellationToken).ConfigureAwait(false);
        var game = _catalog.Find(code);
        return game is null ? null : game with { Code = _catalog.Present(game.Code, current.GameCode) };
    }

    /// <inheritdoc />
    public async ValueTask<MessengerChatOutcome> RequestChatSessionAsync(string session, MessengerUserId friend, CancellationToken cancellationToken)
    {
        var (current, _) = await AuthorizeAsync(session, null, cancellationToken).ConfigureAwait(false);
        if (friend == current.User.Id) return new(MessengerResult.Invalid, null);
        var relation = (await _store.LoadRelationsAsync(current.User.Id, cancellationToken).ConfigureAwait(false)).Relations.FirstOrDefault(r => r.Friend.Id == friend);
        if (relation is null) return new(MessengerResult.NotFound, null);
        if (relation.Blocked || relation.BlockedByFriend) return new(MessengerResult.Refused, null);
        lock (_gate)
        {
            var pair = PairOf(current.User.Id, friend);
            if (!_chatByPair.TryGetValue(pair, out var id))
            {
                if (_chats.Count >= _options.MaxChats) throw new InvalidOperationException("Messenger chat capacity reached.");
                do id = _lastChatId = _lastChatId == int.MaxValue ? 1 : _lastChatId + 1;
                while (_chats.ContainsKey(id));
                _chats.Add(id, new Chat(id, current.User, relation.Friend));
                _chatByPair.Add(pair, id);
            }
            var chat = _chats[id];
            chat.Members[Array.FindIndex(chat.Members, m => m.Id == current.User.Id)] = current.User;
            chat.Open.Add(current.User.Id.Value);
            var view = ToChat(chat);
            Enqueue(current.User.Id, sequence => new MessengerChatOpenedEvent(sequence, view));
            return new(MessengerResult.Ok, Show(view, current.GameCode));
        }
    }

    /// <inheritdoc />
    public async ValueTask<MessengerChat?> GetChatSessionAsync(string session, int chatId, CancellationToken cancellationToken)
    {
        var (current, _) = await AuthorizeAsync(session, null, cancellationToken).ConfigureAwait(false);
        lock (_gate) return MemberChat(chatId, current.User.Id) is { } chat ? Show(ToChat(chat), current.GameCode) : null;
    }

    /// <inheritdoc />
    public async ValueTask<MessengerResult> SendChatMessageAsync(string session, int chatId, string text, CancellationToken cancellationToken)
    {
        if (!ValidText(text, 1, _options.MaxChatMessage) || string.IsNullOrWhiteSpace(text)) return MessengerResult.Invalid;
        var (current, _) = await AuthorizeAsync(session, null, cancellationToken).ConfigureAwait(false);
        MessengerUserId other;
        lock (_gate)
        {
            if (MemberChat(chatId, current.User.Id) is not { } found) return MessengerResult.NotFound;
            other = found.Members.First(m => m.Id != current.User.Id).Id;
        }
        // A block placed after the chat opened ends it for new lines.
        var relation = (await _store.LoadRelationsAsync(current.User.Id, cancellationToken).ConfigureAwait(false)).Relations.FirstOrDefault(r => r.Friend.Id == other);
        if (relation is null) return MessengerResult.NotFound;
        if (relation.Blocked || relation.BlockedByFriend) return MessengerResult.Refused;
        lock (_gate)
        {
            if (MemberChat(chatId, current.User.Id) is not { } chat) return MessengerResult.NotFound;
            var view = ToChat(chat);
            foreach (var member in chat.Members)
            {
                if (!_accounts.ContainsKey(member.Id.Value)) continue;
                if (chat.Open.Add(member.Id.Value)) Enqueue(member.Id, sequence => new MessengerChatOpenedEvent(sequence, view));
                Enqueue(member.Id, sequence => new MessengerChatMessageEvent(sequence, chat.Id, current.User, text));
            }
        }
        return MessengerResult.Ok;
    }

    /// <inheritdoc />
    public async ValueTask<MessengerResult> CloseChatSessionAsync(string session, int chatId, CancellationToken cancellationToken)
    {
        var (current, _) = await AuthorizeAsync(session, null, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (MemberChat(chatId, current.User.Id) is not { } chat) return MessengerResult.NotFound;
            chat.Open.Remove(current.User.Id.Value);
            if (chat.Open.Count == 0) EndChat(chat);
            return MessengerResult.Ok;
        }
    }

    /// <inheritdoc />
    public async ValueTask<MessengerEventBatch> WaitEventsAsync(string session, long after, TimeSpan wait, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(after);
        var (current, _) = await AuthorizeAsync(session, null, cancellationToken).ConfigureAwait(false);
        var deadline = _clock.GetUtcNow() + wait;
        while (true)
        {
            Task signal;
            lock (_gate)
            {
                if (!_accounts.TryGetValue(current.User.Id.Value, out var account)) throw new UnauthorizedAccessException();
                var last = account.NextSequence - 1;
                // A cursor beyond the last issued number comes from an earlier process; the client must refetch.
                if (after > last || after < account.DroppedThrough) return new(account.Events.Select(e => Show(e, current.GameCode)).ToArray(), last, true);
                var pending = account.Events.Where(e => e.Sequence > after).Select(e => Show(e, current.GameCode)).ToArray();
                var remaining = deadline - _clock.GetUtcNow();
                if (pending.Length > 0 || remaining <= TimeSpan.Zero) return new(pending, pending.Length > 0 ? pending[^1].Sequence : after, false);
                signal = account.Signal.Task.WaitAsync(remaining, _clock, cancellationToken);
            }
            try { await signal.ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
    }

    /// <summary>Ends idle sessions, recomputes presence, forgets expired keys and prunes stale requests.</summary>
    internal async ValueTask SweepAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var changed = new List<MessengerUser>();
        lock (_gate)
        {
            foreach (var account in _accounts.Values.ToArray())
            {
                var wasOnline = account.VisibleOnline;
                foreach (var session in account.Sessions.Where(s => s.ExpiresAt <= now).ToArray()) RemoveSession(session);
                if (!_accounts.ContainsKey(account.User.Id.Value) ? wasOnline : RefreshPresence(account, now)) changed.Add(account.User);
            }
            foreach (var expired in _usedTickets.Where(p => p.Value <= now.ToUnixTimeSeconds()).Select(p => p.Key).ToArray()) _usedTickets.Remove(expired);
            ForgetRequestKeys(now);
        }
        foreach (var user in changed) await NotifyPresenceAsync(user, cancellationToken).ConfigureAwait(false);
        if (now - _lastPrune >= PrunePeriod)
        {
            _lastPrune = now;
            var removed = await _store.PruneRequestsAsync(now.UtcDateTime.AddHours(-_options.RequestHours), cancellationToken).ConfigureAwait(false);
            if (removed > 0) _logger?.LogInformation("Pruned {Count} expired messenger friend requests.", removed);
        }
    }

    private ECDsa? KeyFor(string tenant) => _tenants.GetValueOrDefault(tenant);

    private async ValueTask<MessengerResult> MoveAsync(string session, string? requestKey, string operation, MessengerUserId friend, int from, int to, CancellationToken cancellationToken)
    {
        var (current, _) = await AuthorizeAsync(session, null, cancellationToken).ConfigureAwait(false);
        return await ReplayAsync(current.User, operation, requestKey, async () =>
        {
            var result = await _store.SetFriendCategoryAsync(current.User.Id, friend, from, to, cancellationToken).ConfigureAwait(false);
            if (result == MessengerResult.Ok) Enqueue(current.User.Id, sequence => new MessengerFriendListChangedEvent(sequence));
            return result;
        }).ConfigureAwait(false);
    }

    private async ValueTask<(Session Session, bool PresenceChanged)> AuthorizeAsync(string token, Action<Session>? change, CancellationToken cancellationToken)
    {
        Session session;
        bool changed;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (token is null || !_sessions.TryGetValue(token, out var found)) throw new UnauthorizedAccessException();
            if (found.ExpiresAt <= now)
            {
                RemoveSession(found);
                throw new UnauthorizedAccessException();
            }
            session = found;
            session.ExpiresAt = now.AddSeconds(_options.SessionSeconds);
            session.LastSeen = now;
            change?.Invoke(session);
            changed = RefreshPresence(_accounts[session.User.Id.Value], now);
        }
        if (changed) await NotifyPresenceAsync(session.User, cancellationToken).ConfigureAwait(false);
        return (session, changed);
    }

    private async ValueTask<T> ReplayAsync<T>(MessengerUser user, string operation, string? requestKey, Func<Task<T>> run)
    {
        if (requestKey is null) return await run().ConfigureAwait(false);
        if (requestKey.Length is 0 or > MaxRequestKeyLength || requestKey.Any(c => c is < '!' or > '~'))
            throw new ArgumentException("Request keys are 1-64 printable ASCII characters.", nameof(requestKey));
        var key = $"{user.Id.Value}:{operation}:{requestKey}";
        TaskCompletionSource<object>? owned = null;
        Task<object>? known = null;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            ForgetRequestKeys(now);
            if (_requestKeys.TryGetValue(key, out var entry)) known = entry.Result;
            else
            {
                if (_requestKeys.Count >= _options.MaxRequestKeys) throw new InvalidOperationException("Messenger request key capacity reached.");
                owned = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _requestKeys.Add(key, (now, owned.Task));
                _requestKeyOrder.Enqueue(key);
            }
        }
        if (known is not null) return (T)await known.ConfigureAwait(false);
        try
        {
            var result = await run().ConfigureAwait(false);
            owned!.SetResult(result!);
            return result;
        }
        catch (Exception e)
        {
            // A failed attempt is not remembered, so the caller may retry with the same key.
            lock (_gate) _requestKeys.Remove(key);
            owned!.SetException(e);
            throw;
        }
    }

    private void ForgetRequestKeys(DateTimeOffset now)
    {
        while (_requestKeyOrder.TryPeek(out var oldest) &&
               (!_requestKeys.TryGetValue(oldest, out var entry) || now - entry.At >= TimeSpan.FromSeconds(_options.RequestKeySeconds)))
        {
            _requestKeyOrder.Dequeue();
            if (_requestKeys.TryGetValue(oldest, out var stale) && now - stale.At >= TimeSpan.FromSeconds(_options.RequestKeySeconds)) _requestKeys.Remove(oldest);
        }
    }

    private void DropExpiredSessions(DateTimeOffset now)
    {
        foreach (var expired in _sessions.Values.Where(s => s.ExpiresAt <= now).ToArray()) RemoveSession(expired);
    }

    private void RemoveSession(Session session)
    {
        _sessions.Remove(session.Token);
        if (!_accounts.TryGetValue(session.User.Id.Value, out var account)) return;
        account.Sessions.Remove(session);
        if (account.Sessions.Count == 0)
        {
            _accounts.Remove(session.User.Id.Value);
            account.Signal.TrySetResult();
            // A signed-out account has no window; its chats end once nobody shows them.
            foreach (var chat in _chats.Values.Where(c => c.Open.Remove(session.User.Id.Value) && c.Open.Count == 0).ToArray()) EndChat(chat);
        }
    }

    private Chat? MemberChat(int chatId, MessengerUserId member) =>
        _chats.TryGetValue(chatId, out var chat) && chat.Members.Any(m => m.Id == member) ? chat : null;

    private void EndChat(Chat chat)
    {
        _chats.Remove(chat.Id);
        _chatByPair.Remove(chat.Pair);
    }

    private MessengerChat ToChat(Chat chat) => new(chat.Id, chat.Members.Select(m => new MessengerChatMember(m, PresenceOf(m.Id))).ToArray());

    private static (long, long) PairOf(MessengerUserId a, MessengerUserId b) => a.Value < b.Value ? (a.Value, b.Value) : (b.Value, a.Value);

    private bool RefreshPresence(Account account, DateTimeOffset now)
    {
        var online = account.Sessions.Any(s => s.Online && now - s.LastSeen < TimeSpan.FromSeconds(_options.PresenceTimeoutSeconds));
        if (online == account.VisibleOnline) return false;
        account.VisibleOnline = online;
        return true;
    }

    private MessengerPresence PresenceOf(MessengerUserId id) =>
        _accounts.TryGetValue(id.Value, out var account) && account.VisibleOnline
            ? new(MessengerPresenceStatus.Online, account.Level)
            : Hidden(id);

    private MessengerPresence Hidden(MessengerUserId id) =>
        new(MessengerPresenceStatus.Offline, _accounts.TryGetValue(id.Value, out var account) ? account.Level : 0);

    private async ValueTask NotifyPresenceAsync(MessengerUser user, CancellationToken cancellationToken)
    {
        var relations = await _store.LoadRelationsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        foreach (var relation in relations.Relations)
        {
            MessengerPresence visible;
            lock (_gate) visible = relation.Blocked ? Hidden(user.Id) : PresenceOf(user.Id);
            Enqueue(relation.Friend.Id, sequence => new MessengerPresenceChangedEvent(sequence, user.Id, visible));
        }
    }

    // Events go only to accounts with a live session; an offline account rebuilds its state at sign-in.
    private void Enqueue(MessengerUserId target, Func<long, MessengerEvent> create)
    {
        lock (_gate)
        {
            if (!_accounts.TryGetValue(target.Value, out var account)) return;
            account.Events.AddLast(create(account.NextSequence++));
            while (account.Events.Count > _options.EventQueue)
            {
                account.DroppedThrough = account.Events.First!.Value.Sequence;
                account.Events.RemoveFirst();
            }
            var signal = account.Signal;
            account.Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.TrySetResult();
        }
    }

    // Events are queued once per account and read by sessions that may show different codes.
    private MessengerEvent Show(MessengerEvent e, uint viewer) => e switch
    {
        MessengerFriendRequestEvent request => request with { Request = request.Request with { From = Show(request.Request.From, viewer), To = Show(request.Request.To, viewer) } },
        MessengerChatOpenedEvent opened => opened with { Chat = Show(opened.Chat, viewer) },
        MessengerChatMessageEvent line => line with { From = Show(line.From, viewer) },
        _ => e,
    };

    private MessengerChat Show(MessengerChat chat, uint viewer) => chat with { Members = chat.Members.Select(m => m with { User = Show(m.User, viewer) }).ToArray() };

    private MessengerUser Show(MessengerUser user, uint viewer) => user with { GameCode = _catalog.Present(user.GameCode, viewer) };

    private MessengerCategory Show(MessengerCategory category, uint viewer) => category with { GameCode = _catalog.Present(category.GameCode, viewer) };

    private static MessengerFriendRequest ToRequest(MessengerStoredRequest request, MessengerUser from, MessengerUser to) =>
        new(request.Handle, from, to, request.Message, new DateTimeOffset(DateTime.SpecifyKind(request.CreatedAt, DateTimeKind.Utc)));

    private static bool ValidText(string? value, int minimum, int maximum) =>
        value is not null && value.Length >= minimum && value.Length <= maximum && !value.Any(char.IsControl);
}
