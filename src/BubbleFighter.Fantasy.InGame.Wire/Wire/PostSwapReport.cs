using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// C2S 25, the weapon-preset post-swap report (5 bytes): one <c>i32</c> in-session actor id, then one
/// <c>u8</c> preset slot. Built inline by <c>sub_6DA420</c> (model[+448] actor + the 836 reply's slot) and sent
/// on the in-game session connection right after a status-zero S2C 836 apply. Log-only: no dispatcher case in
/// any surface consumes a reply, and the builder sets no wait-latch, so server silence cannot stall the client.
/// Dialect note: the clan-server empty bounce in sub_525170 shares the opcode with an empty body - this handler
/// is for the game dialect's 5-byte form only. </summary> <param name="ActorId">The in-session Character's
/// object id.</param> <param name="Slot">The preset slot the swap landed on.</param>
public readonly record struct PostSwapReport(int ActorId, byte Slot)
{
    /// <summary>The body length every game-dialect frame carries.</summary>
    public const int BodyLength = 5;

    /// <summary>Parses a complete C2S 25 payload (game dialect).</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static PostSwapReport Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != BodyLength)
            throw new PacketFormatException(
                $"post-swap report 25 needs exactly {BodyLength} bytes, got {payload.Length}", payload.Length);
        var r = new PacketReader(payload);
        var actor = r.ReadInt32();
        var slot = r.ReadByte();
        return new PostSwapReport(actor, slot);
    }
}
