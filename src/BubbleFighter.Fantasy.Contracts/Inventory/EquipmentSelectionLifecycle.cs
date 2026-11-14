using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Inventory;

/// <summary>
/// Raised by the selection owner, and awaited, before a player's equipment selection starts its storage write. Scene
/// owners use it to keep a waiting room from starting a round with the appearance the write is about to replace.
/// Every <see cref="EquipmentSelectionStarting"/> is followed by exactly one <see cref="EquipmentSelectionSettled"/>.
/// </summary>
/// <param name="Session">The game session making the selection.</param>
public sealed record EquipmentSelectionStarting(ISession Session) : IEvent;

/// <summary>
/// Raised by the selection owner after a selection started with <see cref="EquipmentSelectionStarting"/> ended, whether
/// it committed and published every snapshot, was refused or failed. It is raised even when the request was cancelled.
/// </summary>
/// <param name="Session">The game session that made the selection.</param>
public sealed record EquipmentSelectionSettled(ISession Session) : IEvent;
