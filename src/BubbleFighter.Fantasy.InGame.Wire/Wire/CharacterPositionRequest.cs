using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// C2S 301 / C2S 511 character position report. Builder <c>sub_913640</c> at <c>0x913640</c> emits
/// the same seven-field body under both opcodes: opcode 301 while no room session exists (plaza,
/// <c>dword_18E56A4 == null</c>) and opcode 511 inside a room. The body is 22 bytes; the S2C echo is the same
/// body with the sender identity prepended (26 bytes). Evidence: verified widths, order and the 301/511 opcode
/// switch. Field roles past the counter are inferred; the body is relayed opaquely rather than re-derived.
/// </summary> <param name="Counter">Sequence counter from <c>this[426]</c>; compared by the client on
/// apply.</param> <param name="PositionX">First position float (axis order inferred).</param> <param
/// name="PositionY">Second position float (axis order inferred).</param> <param name="PositionZ">Third position
/// float (axis order inferred).</param> <param name="FlagA">First toggle byte (<c>this+1472</c>).</param>
/// <param name="FlagB">Second toggle byte (<c>this+1473</c>).</param> <param name="State">Character state word
/// (<c>this[152]</c>).</param>
public sealed record CharacterPositionRequest(
    int Counter,
    int PositionX,
    int PositionY,
    int PositionZ,
    byte FlagA,
    byte FlagB,
    int State)
{
    /// <summary>Byte length of one C2S 301 / C2S 511 body.</summary>
    public const int BodyBytes = 22;

    /// <summary>Byte length of one S2C 301 / S2C 514 body (identity plus the C2S body).</summary>
    public const int ReplyBytes = 26;

    /// <summary>Parses a complete C2S 301 / C2S 511 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static CharacterPositionRequest Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var counter = r.ReadInt32();
        var x = r.ReadInt32();
        var y = r.ReadInt32();
        var z = r.ReadInt32();
        var flagA = r.ReadByte();
        var flagB = r.ReadByte();
        var state = r.ReadInt32();
        WireUtil.RequireConsumed(r, "C2S 301/511");
        return new CharacterPositionRequest(counter, x, y, z, flagA, flagB, state);
    }

    /// <summary>Writes the 22-byte body in wire order.</summary>
    /// <param name="w">The destination writer.</param>
    public void Write(PacketWriter w)
    {
        w.WriteInt32(Counter);
        w.WriteInt32(PositionX);
        w.WriteInt32(PositionY);
        w.WriteInt32(PositionZ);
        w.WriteByte(FlagA);
        w.WriteByte(FlagB);
        w.WriteInt32(State);
    }

    /// <summary>
    /// Writes the 26-byte S2C body: the sender identity followed by this
    /// request's 22 bytes verbatim. The client applies it through
    /// <c>sub_91FB80</c> and self-filters on the identity, so the identity must
    /// be the sender's character id.
    ///
    /// </summary>
    /// <param name="w">The destination writer.</param>
    /// <param name="identity">The sending character id.</param>
    public void WriteReply(PacketWriter w, int identity)
    {
        w.WriteInt32(identity);
        Write(w);
    }
}
