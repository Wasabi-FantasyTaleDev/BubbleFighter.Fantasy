using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace BubbleFighter.Fantasy.Abstractions.Protocol;

/// <summary>
/// Little-endian payload builder over an <see cref="IBufferWriter{T}"/>. Symmetric with
/// <see cref="PacketReader"/>.
/// </summary>
public sealed class PacketWriter
{
    private readonly IBufferWriter<byte> _writer;

    /// <summary>Creates a writer over a buffer.</summary>
    /// <param name="writer">The destination buffer.</param>
    public PacketWriter(IBufferWriter<byte> writer) => _writer = writer;

    /// <summary>Creates a writer over a fresh growable buffer.</summary>
    public static PacketWriter Create() => new(new ArrayBufferWriter<byte>());

    /// <summary>Gets the underlying buffer.</summary>
    public IBufferWriter<byte> Buffer => _writer;

    private Span<byte> Take(int count)
    {
        var span = _writer.GetSpan(count);
        _writer.Advance(count);
        return span;
    }

    /// <summary>Writes one unsigned byte.</summary>
    /// <param name="value">The value.</param>
    public void WriteByte(byte value) => Take(1)[0] = value;

    /// <summary>Writes a little-endian unsigned 16-bit integer.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), value);

    /// <summary>Writes an <c>i16</c> little-endian.</summary>
    /// <param name="value">The value.</param>
    public void WriteInt16(short value) => BinaryPrimitives.WriteInt16LittleEndian(Take(2), value);

    /// <summary>Writes a little-endian signed 32-bit integer.</summary>
    /// <param name="value">The value.</param>
    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), value);

    /// <summary>Writes a little-endian unsigned 32-bit integer.</summary>
    /// <param name="value">The value.</param>
    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), value);

    /// <summary>Writes a little-endian signed 64-bit integer.</summary>
    /// <param name="value">The value.</param>
    public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), value);

    /// <summary>Writes a little-endian single-precision float.</summary>
    /// <param name="value">The value.</param>
    public void WriteSingle(float value) => BinaryPrimitives.WriteSingleLittleEndian(Take(4), value);

    /// <summary>Writes a little-endian double-precision float.</summary>
    /// <param name="value">The value.</param>
    public void WriteDouble(double value) => BinaryPrimitives.WriteDoubleLittleEndian(Take(8), value);

    /// <summary>Writes a boolean as a 4-byte little-endian integer.</summary>
    /// <param name="value">The value.</param>
    public void WriteBool32(bool value) => WriteUInt32(value ? 1u : 0u);

    /// <summary>Writes a UTF-16LE string prefixed by an unsigned 32-bit character count.</summary>
    /// <param name="value">The string.</param>
    public void WriteWideStringPrefixed(string value)
    {
        WriteUInt32((uint)value.Length);
        var bytes = Encoding.Unicode.GetBytes(value);
        bytes.CopyTo(Take(bytes.Length));
    }

    /// <summary>Writes a UTF-16LE string followed by a NUL terminator.</summary>
    /// <param name="value">The string.</param>
    public void WriteWideCString(string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value);
        bytes.CopyTo(Take(bytes.Length));
        WriteUInt16(0);
    }

    /// <summary>Writes raw bytes.</summary>
    /// <param name="bytes">The bytes.</param>
    public void WriteBytes(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Take(bytes.Length));
}
