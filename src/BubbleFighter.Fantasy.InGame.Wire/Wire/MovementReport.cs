using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// C2S/S2C 32885 full movement: i32 entity, three f32 coordinates, three u8 orientation/state fields,
/// and u16 tick. Serializer 0x95E920 and delta producer 0x95F490 establish all 21 wire bytes.
/// </summary>
/// <param name="ObjectId">The scene entity computed from the room slot by 0x6D2040.</param>
/// <param name="X">Absolute world x.</param>
/// <param name="Y">Absolute world y.</param>
/// <param name="Z">Absolute world z.</param>
/// <param name="Yaw">Raw yaw byte.</param>
/// <param name="Pitch">Raw pitch byte.</param>
/// <param name="State">Packed movement state.</param>
/// <param name="Tick">Unsigned sample clock, including natural wraparound.</param>
public readonly record struct MovementReport(int ObjectId, float X, float Y, float Z,
    byte Yaw, byte Pitch, byte State, ushort Tick)
{
    /// <summary>Length of the complete serialized movement record.</summary>
    public const int BodyLength = 21;

    /// <summary>Writes every component without converting coordinates or the sample clock.</summary>
    public void Write(PacketWriter w)
    {
        w.WriteInt32(ObjectId);
        w.WriteSingle(X); w.WriteSingle(Y); w.WriteSingle(Z);
        w.WriteByte(Yaw); w.WriteByte(Pitch); w.WriteByte(State);
        w.WriteUInt16(Tick);
    }

    /// <summary>Parses exactly one complete full movement record.</summary>
    public static MovementReport Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var result = new MovementReport(r.ReadInt32(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
            r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadUInt16());
        WireUtil.RequireConsumed(r, "C2S 32885");
        return result;
    }
}
