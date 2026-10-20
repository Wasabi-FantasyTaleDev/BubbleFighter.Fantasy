using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// <c>RelaySessionKey</c> as written by <c>sub_1159210</c> and read by
/// <c>sub_11592A0</c> (<c>0x1159210</c> / <c>0x11592A0</c>):
/// <c>i32, i32, i32, u16, u16, i32</c> (20 bytes). Evidence: verified.
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

/// <summary>
/// Relay C2S 1 body from <c>sub_95FD30</c> (<c>0x95FD30</c>): key, <c>u8</c>,
/// <c>i32</c>, <c>i32</c>, prefixed wstring. Evidence: verified.
/// </summary>
/// <param name="Key">The session key issued in S2C 571.</param>
/// <param name="Unknown20"><c>u8</c> after the key.</param>
/// <param name="Unknown21">First trailing <c>i32</c> (context).</param>
/// <param name="Unknown22">Second trailing <c>i32</c> (user number slot).</param>
/// <param name="Identity">Prefixed wstring (account or nickname).</param>
public readonly record struct RelayAuthenticate(
    RelaySessionKey Key,
    byte Unknown20,
    int Unknown21,
    int Unknown22,
    string Identity)
{
    /// <summary>Parses a complete relay C2S 1 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static RelayAuthenticate Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var key = RelaySessionKey.Read(ref r);
        var u20 = r.ReadByte();
        var u21 = r.ReadInt32();
        var u22 = r.ReadInt32();
        var id = r.ReadWideStringPrefixed();
        WireUtil.RequireConsumed(r, "relay C2S 1");
        return new RelayAuthenticate(key, u20, u21, u22, id);
    }
}

/// <summary>
/// Relay motion frame (20003 / 20004) from <c>sub_95FAE0</c>: leading <c>i64</c>
/// object identity, remainder opaque. Evidence: verified for the 8-byte prefix;
/// the rest is not decoded (no invented physics).
/// </summary>
/// <param name="ObjectIdentity">First eight bytes, written as two <c>i32</c> halves.</param>
/// <param name="Remainder">Bytes after the identity. Not interpreted.</param>
public readonly record struct RelayMotion(long ObjectIdentity, byte[] Remainder)
{
    /// <summary>Parses a relay 20003/20004 payload. Requires at least 8 bytes.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static RelayMotion Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
            throw new PacketFormatException("relay motion shorter than the 8-byte identity.", 0);
        var r = new PacketReader(payload);
        var id = r.ReadInt64();
        var rest = r.Remaining == 0 ? [] : r.ReadBytes(r.Remaining).ToArray();
        return new RelayMotion(id, rest);
    }
}
