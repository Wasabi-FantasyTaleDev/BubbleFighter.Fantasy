using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>The player's whole inventory is republished, for example after an equipment change.</summary>
/// <param name="CharacterBag">Every owned slot.</param>
/// <param name="EquippedBag">The equipped selection.</param>
public sealed record InventoryReplaced(IReadOnlyList<InventorySlotRecord> CharacterBag, IReadOnlyList<InventorySlotRecord> EquippedBag) : IMessage;

/// <summary>A purchase changed the bag; the codec restates the complete bag the way its build expects.</summary>
/// <param name="CharacterBag">Every owned slot after the purchase.</param>
public sealed record InventoryPurchaseApplied(IReadOnlyList<InventorySlotRecord> CharacterBag) : IMessage;

/// <summary>Individual slots changed and are republished in place.</summary>
/// <param name="Slots">The changed slots.</param>
public sealed record InventorySlotsUpdated(IReadOnlyList<InventorySlotRecord> Slots) : IMessage;

/// <summary>
/// Time-limited items of the player ended and were removed from the bag; the build's codec tells the
/// client with its native expiry notice.
/// </summary>
/// <param name="ItemIds">The distinct item ids that expired.</param>
public sealed record ItemsExpired(IReadOnlyList<int> ItemIds) : IMessage;
