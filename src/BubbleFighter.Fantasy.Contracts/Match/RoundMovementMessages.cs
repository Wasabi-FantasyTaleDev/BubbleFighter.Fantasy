using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Movement;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>
/// A player's own movement during a round: the parts of its character's position, orientation and state that the
/// mask selects. A full record carries <see cref="PlayerMovementState.FullMask"/>; a partial one only what changed
/// since the last full record, so it means something only after one.
/// </summary>
/// <param name="ActorId">The character the client says moved; it must be the sender's own.</param>
/// <param name="Mask">Presence bits: 1/2/4 select x/y/z, 8/0x10/0x20 select yaw/pitch/state.</param>
/// <param name="X">Position x; ignored when its bit is clear.</param>
/// <param name="Y">Position y; ignored when its bit is clear.</param>
/// <param name="Z">Position z; ignored when its bit is clear.</param>
/// <param name="Yaw">Raw yaw byte; ignored when its bit is clear.</param>
/// <param name="Pitch">Raw pitch byte; ignored when its bit is clear.</param>
/// <param name="State">Packed state byte; ignored when its bit is clear.</param>
/// <param name="Tick">The client's sample clock, passed on unchanged.</param>
public sealed record RoundMovementReported(int ActorId, byte Mask, float X, float Y, float Z, byte Yaw, byte Pitch,
    byte State, ushort Tick) : IMessage;

/// <summary>
/// Another player's complete movement record, merged by the server from that player's full and partial reports and
/// sent on the receiver's match relay connection.
/// </summary>
/// <param name="ActorId">The moving character.</param>
/// <param name="Movement">Its merged position, orientation and state.</param>
/// <param name="Tick">The tick of the report that produced the record.</param>
public sealed record RoundMovementRelayed(int ActorId, PlayerMovementState Movement, ushort Tick) : IMessage;
