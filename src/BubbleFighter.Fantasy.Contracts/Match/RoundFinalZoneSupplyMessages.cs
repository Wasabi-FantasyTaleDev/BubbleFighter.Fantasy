using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>Creates a native drone whose flight path is selected by a shared seed.</summary>
/// <param name="ObjectId">Server-reserved drone handle.</param>
/// <param name="Seed">Seed consumed before the native flight-path generator.</param>
public sealed record RoundFinalZoneDroneSpawned(int ObjectId, int Seed) : IMessage;

/// <summary>Reports the destruction of a currently issued native drone.</summary>
/// <param name="ObjectId">Handle of the destroyed drone.</param>
/// <param name="X">Reported world-space horizontal coordinate.</param>
/// <param name="Y">Reported world-space height.</param>
/// <param name="Z">Reported world-space depth coordinate.</param>
/// <param name="Unknown16">Uninterpreted word copied from the damaging object.</param>
public sealed record RoundFinalZoneDroneDestroyed(int ObjectId, float X, float Y, float Z, int Unknown16) : IMessage;

/// <summary>Replaces a destroyed drone with a seeded box whose contents the server has registered.</summary>
/// <param name="Drone">Accepted destruction descriptor.</param>
/// <param name="BoxObjectId">Server-reserved supply-box handle.</param>
/// <param name="Seed">Seed used by both server and clients to generate box contents.</param>
public sealed record RoundFinalZoneDroneBoxSpawned(RoundFinalZoneDroneDestroyed Drone, int BoxObjectId, int Seed) : IMessage;

/// <summary>Requests a golden supply at a map-script spawn point.</summary>
/// <param name="SpawnIndex">Index returned to the native map-script callback.</param>
/// <param name="X">World-space horizontal coordinate.</param>
/// <param name="Y">World-space height.</param>
/// <param name="Z">World-space depth coordinate.</param>
public sealed record RoundFinalZoneGoldenSupplyRequested(int SpawnIndex, float X, float Y, float Z) : IMessage;

/// <summary>Creates a seeded golden supply and acknowledges its map-script spawn index.</summary>
/// <param name="Spawn">Accepted map-script descriptor.</param>
/// <param name="BoxObjectId">Server-reserved supply-box handle.</param>
/// <param name="Seed">Seed used by both server and clients to generate box contents.</param>
public sealed record RoundFinalZoneGoldenSupplySpawned(RoundFinalZoneGoldenSupplyRequested Spawn, int BoxObjectId, int Seed) : IMessage;
