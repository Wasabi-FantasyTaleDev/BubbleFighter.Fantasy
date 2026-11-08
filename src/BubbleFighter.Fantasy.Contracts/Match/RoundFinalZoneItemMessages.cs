using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>A generated map item with an explicit world handle, specification and native spawn descriptor.</summary>
/// <param name="ObjectId">Server-reserved world object handle.</param>
/// <param name="ItemSpecId">Key in the client's generated-item specification table.</param>
/// <param name="Quantity">Quantity installed in the generated item.</param>
/// <param name="X">World-space horizontal coordinate.</param>
/// <param name="Y">World-space height.</param>
/// <param name="Z">World-space depth coordinate.</param>
/// <param name="Unknown24">Uninterpreted descriptor word at offset 24.</param>
/// <param name="Unknown28">Uninterpreted descriptor word at offset 28.</param>
/// <param name="Unknown32">Uninterpreted descriptor word at offset 32.</param>
/// <param name="Temporary">Whether the native object uses its short expiration timer.</param>
/// <param name="Reinforcement">Native reinforcement vectors carried with the item.</param>
public sealed record FinalZoneMapItem(int ObjectId, int ItemSpecId, int Quantity, float X, float Y, float Z,
    int Unknown24, int Unknown28, int Unknown32, bool Temporary, FinalZoneReinforcement Reinforcement);

/// <summary>Creates an explicit batch of generated map items through the native Area32 item-list consumer.</summary>
/// <param name="Items">Authoritative objects to create; each handle belongs to the current round.</param>
public sealed record RoundFinalZoneItemsSpawned(IReadOnlyList<FinalZoneMapItem> Items) : IMessage;

/// <summary>Returns held items to the world; the server debits the issuing inventory before assigning new handles.</summary>
/// <param name="Items">Native drop descriptors; ObjectId is absent on this request and is always zero.</param>
public sealed record RoundFinalZoneItemsDropped(IReadOnlyList<FinalZoneMapItem> Items) : IMessage;
