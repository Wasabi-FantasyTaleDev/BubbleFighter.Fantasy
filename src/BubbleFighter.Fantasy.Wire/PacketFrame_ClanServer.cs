using System.Buffers;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Wire;

/// <summary>
/// Codec for the clan-server wire frame, a 4-byte little-endian header
/// <c>u16 bodyLength, u16 opcode</c> followed by the body, with per-frame XOR cipher.
/// The frame layout and cipher are build-independent constants: kr-1256, kr-812, CN2014~2019,
/// ID2014/2015, TW2019 use identical 256-byte key tables at the same file offset, and the
/// cipher runs on every frame whose session flag at <c>this+4</c> is set (the static
/// initializer <c>kr-1256 sub_401870</c> sets it to 1 for the global clan session).
/// The receive loop (<c>kr-1256 sub_11143A0</c>) stops at <c>bodyLength >= 0xFFFC</c>,
/// so the largest body is 65531 bytes.
/// </summary>
/// <remarks>
/// Frame writers: <c>kr-1256 sub_1114A80</c> (send), <c>kr-1256 sub_11143A0</c> (receive).
/// Cipher: <c>kr-1256 sub_1114A80 + KeyTable[(opcode + body.Length) % 255]</c> for send,
/// <c>kr-1256 sub_11143A0</c> for receive. The key table is at VA 0x1891F28 in kr-1256's
/// unpacked exe (file offset 21569320), and identical in every build with a clan connection.
/// kr-812: frame <c>sub_A518F0</c>, cipher <c>sub_A51A20 + sub_A51A90</c>, key lookup <c>sub_A517C0</c>.
/// </remarks>
public static class PacketFrame_ClanServer
{
    /// <summary>The frame header byte length.</summary>
    public const int HeaderSize = 4;

    /// <summary>
    /// The largest body the receive loop accepts. The length field is 16-bit, but the
    /// client stops at <c>bodyLength >= 0xFFFC</c> (<c>kr-1256 sub_11143A0</c>), so
    /// the maximum body is 65531 bytes.
    /// </summary>
    public const int MaxBodyBytes = 0xFFFB;

    /// <summary>
    /// The 256-byte XOR key table used by every client build with a clan connection.
    /// Extracted from kr-1256 VA 0x1891F28 (file offset 21569320 in BubbleFighter.Unpacked.exe),
    /// and verified byte-identical in kr-812, CN2014 0.2.46/0.2.48/0.2.50/0.2.51, CN2015,
    /// CN2016 0.2.70/0.2.72, CN2017 0.2.74/0.2.80, CN2019 0.2.98, ID2014, ID2015 and TW2019.
    /// </summary>
    private static readonly byte[] KeyTable =
    [
        0x8a, 0x0c, 0x85, 0xf3, 0xcc, 0x82, 0x5f, 0xc1, 0x92, 0x56, 0x57, 0x6a, 0xaa, 0x03, 0xf0, 0xba,
        0x70, 0xe7, 0x87, 0x7a, 0xbb, 0x38, 0x84, 0x9e, 0x4c, 0x18, 0x99, 0x78, 0x50, 0x4d, 0xc5, 0x5a,
        0x0f, 0x95, 0x5b, 0xf2, 0x5a, 0x99, 0x34, 0xea, 0x49, 0x1a, 0xc4, 0x89, 0x64, 0xb5, 0xd3, 0xd7,
        0x39, 0x72, 0xea, 0x18, 0x18, 0xed, 0x60, 0x06, 0x53, 0x51, 0xfd, 0xac, 0x56, 0x7a, 0x20, 0xd6,
        0x65, 0x72, 0x63, 0xd2, 0xdb, 0x12, 0x35, 0x9c, 0xc0, 0x10, 0x52, 0xa9, 0xf7, 0x3d, 0x90, 0x57,
        0xfb, 0xa8, 0x89, 0x4c, 0x55, 0x0a, 0x68, 0x43, 0x42, 0x92, 0x8f, 0x8b, 0xcb, 0x09, 0x41, 0x3e,
        0x2d, 0xde, 0x87, 0x76, 0x4c, 0x93, 0x0c, 0xf8, 0x37, 0x11, 0x12, 0xb1, 0xd3, 0xec, 0xcc, 0x6e,
        0xba, 0x21, 0x3f, 0xa2, 0x72, 0x2f, 0xd9, 0xc0, 0x7c, 0x4f, 0x92, 0x63, 0xe2, 0x85, 0x22, 0xcf,
        0x4c, 0xcc, 0x17, 0x8f, 0x30, 0x38, 0x08, 0x30, 0x36, 0x2b, 0xfb, 0x5b, 0xea, 0x8d, 0x50, 0x66,
        0x3c, 0xa4, 0xcc, 0x7a, 0x75, 0x70, 0x96, 0x7b, 0x29, 0xaf, 0x32, 0x59, 0xcf, 0x71, 0xda, 0xdc,
        0xeb, 0xdf, 0x44, 0x29, 0x0b, 0x11, 0x9d, 0x8a, 0x06, 0x20, 0x6b, 0xb3, 0xb4, 0x5b, 0x06, 0x19,
        0x0d, 0x34, 0x56, 0x05, 0xe2, 0xd8, 0xa1, 0x05, 0x37, 0x8e, 0x7a, 0xe4, 0x4b, 0x45, 0xa9, 0xca,
        0xf8, 0x70, 0x24, 0x9f, 0x67, 0x1f, 0x61, 0xe2, 0xb4, 0xe4, 0xa0, 0x9d, 0x2a, 0x06, 0x7e, 0xf9,
        0x7b, 0x02, 0x65, 0x4b, 0xc9, 0xe2, 0xa4, 0x01, 0xd3, 0x7b, 0xdc, 0xd1, 0x92, 0xe7, 0x70, 0x16,
        0x24, 0x0b, 0xb3, 0xa3, 0xd2, 0xda, 0x8f, 0xaa, 0x95, 0x24, 0xbd, 0xcd, 0xca, 0x32, 0xec, 0x0b,
        0x1a, 0x73, 0x64, 0xa5, 0x36, 0x81, 0xed, 0xab, 0xf5, 0x3a, 0xae, 0x40, 0xe5, 0x3a, 0xb4, 0x4b,
    ];

    /// <summary>Encodes a frame with per-frame XOR cipher.</summary>
    /// <param name="opcode">The wire opcode.</param>
    /// <param name="body">The body bytes.</param>
    /// <returns>The complete ciphered frame.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the body exceeds <see cref="MaxBodyBytes"/>.</exception>
    public static byte[] Encode(ushort opcode, ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxBodyBytes)
            throw new ArgumentOutOfRangeException(nameof(body), body.Length,
                $"Clan frame body is limited to {MaxBodyBytes} bytes.");
        var frame = new byte[HeaderSize + body.Length];
        Encode(opcode, body, frame);
        return frame;
    }

    /// <summary>Encodes the frame into a caller buffer.</summary>
    /// <param name="opcode">The wire opcode.</param>
    /// <param name="body">The body bytes.</param>
    /// <param name="destination">At least <see cref="HeaderSize"/> plus the body length bytes.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the body exceeds <see cref="MaxBodyBytes"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is too short.</exception>
    public static int Encode(ushort opcode, ReadOnlySpan<byte> body, Span<byte> destination)
    {
        if (body.Length > MaxBodyBytes)
            throw new ArgumentOutOfRangeException(nameof(body), body.Length,
                $"Clan frame body is limited to {MaxBodyBytes} bytes.");
        var length = HeaderSize + body.Length;
        if (destination.Length < length)
            throw new ArgumentException($"The frame needs {length} bytes.", nameof(destination));

        // Write header (length + opcode) in the clear
        BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)body.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], opcode);

        // Copy body and apply per-frame XOR
        body.CopyTo(destination[HeaderSize..]);
        ApplyCipher(opcode, body.Length, destination[HeaderSize..(HeaderSize + body.Length)]);

        return length;
    }

    /// <summary>
    /// Tries to consume one complete frame from the head of a receive buffer, advancing
    /// it only when a whole frame was consumed. The frame is deciphered in place.
    /// </summary>
    /// <param name="buffer">The receive buffer.</param>
    /// <param name="maxBodyBytes">The maximum accepted body length.</param>
    /// <param name="opcode">Receives the opcode.</param>
    /// <param name="body">Receives a freshly allocated body; empty when the frame is incomplete.</param>
    /// <returns><see langword="true"/> when a frame was consumed; <see langword="false"/> when more bytes are needed.</returns>
    /// <exception cref="PacketFormatException">Thrown when the declared length exceeds
    /// <paramref name="maxBodyBytes"/> or the client's own 65531-byte bound.</exception>
    public static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, int maxBodyBytes, out ushort opcode, out byte[] body)
    {
        opcode = 0;
        body = [];
        if (buffer.Length < HeaderSize)
            return false;

        Span<byte> header = stackalloc byte[HeaderSize];
        buffer.Slice(0, HeaderSize).CopyTo(header);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header);

        // The client stops at bodyLength >= 0xFFFC (kr-1256 sub_11143A0)
        if (length >= 0xFFFC)
            throw new PacketFormatException($"clan frame body length {length} exceeds the client bound 0xFFFB", 0);
        if (length > maxBodyBytes)
            throw new PacketFormatException($"clan frame body length {length} exceeds max_body_bytes {maxBodyBytes}", 0);
        if (buffer.Length < HeaderSize + length)
            return false;

        opcode = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        body = new byte[length];
        buffer.Slice(HeaderSize, length).CopyTo(body);

        // Decipher the body
        ApplyCipher(opcode, length, body);

        buffer = buffer.Slice(HeaderSize + length);
        return true;
    }

    /// <summary>
    /// Applies the per-frame XOR cipher in place. The same operation enciphers and deciphers,
    /// as the cipher is symmetric per frame.
    /// </summary>
    /// <param name="opcode">The frame's opcode.</param>
    /// <param name="bodyLength">The body length.</param>
    /// <param name="body">The body span to transform.</param>
    /// <remarks>
    /// Cipher formula from <c>kr-1256 sub_1114A80</c> (send) and <c>kr-1256 sub_11143A0</c> (receive):
    /// <code>
    /// var key = KeyTable[(opcode + body.Length) % 255];
    /// for (var i = 0; i &lt; body.Length; i++)
    ///     body[i] ^= unchecked((byte)(key + key % (i + 1)));
    /// </code>
    /// kr-812 equivalents: <c>sub_A51A20 + sub_A516E0</c> (send), <c>sub_A51A90 + sub_A51750</c> (receive).
    /// </remarks>
    private static void ApplyCipher(ushort opcode, int bodyLength, Span<byte> body)
    {
        var key = KeyTable[(opcode + bodyLength) % 255];
        for (var i = 0; i < body.Length; i++)
            body[i] ^= unchecked((byte)(key + key % (i + 1)));
    }
}
