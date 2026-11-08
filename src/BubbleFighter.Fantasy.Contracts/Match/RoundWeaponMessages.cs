using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>A player's native dropped-weapon position, launch vector and unmodified weapon parameters.</summary>
/// <param name="X">The world x coordinate.</param>
/// <param name="Y">The world y coordinate.</param>
/// <param name="Z">The world z coordinate.</param>
/// <param name="VelocityX">The x launch component.</param>
/// <param name="VelocityY">The y launch component.</param>
/// <param name="VelocityZ">The z launch component.</param>
/// <param name="WeaponSpec">The native weapon specification selector.</param>
/// <param name="Unknown32">The first uninterpreted weapon word from the request.</param>
/// <param name="Unknown36">The second uninterpreted weapon word from the request.</param>
/// <param name="Unknown40">The uninterpreted native boolean from the request.</param>
public sealed record RoundWeaponDropRequested(float X, float Y, float Z,
    float VelocityX, float VelocityY, float VelocityZ, int WeaponSpec,
    int Unknown32, int Unknown36, bool Unknown40) : IMessage;

/// <summary>Creates one dropped weapon using a server-assigned identity and the reported native parameters.</summary>
/// <param name="ObjectId">The room-owned object identity returned in later pickup requests.</param>
/// <param name="Drop">The accepted native drop parameters.</param>
public sealed record RoundWeaponCreated(int ObjectId, RoundWeaponDropRequested Drop) : IMessage;

/// <summary>A player requests a dropped weapon for its own character.</summary>
/// <param name="ObjectId">The weapon object's server-assigned identity.</param>
/// <param name="Actor">The touching character's native object identity.</param>
/// <param name="ActorSeat">The character's seat decoded by the dialect; negative values are not players.</param>
public sealed record RoundWeaponPickupRequested(int ObjectId, int Actor, int ActorSeat) : IMessage;

/// <summary>Confirms the single accepted pickup of a dropped weapon to the round.</summary>
/// <param name="ObjectId">The consumed weapon object's identity.</param>
/// <param name="Actor">The touching character's native object identity.</param>
public sealed record RoundWeaponPicked(int ObjectId, int Actor) : IMessage;
