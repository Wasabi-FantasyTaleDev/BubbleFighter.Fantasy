using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// C2S 624, the death report (8 bytes): <c>sub_708B60</c> writes two 4-byte fields - the first from
/// the wrapper's source dword, the second the source byte zero-extended to <c>i32</c> (sub_708B60). Live
/// senders write <c>{1, 0}</c> on the death path <c>sub_B38850</c>, back-to-back with the empty C2S 625, and
/// <c>{0, 0}</c> on the timer paths. The field meanings are not recovered, so they keep the unknownN names; the
/// S2C census has no 624 row, so the parsed values are only logged - nothing is relayed or answered.
/// </summary>
/// <param name="Unknown0">The first dword; live sites write 0 or 1.</param> <param name="Unknown1">The second
/// dword; the wrapper always writes 0.</param>
public readonly record struct DeathReport(int Unknown0, int Unknown1)
{
    /// <summary>The body length every constructed frame carries (2 x i32).</summary>
    public const int BodyLength = 8;

    /// <summary>Parses a complete C2S 624 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static DeathReport Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != BodyLength)
            throw new PacketFormatException(
                $"death report 624 needs exactly {BodyLength} bytes, got {payload.Length}", payload.Length);
        var r = new PacketReader(payload);
        var first = r.ReadInt32();
        var second = r.ReadInt32();
        return new DeathReport(first, second);
    }
}
