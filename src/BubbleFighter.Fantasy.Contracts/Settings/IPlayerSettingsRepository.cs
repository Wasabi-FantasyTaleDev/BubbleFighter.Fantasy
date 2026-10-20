namespace BubbleFighter.Fantasy.Contracts.Settings;

/// <summary>One named native control and the key word assigned to it, preserving all 32 key bits.</summary>
/// <param name="Name">Case-sensitive control name emitted by the client's control tree.</param>
/// <param name="Key">The native four-byte key value.</param>
public sealed record CustomizedKey(string Name, int Key);

/// <summary>Stores complete ordered key maps by account and an explicitly configured control-name scope.
/// Callers write only for accounts already established by the deployment's player repository.</summary>
public interface IPlayerSettingsRepository
{
    /// <summary>Gets the validated immutable capacity policy used by this repository.</summary>
    CustomizedKeyLimits Limits { get; }

    /// <summary>Returns a detached ordered map, or an empty map when the account has no saved overrides in this scope.</summary>
    ValueTask<IReadOnlyList<CustomizedKey>> ReadKeysAsync(long userNum, string scope, CancellationToken ct);

    /// <summary>Atomically replaces one scope's complete map; an empty map resets that scope and releases its capacity.
    /// Invalid, cancelled or over-capacity writes leave all saved maps unchanged.</summary>
    ValueTask ReplaceKeysAsync(long userNum, string scope, IReadOnlyList<CustomizedKey> mappings, CancellationToken ct);
}
