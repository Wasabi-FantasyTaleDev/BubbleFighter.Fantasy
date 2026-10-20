using System.Security.Cryptography;
using System.Text;
using BubbleFighter.Fantasy.Contracts.Launcher;

namespace BubbleFighter.Fantasy.Launcher.Http;

/// <summary>A signed-in launcher's credentials: a short access bearer and the durable refresh credential that renews it.</summary>
/// <param name="AccessToken">Opaque bearer for API requests, held only in this process.</param>
/// <param name="AccessExpiresAt">When the bearer stops being accepted.</param>
/// <param name="RefreshToken">Opaque, single-use renewal credential; only its hash is stored.</param>
/// <param name="RefreshExpiresAt">When the renewal credential stops being accepted.</param>
/// <param name="Username">The account the sign-in belongs to.</param>
public sealed record LauncherGrant(string AccessToken, DateTimeOffset AccessExpiresAt, string RefreshToken, DateTimeOffset RefreshExpiresAt,
    string Username);

/// <summary>
/// Durable refresh sessions in an <see cref="ILauncherSessionStore"/>, with bounded in-process access bearers and
/// build-bound single-use launch tickets derived from them. A server restart drops bearers and tickets only; the
/// launcher renews its bearer from the stored session.
/// </summary>
public sealed class LauncherSessions(LauncherOptions options, ILauncherSessionStore store, TimeProvider? clock = null) : ILauncherTicketService
{
    private const int TokenBytes = 32;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private DateTimeOffset? _lastRefreshPrune;
    private readonly Dictionary<string, Entry> _access = new(StringComparer.Ordinal), _tickets = new(StringComparer.Ordinal);
    private sealed record Entry(string Username, string SessionId, string? Build, DateTimeOffset ExpiresAt);

    /// <summary>Starts a new durable session for <paramref name="username"/> and returns its first credentials.</summary>
    public ValueTask<LauncherGrant> SignInAsync(string username, CancellationToken ct) =>
        SerializeAsync(() => SignInCoreAsync(username, ct), ct);

    private async ValueTask<LauncherGrant> SignInCoreAsync(string username, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await PruneRefreshSessionsAsync(now, ct).ConfigureAwait(false);
        var sessionId = NewToken(); var refresh = NewToken(); var refreshExpiry = now.AddSeconds(options.RefreshSessionSeconds);
        lock (_gate)
        {
            Prune();
            // A capped account replaces an older bearer, even when the global bearer table is full.
            if (_access.Values.Count(entry => entry.Username == username) < options.MaxSessionsPerAccount)
                RequireRoom(_access);
        }
        var retained = await store.CreateLimitedAsync(sessionId, username, Key(refresh), refreshExpiry.UtcDateTime,
            now.UtcDateTime, options.MaxSessionsPerAccount, ct).ConfigureAwait(false);
        (string access, DateTimeOffset accessExpiry) credentials;
        lock (_gate)
        {
            foreach (var entries in new[] { _access, _tickets })
                foreach (var key in entries.Where(p => p.Value.Username == username && !retained.Contains(p.Value.SessionId))
                             .Select(p => p.Key).ToArray()) entries.Remove(key);
            credentials = AddAccess(username, sessionId, now);
        }
        var (access, accessExpiry) = credentials;
        return new(access, accessExpiry, refresh, refreshExpiry, username);
    }

    /// <summary>Prunes expired durable refresh sessions before accepting HTTP requests; repeated calls within an hour do nothing.</summary>
    public ValueTask InitializeAsync(CancellationToken ct) =>
        new(SerializeAsync(async () =>
        {
            await PruneRefreshSessionsAsync(_clock.GetUtcNow(), ct).ConfigureAwait(false);
            return true;
        }, ct).AsTask());

    private async ValueTask PruneRefreshSessionsAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_lastRefreshPrune is { } last && now - last < TimeSpan.FromHours(1)) return;
        await store.PruneExpiredAsync(now.UtcDateTime, ct).ConfigureAwait(false);
        _lastRefreshPrune = now;
    }

    /// <summary>
    /// Exchanges a live refresh credential for new credentials of the same session. The old credential stops working;
    /// of concurrent exchanges of one credential exactly one succeeds. Returns null for an unknown, expired, revoked,
    /// malformed or already exchanged credential.
    /// </summary>
    public ValueTask<LauncherGrant?> RefreshAsync(string refreshToken, CancellationToken ct) =>
        SerializeAsync(() => RefreshCoreAsync(refreshToken, ct), ct);

    private async ValueTask<LauncherGrant?> RefreshCoreAsync(string refreshToken, CancellationToken ct)
    {
        if (!WellFormed(refreshToken)) return null;
        var now = _clock.GetUtcNow(); var current = Key(refreshToken);
        var session = await store.FindAsync(current, ct).ConfigureAwait(false);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= now.UtcDateTime) return null;
        var next = NewToken(); var refreshExpiry = now.AddSeconds(options.RefreshSessionSeconds);
        if (!await store.TryRotateAsync(session.SessionId, current, Key(next), refreshExpiry.UtcDateTime, now.UtcDateTime, ct).ConfigureAwait(false))
            return null;
        lock (_gate)
            // Earlier bearers of the session are superseded by the new one.
            foreach (var key in _access.Where(p => p.Value.SessionId == session.SessionId).Select(p => p.Key).ToArray()) _access.Remove(key);
        var (access, accessExpiry) = AddAccess(session.Username, session.SessionId, now);
        return new(access, accessExpiry, next, refreshExpiry, session.Username);
    }

    /// <summary>Finds an unexpired bearer's account without extending its lifetime.</summary>
    public string? Account(string accessToken) => Find(accessToken)?.Username;

    /// <summary>
    /// Ends the session named by <paramref name="refreshToken"/>, or by <paramref name="accessToken"/> when the refresh
    /// credential is unknown, together with its bearers and pending tickets. Ending an ended session succeeds.
    /// </summary>
    /// <returns>False when both credentials are live but belong to different sessions; nothing is revoked then.</returns>
    public ValueTask<bool> SignOutAsync(string? refreshToken, string? accessToken, CancellationToken ct) =>
        SerializeAsync(() => SignOutCoreAsync(refreshToken, accessToken, ct), ct);

    private async ValueTask<bool> SignOutCoreAsync(string? refreshToken, string? accessToken, CancellationToken ct)
    {
        var bearer = accessToken is null ? null : Find(accessToken);
        var session = refreshToken is not null && WellFormed(refreshToken)
            ? await store.FindAsync(Key(refreshToken), ct).ConfigureAwait(false) : null;
        var now = _clock.GetUtcNow();
        var live = session is not null && session.RevokedAt is null && session.ExpiresAt > now.UtcDateTime;
        if (live && bearer is not null && bearer.SessionId != session!.SessionId) return false;
        var sessionId = session?.SessionId ?? bearer?.SessionId;
        if (sessionId is null) return true;
        await store.RevokeAsync(sessionId, now.UtcDateTime, ct).ConfigureAwait(false);
        lock (_gate)
            foreach (var entries in new[] { _access, _tickets })
                foreach (var key in entries.Where(p => p.Value.SessionId == sessionId).Select(p => p.Key).ToArray()) entries.Remove(key);
        return true;
    }

    /// <summary>Issues a ticket whose expiry cannot outlive the authorizing bearer.</summary>
    public (string Token, DateTimeOffset ExpiresAt) Issue(string accessToken, string build)
    {
        lock (_gate)
        {
            Prune();
            var bearer = _access.GetValueOrDefault(Key(accessToken)) ?? throw new UnauthorizedAccessException();
            var expiry = _clock.GetUtcNow().AddSeconds(options.TicketSeconds);
            return Add(_tickets, bearer with { Build = build, ExpiresAt = expiry < bearer.ExpiresAt ? expiry : bearer.ExpiresAt });
        }
    }

    /// <inheritdoc />
    public string? Redeem(string ticket, string build)
    {
        lock (_gate)
        {
            Prune(); var key = Key(ticket); var entry = _tickets.GetValueOrDefault(key);
            if (entry is null || entry.Build != build) return null;
            _tickets.Remove(key); return entry.Username;
        }
    }

    /// <summary>Whether <paramref name="token"/> has the shape of a credential this service issues: 64 lowercase hex digits.</summary>
    public static bool WellFormed(string token) =>
        token.Length == 2 * TokenBytes && token.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private Entry? Find(string accessToken)
    {
        lock (_gate) { Prune(); return _access.GetValueOrDefault(Key(accessToken)); }
    }

    private (string Token, DateTimeOffset ExpiresAt) AddAccess(string username, string sessionId, DateTimeOffset now)
    {
        lock (_gate) { Prune(); return Add(_access, new(username, sessionId, null, now.AddSeconds(options.SessionSeconds))); }
    }

    private (string Token, DateTimeOffset ExpiresAt) Add(Dictionary<string, Entry> entries, Entry value)
    {
        RequireRoom(entries);
        var token = NewToken(); entries.Add(Key(token), value); return (token, value.ExpiresAt);
    }

    private void RequireRoom(Dictionary<string, Entry> entries)
    {
        if (entries.Count >= options.MaxSessions) throw new InvalidOperationException("Authentication capacity reached.");
    }

    private void Prune()
    {
        var now = _clock.GetUtcNow();
        foreach (var entries in new[] { _access, _tickets })
            foreach (var key in entries.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToArray()) entries.Remove(key);
    }

    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenBytes));
    // Keep persisted rotations and the in-process bearer/ticket changes in the same order.
    private async ValueTask<T> SerializeAsync<T>(Func<ValueTask<T>> action, CancellationToken ct)
    {
        await _mutations.WaitAsync(ct).ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally { _mutations.Release(); }
    }
    private static string Key(string token) => token.Length > 4096 ? "" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
