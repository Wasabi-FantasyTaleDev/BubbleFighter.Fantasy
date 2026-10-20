using BubbleFighter.Fantasy.Contracts.Settings;

namespace BubbleFighter.Fantasy.Persistence.Memory;

/// <summary>Bounded customized-key maps sharing the account table and per-player mutation gate.</summary>
public sealed class MemoryPlayerSettings : IPlayerSettingsRepository
{
    private readonly MemoryPlayerTable _table;

    /// <summary>Creates settings storage using the same bounded player table as the other memory repositories.</summary>
    public MemoryPlayerSettings(MemoryPlayerTable table, CustomizedKeyLimits limits)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        _table = table;
        Limits = limits;
    }

    /// <inheritdoc />
    public CustomizedKeyLimits Limits { get; }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CustomizedKey>> ReadKeysAsync(long userNum, string scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CustomizedKeyLimits.ValidateIdentity(userNum, scope);
        var player = _table.Find(userNum);
        if (player is null) return ValueTask.FromResult<IReadOnlyList<CustomizedKey>>([]);
        lock (player.Gate)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<CustomizedKey>>(
                player.CustomizedKeys.TryGetValue(scope, out var keys) ? Limits.Copy(keys) : []);
        }
    }

    /// <inheritdoc />
    public ValueTask ReplaceKeysAsync(long userNum, string scope, IReadOnlyList<CustomizedKey> mappings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CustomizedKeyLimits.ValidateIdentity(userNum, scope);
        var copy = Limits.Copy(mappings);
        // A reset of an unseen account must not consume its capacity.
        var player = copy.Length == 0 ? _table.Find(userNum) : _table.GetOrAdd(userNum);
        if (player is null) return ValueTask.CompletedTask;
        lock (player.Gate)
        {
            ct.ThrowIfCancellationRequested();
            if (copy.Length == 0) player.CustomizedKeys.Remove(scope);
            else
            {
                if (!player.CustomizedKeys.ContainsKey(scope) && player.CustomizedKeys.Count >= Limits.MaxScopes)
                    throw new InvalidOperationException("The player's customized-key scope capacity has been reached.");
                player.CustomizedKeys[scope] = copy;
            }
        }
        return ValueTask.CompletedTask;
    }
}
