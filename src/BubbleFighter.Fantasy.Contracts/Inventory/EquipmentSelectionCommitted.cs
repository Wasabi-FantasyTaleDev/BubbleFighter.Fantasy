using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>Notifies scene owners of a committed selection before the inventory acknowledgement is sent.</summary>
/// <param name="Session">Authenticated game session that made the selection.</param>
/// <param name="UserNum">Owner of the committed inventory.</param>
/// <param name="CharacterId">Selected character item.</param>
/// <param name="EquippedItemIds">Read-only, bounded snapshot of all worn item ids from the same commit.</param>
public sealed record EquipmentSelectionCommitted(ISession Session, int UserNum, int CharacterId,
    IReadOnlyList<int> EquippedItemIds) : IEvent;
