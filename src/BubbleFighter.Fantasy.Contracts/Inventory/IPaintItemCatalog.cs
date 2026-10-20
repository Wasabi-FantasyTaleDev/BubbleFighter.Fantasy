namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Resolves avatar spray items from the selected client's native item definitions.</summary>
public interface IPaintItemCatalog
{
    /// <summary>Returns the fixed material color of a supported spray, or false for other item functions.</summary>
    bool TryGetColorIndex(int itemId, out ushort colorIndex);
}
