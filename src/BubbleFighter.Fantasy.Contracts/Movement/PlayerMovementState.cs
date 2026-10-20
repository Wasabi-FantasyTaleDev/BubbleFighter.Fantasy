namespace BubbleFighter.Fantasy.Contracts.Movement;

/// <summary>
/// One actor's merged movement state, the record the others-only S2C 32885 rebroadcast transposes
/// onto the wire. The six fields are exactly the mask-gated payload of the client's high-cadence movement
/// samples: absolute position floats plus the raw yaw, pitch and state bytes, kept in wire units so a
/// rebroadcast is a byte-for-byte copy with no re-encoding. </summary> <param name="X">Absolute world
/// x.</param> <param name="Y">Absolute world y.</param> <param name="Z">Absolute world z.</param> <param
/// name="Yaw">Raw yaw byte as sent on the wire.</param> <param name="Pitch">Raw pitch byte as sent on the
/// wire.</param> <param name="State">Packed state byte as sent on the wire.</param>
public sealed record PlayerMovementState(float X, float Y, float Z, byte Yaw, byte Pitch, byte State)
{
    /// <summary>
    /// The mask value whose bits select every field of a movement sample
    /// (1/2/4 = x/y/z, 8/0x10/0x20 = yaw/pitch/state). A sample or report
    /// carrying it carries everything, so it can initialize an actor with no
    /// stored state; anything narrower is a partial and may only merge into
    /// state that already exists.
    ///
    /// </summary>
    public const byte FullMask = 0x3F;
}
