using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Requests one endpoint of an owned FinalZone BoobyTrapRod at the native target position.</summary>
/// <param name="Actor">The placing participant's scene handle.</param>
/// <param name="ItemSpecId">The generated-item specification selected by the client.</param>
/// <param name="Stage">Native placement stage: one for the first endpoint, three for the second.</param>
/// <param name="X">The target's world X coordinate.</param>
/// <param name="Y">The target's world Y coordinate.</param>
/// <param name="Z">The target's world Z coordinate.</param>
public sealed record RoundFinalZoneTrapRequested(int Actor, int ItemSpecId, int Stage, float X, float Y, float Z) : IMessage;

/// <summary>Creates one server-issued rod and advances its owner's native placement state.</summary>
/// <param name="ObjectId">The nonreusable scene handle allocated for this endpoint.</param>
/// <param name="Placement">The accepted owner, item, stage and target position.</param>
public sealed record RoundFinalZoneTrapSpawned(int ObjectId, RoundFinalZoneTrapRequested Placement) : IMessage;

/// <summary>Connects two issued rod endpoints through the native peer trap consumer.</summary>
/// <param name="Actor">The placing participant's scene handle.</param>
/// <param name="FirstObjectId">The first accepted rod endpoint.</param>
/// <param name="SecondObjectId">The second accepted rod endpoint.</param>
/// <param name="WeaponId">The native trap weapon specification.</param>
/// <param name="Unknown16">The opaque native weapon attribute words.</param>
public sealed record RoundFinalZoneTrapLinked(int Actor, int FirstObjectId, int SecondObjectId,
    ushort WeaponId, IReadOnlyList<int> Unknown16) : IMessage;
