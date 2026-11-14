using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Lets inventory features materialize durable metadata before a dialect publishes ownership.</summary>
/// <param name="Protocol">The recipient's game protocol and native catalog.</param>
/// <param name="UserNum">The authenticated inventory owner.</param>
/// <param name="CharacterBag">The supplied character bag, empty for a partial update.</param>
/// <param name="EquippedBag">The supplied equipped bag, empty when the caller did not read it.</param>
/// <param name="Changes">The changed rows, or null for an inventory replacement.</param>
/// <param name="IsComplete">Whether both supplied bags already describe one complete committed snapshot.</param>
public sealed record InventoryPublicationPreparing(string Protocol, long UserNum,
    IReadOnlyList<InventorySlotRecord> CharacterBag, IReadOnlyList<InventorySlotRecord> EquippedBag,
    IReadOnlyList<InventorySlotRecord>? Changes, bool IsComplete) : IEvent;
