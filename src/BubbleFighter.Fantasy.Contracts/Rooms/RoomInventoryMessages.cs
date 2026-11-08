using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Rooms;

/// <summary>The native temporary room selection, independent of owned bag items and saved preset pages.</summary>
/// <param name="CharacterSelection">The native character selector supplied by the room dialog.</param>
/// <param name="WeaponSelections">The ordered native weapon-spec selectors, including any retained trailing selector.</param>
public sealed record RoomInventorySelection(int CharacterSelection, IReadOnlyList<int> WeaponSelections);

/// <summary>Requests replacement of the caller's temporary waiting-room selection.</summary>
/// <param name="Selection">The dialect-validated native selection; it grants no inventory items.</param>
public sealed record RoomInventoryChangeRequested(RoomInventorySelection Selection) : IMessage;

/// <summary>Publishes a seated player's committed temporary selection to the room's members.</summary>
/// <param name="Slot">The server-owned seat index, never supplied by the requesting client.</param>
/// <param name="Selection">The selection retained by the room owner.</param>
public sealed record RoomInventoryChanged(int Slot, RoomInventorySelection Selection) : IMessage;
