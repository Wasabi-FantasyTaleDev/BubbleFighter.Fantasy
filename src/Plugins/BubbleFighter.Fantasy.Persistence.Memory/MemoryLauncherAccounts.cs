using BubbleFighter.Fantasy.Contracts.Launcher;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>Bounded, process-local launcher credentials for development profiles.</summary>
public sealed class MemoryLauncherAccounts(int maximumAccounts) : ILauncherAccountRepository
{
    private readonly Dictionary<string, LauncherAccount> _accounts = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <inheritdoc />
    public ValueTask<LauncherAccount?> FindAsync(string username, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return ValueTask.FromResult(_accounts.GetValueOrDefault(username));
    }

    /// <inheritdoc />
    public ValueTask<bool> RegisterAsync(string username, string passwordHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_accounts.ContainsKey(username)) return ValueTask.FromResult(false);
            if (_accounts.Count >= maximumAccounts) throw new InvalidOperationException("Launcher account storage is full.");
            _accounts.Add(username, new LauncherAccount(username, passwordHash, null, null, null));
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> TrySetInitialPasswordAsync(string username, string passwordHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(passwordHash);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_accounts.TryGetValue(username, out var account) || account.PasswordHash.Length != 0 || account.BannedUntil > DateTime.UtcNow)
                return ValueTask.FromResult(false);
            _accounts[username] = account with { PasswordHash = passwordHash };
            return ValueTask.FromResult(true);
        }
    }
}
