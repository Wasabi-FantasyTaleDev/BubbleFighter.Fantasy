using System.Buffers;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Wire;

/// <summary>
/// Codec for the legacy wire frame used by the kr-812 build (header laid out by
/// <c>Lambda::TcpPacket</c>'s constructor <c>sub_B25EA0</c>, opcode written by
/// <c>sub_B25E90</c>, finalized by <c>sub_B25F90</c>): a 5-byte little-endian header
/// <c>u16 payload_length, u16 opcode, u8 payload_xor</c> followed by the payload.
/// <c>payload_xor</c> is the XOR of all payload bytes; unlike <see cref="WireFrame"/> the
/// header carries no integrity byte of its own.
/// </summary>
public static class LegacyFrame
{
    /// <summary>The frame header byte length.</summary>
    public const int HeaderSize = 5;

    /// <summary>The largest payload the 16-bit length field can describe.</summary>
    public const int MaxPayloadBytes = ushort.MaxValue;

    /// <summary>Encodes a frame with a valid payload integrity byte.</summary>
    /// <param name="opcode">The wire opcode.</param>
    /// <param name="payload">The payload bytes.</param>
    /// <returns>The complete plaintext frame.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the payload exceeds <see cref="MaxPayloadBytes"/>.</exception>
    public static byte[] Encode(ushort opcode, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length,
                $"A legacy frame payload is limited to {MaxPayloadBytes} bytes.");
        var frame = new byte[HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), opcode);
        frame[4] = Xor(payload);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>
    /// Tries to consume one complete frame from the head of a decrypted receive buffer,
    /// advancing it only when a whole frame was consumed.
    /// </summary>
    /// <param name="buffer">The decrypted receive buffer.</param>
    /// <param name="maxPayloadBytes">The maximum accepted payload length.</param>
    /// <param name="opcode">Receives the opcode.</param>
    /// <param name="payload">Receives a freshly allocated payload; empty when the frame is incomplete.</param>
    /// <returns><see langword="true"/> when a frame was consumed; <see langword="false"/> when more bytes are needed.</returns>
    /// <exception cref="PacketFormatException">Thrown when the declared length exceeds
    /// <paramref name="maxPayloadBytes"/> or the payload XOR mismatches.</exception>
    public static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, int maxPayloadBytes, out ushort opcode, out byte[] payload)
    {
        opcode = 0;
        payload = [];
        if (buffer.Length < HeaderSize)
            return false;
        Span<byte> header = stackalloc byte[HeaderSize];
        buffer.Slice(0, HeaderSize).CopyTo(header);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header);
        if (length > maxPayloadBytes)
            throw new PacketFormatException($"frame payload length {length} exceeds max_payload_bytes", 0);
        if (buffer.Length < HeaderSize + length)
            return false;
        var body = new byte[length];
        buffer.Slice(HeaderSize, length).CopyTo(body);
        if (Xor(body) != header[4])
            throw new PacketFormatException("frame payload integrity failure", HeaderSize);
        opcode = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        payload = body;
        buffer = buffer.Slice(HeaderSize + length);
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
