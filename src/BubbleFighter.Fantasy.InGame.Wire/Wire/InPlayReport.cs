using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// C2S 32781: i32 actor, i32 unknown0, bool32 flag. The fallback dispatcher 0x6D8E90
/// resolves Character and calls virtual +0x1E4 or +0x1E8. The body is 12 bytes.
/// </summary>
/// <remarks>
/// This character action is separate from connection latency reports. Unknown values
/// are preserved by the match relay; the second field has no verified sequence semantics.
/// </remarks>
/// <param name="Identity">The million-scale identity, 1000000 on the observed frames.</param>
/// <param name="Sequence">Second <c>i32</c>.</param>
/// <param name="Flag">Trailing <c>i32</c>, 1 on the observed frames.</param>
public readonly record struct InPlayReport(int Identity, int Sequence, int Flag)
{
    /// <summary>The body length every frame carries.</summary>
    public const int BodyLength = 12;

    /// <summary>Parses a complete C2S 32781 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static InPlayReport Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var identity = r.ReadInt32();
        var sequence = r.ReadInt32();
        var flag = r.ReadInt32();
        WireUtil.RequireConsumed(r, "C2S 32781");
        return new InPlayReport(identity, sequence, flag);
    }
}
