using System.Buffers.Binary;
using System.Text;

namespace BubbleFighter.Fantasy.Abstractions.Protocol;

/// <summary>
/// Little-endian cursor over a packet payload. Version-independent; individual codecs
/// decide which fields exist for their client profile. Every read advances
/// <see cref="Offset"/> and throws <see cref="PacketFormatException"/> on truncation.
/// </summary>
public ref struct PacketReader
{
    private ReadOnlySpan<byte> _span;

    /// <summary>Creates a reader over a payload.</summary>
    /// <param name="span">The payload bytes.</param>
    public PacketReader(ReadOnlySpan<byte> span) => _span = span;

    /// <summary>Gets the current read offset within the payload.</summary>
    public int Offset { get; private set; }

    /// <summary>Gets the number of unread bytes.</summary>
    public int Remaining => _span.Length;

    private ReadOnlySpan<byte> Take(int count, string what)
    {
        if (_span.Length < count)
            throw new PacketFormatException($"expected {count} byte(s) for {what} but only {_span.Length} remain", Offset);
        var taken = _span[..count];
        _span = _span[count..];
        Offset += count;
        return taken;
    }

    /// <summary>Reads one unsigned byte.</summary>
    public byte ReadByte() => Take(1, "u8")[0];

    /// <summary>Reads a little-endian unsigned 16-bit integer.</summary>
    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2, "u16"));

    /// <summary>Reads a little-endian signed 32-bit integer.</summary>
    public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4, "i32"));

    /// <summary>Reads a little-endian unsigned 32-bit integer.</summary>
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4, "u32"));

    /// <summary>Reads a little-endian signed 64-bit integer.</summary>
    public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8, "i64"));

    /// <summary>Reads a little-endian single-precision float.</summary>
    public float ReadSingle() => BinaryPrimitives.ReadSingleLittleEndian(Take(4, "f32"));

    /// <summary>Reads a little-endian double-precision float.</summary>
    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8, "f64"));

    /// <summary>Reads a boolean encoded as a 4-byte little-endian integer (non-zero is true).</summary>
    public bool ReadBool32() => ReadUInt32() != 0;

    /// <summary>
    /// Reads a UTF-16LE string prefixed by an unsigned 32-bit character count. No NUL
    /// terminator is expected.
    /// </summary>
    public string ReadWideStringPrefixed()
    {
        var chars = checked((int)ReadUInt32());
        var bytes = Take(chars * 2, "wide string");
        return Encoding.Unicode.GetString(bytes);
    }

    /// <summary>
    /// Reads a NUL-terminated UTF-16LE string. The terminating NUL is consumed; the payload
    /// must contain one.
    /// </summary>
    public string ReadWideCString()
    {
        for (var i = 0; i + 1 < _span.Length; i += 2)
        {
            if (_span[i] == 0 && _span[i + 1] == 0)
            {
                var s = Encoding.Unicode.GetString(_span[..i]);
                _span = _span[(i + 2)..];
                Offset += i + 2;
                return s;
            }
        }
        throw new PacketFormatException("unterminated wide cstring", Offset);
    }

    /// <summary>Reads a raw byte span.</summary>
    /// <param name="count">The number of bytes.</param>
    public ReadOnlySpan<byte> ReadBytes(int count) => Take(count, "bytes");
}
