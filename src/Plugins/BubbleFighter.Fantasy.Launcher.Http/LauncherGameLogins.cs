using System.Security.Cryptography;
using BubbleFighter.Fantasy.Contracts.Companion;

namespace BubbleFighter.Fantasy.Launcher.Http;

/// <summary>
/// Launches of builds whose players sign in inside the game (<c>launcher_login: game</c>). The launcher opens one
/// before anyone signs in; the game binds it with its own sign-in, and the launcher then fetches the launch's
/// companion grant with the launch key. Bounded by <see cref="LauncherOptions.MaxSessions"/>; an unbound launch
/// expires after <see cref="LauncherOptions.TicketSeconds"/>, a bound one after the refresh lifetime since its last use.
/// </summary>
internal sealed class LauncherGameLogins(LauncherOptions options, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _launches = new(StringComparer.Ordinal);

    private sealed class Entry(string key, string build, DateTimeOffset expires)
    {
        public string Key { get; } = key;
        public string Build { get; } = build;
        public DateTimeOffset Expires { get; set; } = expires;
        public string? Account { get; set; }
        public CompanionGrant? Grant { get; set; }
        public bool GrantFetched { get; set; }
    }

    /// <summary>Opens an unbound launch for <paramref name="build"/> and returns its id, key and expiry.</summary>
    public (string Id, string Key, DateTimeOffset ExpiresAt) Open(string build)
    {
        lock (_gate)
        {
            Prune();
            if (_launches.Count >= options.MaxSessions) throw new InvalidOperationException("Authentication capacity reached.");
            var id = Token(); var key = Token();
            var expires = _clock.GetUtcNow().AddSeconds(options.TicketSeconds);
            _launches[id] = new(key, build, expires);
            return (id, key, expires);
        }
    }

    /// <summary>Binds an unbound launch to the account that signed in inside the game; false when it is unknown or taken.</summary>
    public bool Bind(string id, string key, string build, string account)
    {
        lock (_gate)
        {
            Prune();
            if (!_launches.TryGetValue(id, out var entry) || !Matches(entry, key) || entry.Build != build || entry.Account is not null) return false;
            entry.Account = account;
            entry.Expires = _clock.GetUtcNow().AddSeconds(options.RefreshSessionSeconds);
            return true;
        }
    }

    /// <summary>Records the companion grant the bound launch received.</summary>
    public void Attach(string id, CompanionGrant grant)
    {
        lock (_gate)
            if (_launches.TryGetValue(id, out var entry)) entry.Grant = grant;
    }

    /// <summary>
    /// Gets the launch's account and companion grant, with whether this is the grant's first hand-out (later calls
    /// renew it). Null when the key does not match, the launch expired or no game signed in yet.
    /// </summary>
    public (string Account, CompanionGrant? Grant, bool Fresh)? Companion(string id, string key)
    {
        lock (_gate)
        {
            Prune();
            if (!_launches.TryGetValue(id, out var entry) || !Matches(entry, key) || entry.Account is null) return null;
            entry.Expires = _clock.GetUtcNow().AddSeconds(options.RefreshSessionSeconds);
            var fresh = entry.Grant is not null && !entry.GrantFetched;
            if (fresh) entry.GrantFetched = true;
            return (entry.Account, entry.Grant, fresh);
        }
    }

    /// <summary>Gets whether a launch with this id is open, for the caller's error choice.</summary>
    public bool Known(string id)
    {
        lock (_gate) { Prune(); return _launches.ContainsKey(id); }
    }

    private static bool Matches(Entry entry, string key) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(entry.Key), System.Text.Encoding.ASCII.GetBytes(key));

    private void Prune()
    {
        var now = _clock.GetUtcNow();
        foreach (var id in _launches.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) _launches.Remove(id);
    }

    private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
