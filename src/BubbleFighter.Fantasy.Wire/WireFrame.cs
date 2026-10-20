using System.Buffers;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Wire;

/// <summary>
/// Codec for the wire frame (finalized by client function <c>sub_10BB3E0</c>): an 8-byte
/// little-endian header <c>u32 payload_length, u16 opcode, u8 payload_xor,
/// u8 header_xor</c> followed by the payload. <c>payload_xor</c> is the XOR of all payload
/// bytes; <c>header_xor</c> is the XOR of header bytes at indices 0 through 6. The entire
/// frame — header included — travels inside the stream cipher.
/// </summary>
public static class WireFrame
{
    /// <summary>The frame header byte length.</summary>
    public const int HeaderSize = 8;

    /// <summary>A decoded frame header.</summary>
    /// <param name="PayloadLength">The payload byte length.</param>
    /// <param name="Opcode">The wire opcode.</param>
    /// <param name="PayloadXor">The expected XOR of all payload bytes.</param>
    public readonly record struct Header(int PayloadLength, ushort Opcode, byte PayloadXor);

    /// <summary>Encodes a frame with valid integrity bytes.</summary>
    /// <param name="opcode">The wire opcode.</param>
    /// <param name="payload">The payload bytes.</param>
    /// <returns>The complete plaintext frame.</returns>
    public static byte[] Encode(ushort opcode, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0), (uint)payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), opcode);
        frame[6] = Xor(payload);
        frame[7] = Xor(frame.AsSpan(0, 7));
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>Decodes and validates a frame header.</summary>
    /// <param name="header">At least <see cref="HeaderSize"/> decrypted bytes.</param>
    /// <param name="decoded">Receives the header.</param>
    /// <returns><see langword="false"/> when the header integrity byte mismatches.</returns>
    public static bool TryReadHeader(ReadOnlySpan<byte> header, out Header decoded)
    {
        decoded = default;
        if (header.Length < HeaderSize)
            return false;
        if (Xor(header[..7]) != header[7])
            return false;
        decoded = new Header(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(header),
            BinaryPrimitives.ReadUInt16LittleEndian(header[4..]),
            header[6]);
        return true;
    }

    /// <summary>Validates a payload against the header's integrity byte.</summary>
    /// <param name="header">The decoded header.</param>
    /// <param name="payload">The payload bytes; length must equal <see cref="Header.PayloadLength"/>.</param>
    /// <returns><see langword="false"/> when the payload XOR mismatches.</returns>
    public static bool ValidatePayload(Header header, ReadOnlySpan<byte> payload) =>
        payload.Length == header.PayloadLength && Xor(payload) == header.PayloadXor;

    /// <summary>
    /// Tries to consume one complete frame from the head of a decrypted receive buffer.
    /// The buffer may be split across any number of segments; it is advanced only when a
    /// complete frame is consumed.
    /// </summary>
    /// <param name="buffer">The decrypted receive buffer; advanced past the frame on success.</param>
    /// <param name="maxPayloadBytes">The maximum accepted payload length.</param>
    /// <param name="header">Receives the decoded header.</param>
    /// <param name="payload">Receives a freshly allocated payload; empty when the frame is incomplete.</param>
    /// <returns><see langword="true"/> when a complete frame was consumed;
    /// <see langword="false"/> when more bytes are needed.</returns>
    /// <exception cref="PacketFormatException">Thrown when the header or
    /// payload integrity byte mismatches, or the declared payload length exceeds
    /// <paramref name="maxPayloadBytes"/>.</exception>
    public static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, int maxPayloadBytes, out Header header, out byte[] payload)
    {
        header = default;
        payload = [];
        if (buffer.Length < HeaderSize)
            return false;
        Span<byte> headerBytes = stackalloc byte[HeaderSize];
        buffer.Slice(0, HeaderSize).CopyTo(headerBytes);
        if (!TryReadHeader(headerBytes, out header))
            throw new PacketFormatException("frame header integrity failure", 0);
        if (header.PayloadLength < 0 || header.PayloadLength > maxPayloadBytes)
            throw new PacketFormatException($"frame payload length {header.PayloadLength} exceeds max_payload_bytes", 0);
        if (buffer.Length < HeaderSize + header.PayloadLength)
            return false;
        payload = new byte[header.PayloadLength];
        buffer.Slice(HeaderSize, header.PayloadLength).CopyTo(payload);
        if (!ValidatePayload(header, payload))
            throw new PacketFormatException("frame payload integrity failure", HeaderSize);
        buffer = buffer.Slice(HeaderSize + header.PayloadLength);
        return true;
    }

    private static byte Xor(ReadOnlySpan<byte> data)
    {
        byte value = 0;
        foreach (var b in data)
            value ^= b;
        return value;
    }
}
