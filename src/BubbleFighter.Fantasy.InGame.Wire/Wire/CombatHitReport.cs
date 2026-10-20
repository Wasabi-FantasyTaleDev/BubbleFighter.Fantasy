using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// C2S/S2C 38283, the in-session combat hit-resolution frame. The dispatcher case (<c>sub_6D55D0</c>
/// block <c>0x6D8679-0x6D873E</c>) reads exactly <see cref="BodyBytes"/> bytes through seven stream reads: an
/// <c>i32</c> damager key resolved through the world manager's FNV table (a miss is dropped silently), an
/// <c>i32</c> target object id, three binary16 <c>u16</c> hit coordinates (<c>sub_FA5360</c> decodes them to
/// f32), one <c>u8</c> mode (observed 0 and 1 only) and one <c>i32</c> the case collapses to a bool. The server
/// only relays the frame, so the fields are documented here and never parsed.
/// </summary>
public static class CombatHitReport
{
    /// <summary>
    /// Body length the dispatcher case consumes: 4 + 4 + 2 + 2 + 2 + 1 + 4 = 19,
    /// matching every frame the client sends. Length pin for the byte-length test the
    /// opcode policy requires.
    ///
    /// </summary>
    public const int BodyBytes = 19;

    /// <summary>
    /// Pins the payload to exactly <see cref="BodyBytes"/> bytes without decoding any field. The
    /// compatibility relay preserves the complete body rather than assigning meanings to unmodeled fields. A
    /// short or long body is a malformed packet. </summary> <param name="payload">The decrypted
    /// payload.</param> <param name="name">Opcode label for the error.</param>
    public static void Validate(ReadOnlySpan<byte> payload, string name)
    {
        if (payload.Length != BodyBytes)
            throw new PacketFormatException($"{name} must be {BodyBytes} bytes, got {payload.Length}.", 0);
    }
}
