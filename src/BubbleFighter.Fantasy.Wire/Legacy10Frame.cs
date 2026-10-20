using System.Buffers;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Wire;

/// <summary>
/// The ten-byte frame of the oldest clients (profile frame <c>legacy_10</c>): u8 marker (255),
/// u8 unknown1, u16 payload length, u16 unknown2, u16 unknown3, u16 opcode, then the payload,
/// all integers little-endian. Constructor cn-279 sub_56E6F0 clears the header; cn-279 sub_56E6E0
/// sets the opcode and cn-279 sub_56E7E0 finalizes it. Receive path cn-279 sub_570BD0 checks the
/// marker and excludes the header from the length. There is no payload checksum; the three unknown fields are sent as zero.
/// </summary>
public static class Legacy10Frame
{
    /// <summary>The bytes preceding the payload in every cn-279 frame.</summary>
    public const int HeaderSize = 10;

    /// <summary>The largest payload representable by the unsigned 16-bit length.</summary>
    public const int MaxPayloadBytes = ushort.MaxValue;

    /// <summary>Builds a frame with the marker and zero-initialized unknown fields.</summary>
    /// <param name="opcode">The packet number in the connection's direction.</param>
    /// <param name="payload">The unframed message bytes.</param>
    /// <returns>The complete plaintext frame.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The payload exceeds the wire length field.</exception>
    public static byte[] Encode(ushort opcode, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length,
                $"A cn-279 frame payload is limited to {MaxPayloadBytes} bytes.");
        var frame = new byte[HeaderSize + payload.Length];
        frame[0] = 0xFF;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8), opcode);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>Consumes one complete frame without advancing an incomplete input.</summary>
    /// <param name="buffer">The decrypted input, advanced only on success.</param>
    /// <param name="maxPayloadBytes">The deployment's inclusive payload bound.</param>
    /// <param name="opcode">Receives the packet number, or zero when incomplete.</param>
    /// <param name="payload">Receives an owned payload, or an empty array when incomplete.</param>
    /// <returns>Whether a complete frame was consumed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The payload bound is negative.</exception>
    /// <exception cref="PacketFormatException">The marker is invalid or the payload exceeds the bound.</exception>
    public static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, int maxPayloadBytes,
        out ushort opcode, out byte[] payload)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxPayloadBytes);
        opcode = 0;
        payload = [];
        if (buffer.Length < HeaderSize)
            return false;
        Span<byte> header = stackalloc byte[HeaderSize];
        buffer.Slice(0, HeaderSize).CopyTo(header);
        if (header[0] != 0xFF)
            throw new PacketFormatException("cn-279 frame marker must be 255", 0);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        if (length > maxPayloadBytes)
            throw new PacketFormatException($"frame payload length {length} exceeds max_payload_bytes", 2);
        if (buffer.Length < HeaderSize + length)
            return false;
        payload = buffer.Slice(HeaderSize, length).ToArray();
        opcode = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        buffer = buffer.Slice(HeaderSize + length);
        return true;
    }
}
