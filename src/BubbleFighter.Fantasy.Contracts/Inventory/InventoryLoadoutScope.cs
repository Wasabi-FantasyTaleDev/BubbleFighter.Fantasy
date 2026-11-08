namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>An immutable build key and the item identities present in that client's own catalog.</summary>
public sealed class InventoryLoadoutScope
{
    private readonly HashSet<int> _supported;

    /// <summary>Copies a positive, unique catalog and validates the storage key before a repository uses it.</summary>
    public InventoryLoadoutScope(string key, IReadOnlyList<int> supportedItemIds)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(supportedItemIds);
        if (supportedItemIds.Count is < 1 or > 65536 || supportedItemIds.Any(id => id <= 0))
            throw new ArgumentException("A loadout catalog must contain 1..65536 positive item identities.", nameof(supportedItemIds));
        _supported = supportedItemIds.ToHashSet();
        if (_supported.Count != supportedItemIds.Count)
            throw new ArgumentException("A loadout catalog must not repeat item identities.", nameof(supportedItemIds));
        Key = key;
        SupportedItemIds = Array.AsReadOnly(supportedItemIds.ToArray());
    }

    /// <summary>Gets the case-sensitive client-build storage key, independent of deployment ports or node identity.</summary>
    public string Key { get; }

    /// <summary>Gets the copied client catalog; it does not grant ownership of any item.</summary>
    public IReadOnlyList<int> SupportedItemIds { get; }

    /// <summary>Tests whether this client's catalog contains an item identity.</summary>
    public bool Supports(int itemId) => _supported.Contains(itemId);

    /// <summary>Rejects empty, oversized or ambiguous build keys before they can address storage.</summary>
    public static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 64
            || key.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.')))
            throw new ArgumentException("A loadout key must contain 1..64 lowercase ASCII letters, digits, dots, hyphens or underscores.", nameof(key));
    }
}
