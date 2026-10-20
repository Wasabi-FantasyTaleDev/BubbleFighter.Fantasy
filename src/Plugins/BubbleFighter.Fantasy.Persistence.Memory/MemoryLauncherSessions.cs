using BubbleFighter.Fantasy.Contracts.Launcher;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>Bounded, process-local launcher refresh sessions for development profiles; lost on restart.</summary>
public sealed class MemoryLauncherSessions(int maximumSessions) : ILauncherSessionStore
{
    private sealed record Row(string Hash, LauncherRefreshSession Session, DateTime CreatedAt);
    private readonly Dictionary<string, Row> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _idByHash = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <inheritdoc />
    public ValueTask CreateAsync(string sessionId, string username, string tokenHash, DateTime expiresAt, DateTime now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // Ended sessions are dropped before the bound is checked, so only live sign-ins count against it.
            foreach (var ended in _byId.Values.Where(r => r.Session.RevokedAt is not null || r.Session.ExpiresAt <= now).ToArray())
            {
                _byId.Remove(ended.Session.SessionId);
                _idByHash.Remove(ended.Hash);
            }
            if (_byId.Count >= maximumSessions) throw new InvalidOperationException("Launcher session storage is full.");
            _byId.Add(sessionId, new(tokenHash, new(sessionId, username, expiresAt, null), now));
            _idByHash.Add(tokenHash, sessionId);
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<LauncherRefreshSession?> FindAsync(string tokenHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return ValueTask.FromResult(_idByHash.TryGetValue(tokenHash, out var id) ? _byId[id].Session : null);
    }

    /// <inheritdoc />
    public ValueTask<bool> TryRotateAsync(string sessionId, string currentHash, string nextHash, DateTime nextExpiresAt, DateTime now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_byId.TryGetValue(sessionId, out var row) || row.Hash != currentHash || row.Session.RevokedAt is not null
                || row.Session.ExpiresAt <= now)
                return ValueTask.FromResult(false);
            _idByHash.Remove(currentHash);
            _idByHash.Add(nextHash, sessionId);
            _byId[sessionId] = new(nextHash, row.Session with { ExpiresAt = nextExpiresAt }, row.CreatedAt);
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask RevokeAsync(string sessionId, DateTime now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            if (_byId.TryGetValue(sessionId, out var row) && row.Session.RevokedAt is null)
                _byId[sessionId] = row with { Session = row.Session with { RevokedAt = now } };
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> CreateLimitedAsync(string sessionId, string username, string tokenHash,
        DateTime expiresAt, DateTime now, int maximumPerAccount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPerAccount);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_byId.ContainsKey(sessionId) || _idByHash.ContainsKey(tokenHash))
                throw new InvalidOperationException("Launcher session identity already exists.");
            // Ended sessions, like the oldest sign-ins over the account cap, do not count against the storage bound.
            var ended = _byId.Values.Where(row => row.Session.RevokedAt is not null || row.Session.ExpiresAt <= now).ToArray();
            var account = _byId.Values.Except(ended).Where(row => row.Session.Username == username)
                .OrderBy(row => row.CreatedAt).ThenBy(row => row.Session.SessionId, StringComparer.Ordinal).ToArray();
            var remove = account.Take(Math.Max(0, account.Length - maximumPerAccount + 1)).ToArray();
            if (_byId.Count - remove.Length - ended.Length >= maximumSessions)
                throw new InvalidOperationException("Launcher session storage is full.");
            foreach (var row in ended.Concat(remove).ToArray())
            {
                _byId.Remove(row.Session.SessionId);
                _idByHash.Remove(row.Hash);
            }
            _byId.Add(sessionId, new(tokenHash, new(sessionId, username, expiresAt, null), now));
            _idByHash.Add(tokenHash, sessionId);
            return ValueTask.FromResult<IReadOnlyList<string>>(account.Skip(remove.Length)
                .Select(row => row.Session.SessionId).Append(sessionId).ToArray());
        }
    }

    /// <inheritdoc />
    public ValueTask<int> PruneExpiredAsync(DateTime now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var expired = _byId.Values.Where(row => row.Session.ExpiresAt <= now).ToArray();
            foreach (var row in expired)
            {
                _byId.Remove(row.Session.SessionId);
                _idByHash.Remove(row.Hash);
            }
            return ValueTask.FromResult(expired.Length);
        }
    }
}
