namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Resolves and changes the player's exclusive equipped positions using the current build's item rules.</summary>
public interface IEquipmentSelection
{
    /// <summary>Repairs legacy conflicting selections and returns the complete authoritative character bag.</summary>
    ValueTask<IReadOnlyList<InventorySlotRecord>> ReadAsync(long userNum, CancellationToken ct);

    /// <summary>Atomically selects or removes an owned item and returns the updated bag; returns null for an unowned item.</summary>
    ValueTask<IReadOnlyList<InventorySlotRecord>?> ApplyAsync(long userNum, int itemId, bool worn, CancellationToken ct);
}
