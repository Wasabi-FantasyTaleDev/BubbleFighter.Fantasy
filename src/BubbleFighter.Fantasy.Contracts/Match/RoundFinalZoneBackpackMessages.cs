using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Native weapon reinforcement vectors retained with a FinalZone item.</summary>
/// <param name="Unknown4">Uninterpreted first counted word vector.</param>
/// <param name="Unknown20">Uninterpreted second counted word vector.</param>
public sealed record FinalZoneReinforcement(IReadOnlyList<int> Unknown4, IReadOnlyList<int> Unknown20);

/// <summary>One native generated-item specification and its carried amount in a FinalZone backpack.</summary>
/// <param name="ItemSpecId">Generated-item specification key, also used when selecting an entry.</param>
/// <param name="WeaponId">Signed native weapon specification ID; nonweapon entries use the resource's sentinel.</param>
/// <param name="Quantity">Amount the native receiver adds to the held item.</param>
/// <param name="Reinforcement">Weapon reinforcement retained with this entry.</param>
public sealed record FinalZoneBackpackEntry(int ItemSpecId, int WeaponId, int Quantity,
    FinalZoneReinforcement Reinforcement);

/// <summary>Reports the eliminated local character's backpack for server-authorized world placement.</summary>
/// <param name="OwnerActor">Local character's scene object handle.</param>
/// <param name="X">Native world-space horizontal coordinate.</param>
/// <param name="Y">Native world-space height.</param>
/// <param name="Z">Native world-space depth coordinate.</param>
/// <param name="Unknown16">Uninterpreted word following the position.</param>
/// <param name="Unknown20">Uninterpreted second word following the position.</param>
/// <param name="Unknown24">Uninterpreted third word following the position.</param>
/// <param name="Unknown28">Uninterpreted native backpack text.</param>
/// <param name="Items">Contents reported by the client; acceptance requires the round's item ledger.</param>
public sealed record RoundFinalZoneBackpackDropped(int OwnerActor, float X, float Y, float Z,
    int Unknown16, int Unknown20, int Unknown24, string Unknown28,
    IReadOnlyList<FinalZoneBackpackEntry> Items) : IMessage;

/// <summary>Creates an authorized backpack under its server-assigned world object handle.</summary>
/// <param name="ObjectId">Unique handle reserved for this backpack.</param>
/// <param name="Contents">Validated owner, location and item contents.</param>
public sealed record RoundFinalZoneBackpackSpawned(int ObjectId, RoundFinalZoneBackpackDropped Contents) : IMessage;

/// <summary>Requests one entry from the backpack the client has opened.</summary>
/// <param name="ObjectId">World object handle of the open backpack.</param>
/// <param name="ItemSpecId">Generated-item specification key of the selected entry.</param>
public sealed record RoundFinalZoneBackpackPickupRequested(int ObjectId, int ItemSpecId) : IMessage;

/// <summary>Removes one accepted entry from the world backpack on every client.</summary>
/// <param name="UserNumber">Recipient's room user number, compared with CharacterBase offset 580.</param>
/// <param name="ObjectId">World object handle of the backpack.</param>
/// <param name="ItemSpecId">Selected generated-item specification key.</param>
public sealed record RoundFinalZoneBackpackPicked(int UserNumber, int ObjectId, int ItemSpecId) : IMessage;
