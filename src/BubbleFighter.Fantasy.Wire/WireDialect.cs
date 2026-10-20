using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Wire;

/// <summary>
/// Everything about the byte stream that differs between client builds: the plaintext
/// preamble each side writes before the cipher starts, the stream cipher, and the frame
/// layout. Message layouts are not part of a dialect; they belong to the codecs.
/// A build's dialect is data: its profile names the frame layout and, where its binary ships
/// its own, the cipher tables (see <see cref="Create"/>), so a new build needs no code.
/// </summary>
public abstract partial class WireDialect : IWireDialect
{
    /// <summary>Creates the dialect of one client build from its profile identity and stream data.</summary>
    /// <param name="profile">The build's identity: profile id, region, executable major and resource revision.</param>
    /// <param name="stream">The frame layout and cipher tables the build's binary uses.</param>
    /// <returns>A stateless dialect; each connection creates its own cipher state.</returns>
    public static WireDialect Create(ClientProfile profile, WireStream stream)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(stream);
        return new Configured(profile, stream);
    }

    /// <summary>Gets the dialect id, the client profile id it serves (for example <c>kr-1256</c>).</summary>
    public string Id => Profile.Id;

    /// <summary>Gets a one-line description of the build the dialect serves.</summary>
    public string Description => Profile.Description;

    /// <inheritdoc />
    public abstract ClientProfile Profile { get; }

    /// <inheritdoc />
    public abstract int MaxFrameOverheadBytes { get; }

    /// <inheritdoc />
    public abstract byte[] BuildServerPreamble(ushort status, string commandLine);

    /// <summary>
    /// Gets the number of plaintext bytes a side writes when the connection opens, before
    /// its cipher starts; zero when that side writes none.
    /// </summary>
    /// <param name="direction">The direction the preamble travels.</param>
    /// <returns>The preamble byte length.</returns>
    public abstract int PreambleLength(PacketDirection direction);

    /// <summary>Creates the transform that turns received ciphertext into plaintext.</summary>
    /// <param name="direction">The direction the bytes travel.</param>
    /// <returns>A fresh transform positioned at the start of the stream.</returns>
    public abstract IStreamTransform CreateDecryptor(PacketDirection direction);

    /// <summary>Creates the transform that turns plaintext into ciphertext for sending.</summary>
    /// <param name="direction">The direction the bytes travel.</param>
    /// <returns>A fresh transform positioned at the start of the stream.</returns>
    public abstract IStreamTransform CreateEncryptor(PacketDirection direction);

    /// <summary>
    /// Tries to consume one complete frame from the head of a plaintext buffer; advances the
    /// buffer only when a whole frame was consumed.
    /// </summary>
    /// <param name="plaintext">The decrypted buffer.</param>
    /// <param name="maxPayloadBytes">The largest payload accepted.</param>
    /// <param name="frame">Receives the frame.</param>
    /// <returns><see langword="true"/> when a frame was consumed; <see langword="false"/> when more bytes are needed.</returns>
    /// <exception cref="PacketFormatException">Thrown when the frame fails the dialect's integrity checks or exceeds the limit.</exception>
    public abstract bool TryReadFrame(ref ReadOnlySequence<byte> plaintext, int maxPayloadBytes, out DecodedFrame frame);

    /// <summary>Encodes one plaintext frame, integrity bytes included.</summary>
    /// <param name="opcode">The wire opcode.</param>
    /// <param name="payload">The payload bytes.</param>
    /// <returns>The plaintext frame.</returns>
    public abstract byte[] EncodeFrame(ushort opcode, ReadOnlySpan<byte> payload);

    /// <inheritdoc />
    public override string ToString() => Id;

    /// <summary>
    /// The one dialect implementation. Every known build writes nothing before its cipher starts and
    /// reads the server's 258-byte plaintext greeting (<see cref="BootstrapGreeting"/>); the cipher is
    /// <see cref="SimpleCipher"/> seeded (0, 1) client-to-server and (5, 3) server-to-client (kr-812
    /// sub_B26AE0, id-400 sub_A9FAD0, cn-404 sub_AD5390, cn-279 sub_570560); the frame and the table
    /// bytes are the build's data.
    /// </summary>
    private sealed class Configured(ClientProfile profile, WireStream stream) : WireDialect
    {
        public override ClientProfile Profile { get; } = profile;

        public override int MaxFrameOverheadBytes => stream.Frame switch
        {
            WireFrameKind.Modern8 => WireFrame.HeaderSize,
            WireFrameKind.Legacy5 => LegacyFrame.HeaderSize,
            _ => Legacy10Frame.HeaderSize,
        };

        public override byte[] BuildServerPreamble(ushort status, string commandLine) => BootstrapGreeting.Build(status, commandLine);

        public override int PreambleLength(PacketDirection direction) =>
            direction == PacketDirection.ServerToClient ? BootstrapGreeting.Size : 0;

        public override IStreamTransform CreateDecryptor(PacketDirection direction) =>
            new CipherTransform(CreateCipher(direction), decrypt: true);

        public override IStreamTransform CreateEncryptor(PacketDirection direction) =>
            new CipherTransform(CreateCipher(direction), decrypt: false);

        public override bool TryReadFrame(ref ReadOnlySequence<byte> plaintext, int maxPayloadBytes, out DecodedFrame frame)
        {
            frame = default;
            ushort opcode;
            byte[] payload;
            switch (stream.Frame)
            {
                case WireFrameKind.Modern8:
                    if (!WireFrame.TryReadFrame(ref plaintext, maxPayloadBytes, out var header, out payload))
                        return false;
                    opcode = header.Opcode;
                    break;
                case WireFrameKind.Legacy5:
                    if (!LegacyFrame.TryReadFrame(ref plaintext, maxPayloadBytes, out opcode, out payload))
                        return false;
                    break;
                default:
                    if (!Legacy10Frame.TryReadFrame(ref plaintext, maxPayloadBytes, out opcode, out payload))
                        return false;
                    break;
            }
            frame = new DecodedFrame(opcode, payload);
            return true;
        }

        public override byte[] EncodeFrame(ushort opcode, ReadOnlySpan<byte> payload) => stream.Frame switch
        {
            WireFrameKind.Modern8 => WireFrame.Encode(opcode, payload),
            WireFrameKind.Legacy5 => LegacyFrame.Encode(opcode, payload),
            _ => Legacy10Frame.Encode(opcode, payload),
        };

        private SimpleCipher CreateCipher(PacketDirection direction)
        {
            var (seedA, seedB) = direction == PacketDirection.ClientToServer ? (0, 1) : (5, 3);
            return stream.CipherTableA is { } a && stream.CipherTableB is { } b
                ? new SimpleCipher(a, b, seedA, seedB)
                : new SimpleCipher(seedA, seedB);
        }

        private sealed class CipherTransform(SimpleCipher cipher, bool decrypt) : IStreamTransform
        {
            public void Apply(Span<byte> data)
            {
                if (decrypt)
                    cipher.DecryptInPlace(data);
                else
                    cipher.EncryptInPlace(data);
            }
        }
    }
}

/// <summary>How a build lays out one frame on the wire.</summary>
public enum WireFrameKind
{
    /// <summary>The eight-byte <see cref="WireFrame"/> of the final Korean build (<c>modern_8</c>).</summary>
    Modern8,

    /// <summary>The five-byte <see cref="LegacyFrame"/> of the builds before it (<c>legacy_5</c>).</summary>
    Legacy5,

    /// <summary>The ten-byte <see cref="Legacy10Frame"/> of the oldest clients, such as the Chinese 2011 one (<c>legacy_10</c>).</summary>
    Legacy10,
}

/// <summary>The stream data a build's binary fixes: its frame layout and, when it ships its own, the cipher tables.</summary>
public sealed record WireStream
{
    /// <summary>Creates stream data; both tables are given together or not at all.</summary>
    /// <param name="frame">The frame layout.</param>
    /// <param name="cipherTableA">The build's first cipher table, or <see langword="null"/> for the shared pair.</param>
    /// <param name="cipherTableB">The build's second cipher table, or <see langword="null"/> for the shared pair.</param>
    /// <exception cref="ArgumentException">Thrown when only one table is given or a table is empty.</exception>
    public WireStream(WireFrameKind frame, byte[]? cipherTableA = null, byte[]? cipherTableB = null)
    {
        if ((cipherTableA is null) != (cipherTableB is null) || cipherTableA is { Length: 0 } || cipherTableB is { Length: 0 })
            throw new ArgumentException("Cipher tables are given as a non-empty pair or not at all.");
        Frame = frame;
        CipherTableA = cipherTableA;
        CipherTableB = cipherTableB;
    }

    /// <summary>Gets the frame layout: Modern8, Legacy5 or Legacy10, represented in
    /// profile text as modern_8, legacy_5 or legacy_10 respectively.</summary>
    public WireFrameKind Frame { get; }

    /// <summary>Gets the nonempty first cipher table, paired with CipherTableB, or
    /// <see langword="null"/> for the shared pair. The supplied array is borrowed;
    /// keep it immutable for the lifetime of dialects created from this stream.</summary>
    public byte[]? CipherTableA { get; }

    /// <summary>Gets the nonempty second cipher table, paired with CipherTableA, or
    /// <see langword="null"/> for the shared pair. The supplied array is borrowed;
    /// keep it immutable for the lifetime of dialects created from this stream.</summary>
    public byte[]? CipherTableB { get; }

    /// <summary>Reads stream data from profile text: the frame name and optional hexadecimal tables.</summary>
    /// <param name="frame"><c>modern_8</c>, <c>legacy_5</c> or <c>legacy_10</c>.</param>
    /// <param name="cipherTableA">Hexadecimal bytes, or empty for the shared pair.</param>
    /// <param name="cipherTableB">Hexadecimal bytes, or empty for the shared pair.</param>
    /// <returns>The stream data.</returns>
    /// <exception cref="ArgumentException">Thrown for an unknown frame name or malformed tables.</exception>
    public static WireStream Parse(string frame, string? cipherTableA, string? cipherTableB)
    {
        var kind = frame switch
        {
            "modern_8" => WireFrameKind.Modern8,
            "legacy_5" => WireFrameKind.Legacy5,
            "legacy_10" => WireFrameKind.Legacy10,
            _ => throw new ArgumentException($"Unknown wire frame '{frame}'; use modern_8, legacy_5 or legacy_10.", nameof(frame)),
        };
        return new WireStream(kind, Hex(cipherTableA), Hex(cipherTableB));

        static byte[]? Hex(string? text) => string.IsNullOrWhiteSpace(text) ? null : Convert.FromHexString(text);
    }
}
