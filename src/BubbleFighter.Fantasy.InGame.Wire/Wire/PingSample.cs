using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>
/// C2S 518 body, decoded against the arguments of <c>sub_978F20</c>
/// (<c>0x978F20</c>), the handler <c>sub_9D9930</c> reaches for this opcode.
/// </summary>
/// <remarks>
/// <para>
/// The client code validates the seat index against the session seat array at
/// <c>session+592</c> with stride 320 (<c>sub_978F20</c>), pushes a latency
/// sample into a per-seat ring buffer capped at five entries
/// (<c>:36-52</c>), and stores the running mean at
/// <c>session+144+4*seat</c> (<c>:96</c>). The trailing <c>i32</c> is stored
/// verbatim at <c>session+324+4*seat</c> (<c>:97</c>).
/// </para>
/// <para>
/// The latency the handler actually averages is
/// <c>dword_18E8F10 - *(session+1044)</c>, a client-local clock difference, so
/// only the seat index and the trailing stamp come off the wire. Both reserved
/// leading fields are read by the dispatcher's own prologue, not by this
/// handler; they are kept here because every frame carries them.
/// </para>
/// </remarks>
/// <param name="Reserved0">Leading <c>i32</c>.</param>
/// <param name="Reserved1">Second <c>i32</c>.</param>
/// <param name="Seat">Seat index, bounds-checked at <c>sub_978F20</c>.</param>
/// <param name="Stamp">Trailing <c>i32</c> stored at <c>session+324+4*seat</c>.</param>
public readonly record struct PingSample(int Reserved0, int Reserved1, int Seat, int Stamp)
{
    /// <summary>The body length every frame carries.</summary>
    public const int BodyLength = 16;

    /// <summary>Parses a complete C2S 518 payload.</summary>
    /// <param name="payload">The decrypted payload.</param>
    public static PingSample Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var a = r.ReadInt32();
        var b = r.ReadInt32();
        var seat = r.ReadInt32();
        var stamp = r.ReadInt32();
        WireUtil.RequireConsumed(r, "C2S 518");
        return new PingSample(a, b, seat, stamp);
    }
}
