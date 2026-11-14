using BubbleFighter.Fantasy.Contracts.Inventory;

namespace BubbleFighter.Fantasy.Contracts.Enhancement;

/// <summary>Resolves native equipment Article references within an enabled feature's canonical catalog.</summary>
public interface IEquipmentArticleResolver
{
    /// <summary>Creates the catalog preparation that must execute inside a transaction publishing a complete inventory.</summary>
    ArticleInitializationCommand CreateInitialization(long userNum, DateTimeOffset now);

    /// <summary>Returns the exact owned row for a usable equipment Article, or null when it is unavailable.</summary>
    ValueTask<InventorySlotRecord?> ResolveAsync(long userNum, ulong articleKey, CancellationToken ct);
}
