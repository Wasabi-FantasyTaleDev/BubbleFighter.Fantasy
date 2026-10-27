using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Match;

/// <summary>
/// <c>RelaySessionKey</c> as kr-1256 writes it with <c>kr-1256 sub_1159210</c> and reads with
/// <c>kr-1256 sub_11592A0</c>: <c>i32, i32, i32, u16, u16, i32</c> (20 bytes). The room registry
/// mints one per seat, the relay offer carries it to the client, and the match connection
/// presents it back for capability admission. Evidence: verified.
/// </summary>
/// <param name="Unknown0">Object +4.</param>
/// <param name="Unknown1">Object +8.</param>
/// <param name="Unknown2">Object +12.</param>
/// <param name="Unknown3">Object +16.</param>
/// <param name="Unknown4">Object +18.</param>
/// <param name="Unknown5">Object +20.</param>
public readonly record struct RelaySessionKey(
    int Unknown0,
    int Unknown1,
    int Unknown2,
    ushort Unknown3,
    ushort Unknown4,
    int Unknown5)
{
    /// <summary>Byte length of one key on the wire.</summary>
    public const int ByteLength = 20;

    /// <summary>Reads a key from an open reader.</summary>
    /// <param name="r">The reader.</param>
    public static RelaySessionKey Read(ref PacketReader r) =>
        new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadUInt16(), r.ReadUInt16(), r.ReadInt32());

    /// <summary>Writes this key.</summary>
    /// <param name="w">The writer.</param>
    public void Write(PacketWriter w)
    {
        w.WriteInt32(Unknown0);
        w.WriteInt32(Unknown1);
        w.WriteInt32(Unknown2);
        w.WriteUInt16(Unknown3);
        w.WriteUInt16(Unknown4);
        w.WriteInt32(Unknown5);
    }

    /// <summary>Builds a key from a 20-byte random fill. Layout matches the deserializer.</summary>
    /// <param name="bytes">Exactly 20 random bytes.</param>
    public static RelaySessionKey FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteLength)
            throw new ArgumentException("RelaySessionKey requires 20 bytes.", nameof(bytes));
        return new RelaySessionKey(
            BinaryPrimitives.ReadInt32LittleEndian(bytes),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]));
    }
}
