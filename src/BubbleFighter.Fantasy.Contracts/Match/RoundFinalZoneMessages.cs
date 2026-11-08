using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Seeds the native map-item generator for a FinalZone round.</summary>
/// <param name="Seed">Seed shared by every client in this match.</param>
/// <param name="FirstObjectId">First object handle reserved for generated map items.</param>
/// <param name="MaximumCount">Maximum number of objects the native generator may create.</param>
public sealed record RoundFinalZoneItemsSeeded(int Seed, int FirstObjectId, int MaximumCount) : IMessage;

/// <summary>Requests exclusive ownership of one FinalZone map item for the named character.</summary>
/// <param name="ObjectId">Handle of the item touched by the client.</param>
/// <param name="Actor">Character requesting the pickup.</param>
public sealed record RoundFinalZoneItemPickupRequested(int ObjectId, int Actor) : IMessage;

/// <summary>Applies an accepted pickup and removes the item from every client's world.</summary>
/// <param name="ObjectId">Handle of the consumed item.</param>
/// <param name="Actor">Character receiving the item's native effect.</param>
public sealed record RoundFinalZoneItemPicked(int ObjectId, int Actor) : IMessage;

/// <summary>Announces one eliminated character and the remaining live population.</summary>
/// <param name="Actor">Character whose death is final for this match.</param>
/// <param name="SurvivorsRemaining">Number of connected, non-eliminated players after this death.</param>
public sealed record RoundFinalZonePlayerEliminated(int Actor, int SurvivorsRemaining) : IMessage;

/// <summary>Synchronizes an effect and the opaque sequence consumed by FinalZone and Zombie Escape map scripts.</summary>
/// <param name="Dummy">Map helper on which the effect is installed.</param>
/// <param name="Effect">Native effect resource name.</param>
/// <param name="Sound">Native sound resource name, or empty when the effect has no sound.</param>
/// <param name="Visible">Whether the receiving client installs or removes the effect.</param>
/// <param name="Unknown76">Uninterpreted native effect parameter at object offset 76.</param>
/// <param name="Sequence">Opaque script sequence; it is not a monotonic packet counter.</param>
public sealed record RoundFinalZoneMapEffectChanged(string Dummy, string Effect, string Sound,
    bool Visible, int Unknown76, int Sequence) : IMessage;

/// <summary>Controls the native Area32 map's water phase timer.</summary>
/// <param name="Active">Whether to start the supplied phase; false stops the currently selected phase.</param>
/// <param name="Stage">Native water phase code consumed by the map's stage switch.</param>
/// <param name="DurationMs">Duration of the selected native phase in milliseconds.</param>
public sealed record RoundFinalZoneWaterTimerChanged(bool Active, int Stage, int DurationMs) : IMessage;
