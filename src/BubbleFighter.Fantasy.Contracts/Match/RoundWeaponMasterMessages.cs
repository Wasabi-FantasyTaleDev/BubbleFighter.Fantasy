using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>
/// Assigns equipment to one seat's character: each slot receives the item at the same index of
/// <see cref="Items"/>. The WeaponMaster round (mode 26) uses it for every weapon it hands out.
/// </summary>
/// <param name="Seat">The seat whose character receives the equipment.</param>
/// <param name="Slots">The native equipment slot of each assignment.</param>
/// <param name="Items">The item id assigned to the slot at the same index.</param>
/// <param name="Reason">
/// The native reason word. The client shows the weapon-change popup for the local seat and picks its text by this
/// word; the round decides which value it sends.
/// </param>
public sealed record RoundEquipmentApplied(int Seat, IReadOnlyList<int> Slots, IReadOnlyList<int> Items, int Reason) : IMessage;

/// <summary>
/// The sending player asks for a new WeaponMaster weapon: its own client counted as many deaths since its last
/// weapon as the client's option sheet allows. The request carries nothing else; the round checks the count itself.
/// </summary>
public sealed record RoundWeaponMasterRevivalRequested : IMessage;
