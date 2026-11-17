using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Wire;

/// <summary>
/// The clan-server wire dialect: no preamble, identity stream transforms, and the
/// 4-byte clan frame with per-frame XOR cipher. This dialect is build-independent;
/// every client build with a clan connection uses the same frame layout, cipher and
/// 256-byte key table.
/// </summary>
internal sealed class ClanDialect : WireDialect
{
    private readonly ClientProfile _profile;

    public ClanDialect(ClientProfile profile)
    {
        _profile = profile;
    }

    public override ClientProfile Profile => _profile;

    public override int MaxFrameOverheadBytes => PacketFrame_ClanServer.HeaderSize;

    public override int PreambleLength(PacketDirection direction) => 0;

    public override byte[] BuildServerPreamble(ushort status, string commandLine) => [];

    public override IStreamTransform CreateDecryptor(PacketDirection direction) => IdentityTransform.Instance;

    public override IStreamTransform CreateEncryptor(PacketDirection direction) => IdentityTransform.Instance;

    public override bool TryReadFrame(ref System.Buffers.ReadOnlySequence<byte> plaintext, int maxPayloadBytes, out DecodedFrame frame)
    {
        if (PacketFrame_ClanServer.TryReadFrame(ref plaintext, maxPayloadBytes, out var opcode, out var body))
        {
            frame = new DecodedFrame(opcode, body);
            return true;
        }
        frame = default;
        return false;
    }

    public override byte[] EncodeFrame(ushort opcode, ReadOnlySpan<byte> payload) =>
        PacketFrame_ClanServer.Encode(opcode, payload);

    public override int EncodeFrame(ushort opcode, ReadOnlySpan<byte> payload, Span<byte> destination) =>
        PacketFrame_ClanServer.Encode(opcode, payload, destination);

    private sealed class IdentityTransform : IStreamTransform
    {
        public static readonly IdentityTransform Instance = new();
        public void Apply(Span<byte> buffer) { }
    }
}

/// <summary>Extension methods for registering the build-independent clan dialect.</summary>
public static class ClanDialectExtensions
{
    /// <summary>
    /// Creates the build-independent clan-server dialect for a profile.
    /// </summary>
    /// <param name="profile">The client profile.</param>
    /// <returns>The clan dialect.</returns>
    public static IWireDialect CreateClanDialect(this ClientProfile profile) =>
        new ClanDialect(profile);
}
