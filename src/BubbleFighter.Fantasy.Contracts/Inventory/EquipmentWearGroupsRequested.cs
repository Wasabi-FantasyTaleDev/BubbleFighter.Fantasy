using BubbleFighter.Fantasy.Abstractions;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// Requests additional equipment positions for the named protocol before reading or committing worn state.
/// The waterfall returns explicit, nonoverlapping item groups whose member collections are immutable.
/// Contributors keep each group identity stable until its membership changes. The provider validates and
/// caches one bounded snapshot before passing it to the inventory repository's atomic selection; removing
/// a contributor invalidates that snapshot on the next request. Contributions share one
/// <see cref="InventoryLimits.MaxItemsPerWearGroup"/> membership budget.
/// </summary>
/// <param name="Protocol">The equipment provider's game protocol.</param>
public sealed record EquipmentWearGroupsRequested(string Protocol) : IEvent;
