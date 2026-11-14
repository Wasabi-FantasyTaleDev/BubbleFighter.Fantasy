using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Exposes the canonical catalog after the build's character-model compatibility checks.</summary>
public interface IInventoryBuildScope
{
    /// <summary>Gets the immutable scope shared by inventory, equipment and feature-owned progression.</summary>
    InventoryLoadoutScope Scope { get; }
}

/// <summary>Lets a feature initialize metadata using the inventory row's already-filtered build scope.</summary>
/// <param name="BuildKey">The canonical build whose metadata will be prepared.</param>
/// <param name="UserNum">The authenticated owner.</param>
public sealed record InventoryMetadataPreparing(string BuildKey, long UserNum) : IEvent;
