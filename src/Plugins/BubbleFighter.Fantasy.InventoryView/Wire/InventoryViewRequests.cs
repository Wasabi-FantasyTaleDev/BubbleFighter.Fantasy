using System.Text;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InventoryView.Wire;

/// <summary>
/// Parsed C2S 806 request (<c>gcpReqInventoryUseItem</c>). Builder
/// <c>sub_80DA80</c> at <c>0x80DA80</c> writes two leading <c>i32</c> values, then
/// the counted string list <c>sub_429D10</c> at <c>0x429D10</c>: a <c>u32</c>
/// record count (the byte span divided by the 24-byte MSVC wstring stride) and one
/// prefixed wstring per record (<c>u32</c> character count + UTF-16LE). The only
/// builder caller <c>sub_805C50</c> is obfuscated, so live field values stay
/// unpinned.
/// </summary>
/// <remarks>
/// Wire order: <c>i32 unknown0</c>, <c>i32 unknown1</c>, <c>u32 count</c>, then
/// count x (<c>u32 chars</c> + chars x UTF-16LE). The empty list is 12 bytes. No
/// S2C reply to this packet is proven anywhere in the client code, so the plugin logs
/// it fire-and-forget instead of answering.
/// </remarks>
/// <param name="Unknown0">The first leading <c>i32</c> (<c>a3[0]</c>).</param>
/// <param name="Unknown1">The second leading <c>i32</c> (<c>a3[1]</c>).</param>
/// <param name="Records">The counted prefixed-wstring list, in wire order.</param>
public sealed record InventoryViewRequest(int Unknown0, int Unknown1, IReadOnlyList<string> Records)
{
    /// <summary>Maximum prefixed-wstring character count accepted by the client stream at 0x1126430.</summary>
    public const int WideStringCharCap = 0x4000;

    /// <summary>
    /// Parses the payload strictly; any missing, trailing or oversized field throws
    /// <see cref="PacketFormatException"/>. String counts above the client writer
    /// cap <see cref="WideStringCharCap"/> are rejected because
    /// the client could not have produced them.
    /// </summary>
    /// <param name="payload">The decrypted payload.</param>
    /// <returns>The parsed request.</returns>
    /// <exception cref="PacketFormatException">Thrown when the body does not match the builder <c>sub_80DA80</c> shape.</exception>
    public static InventoryViewRequest Parse(ReadOnlySpan<byte> payload)
    {
        var r = new PacketReader(payload);
        var unknown0 = r.ReadInt32();
        var unknown1 = r.ReadInt32();
        var count = r.ReadUInt32();
        if (count > r.Remaining / 4)
            throw new PacketFormatException(
                $"C2S 806 record count {count} cannot fit in the {r.Remaining} byte(s) that follow it.",
                payload.Length - r.Remaining);
        var records = new string[count];
        for (var i = 0; i < count; i++)
        {
            var chars = r.ReadUInt32();
            if (chars > WideStringCharCap)
                throw new PacketFormatException(
                    $"C2S 806 record {i} declares {chars} characters, above the client writer cap {WideStringCharCap} (sub_1126430).",
                    payload.Length - r.Remaining);
            records[i] = Encoding.Unicode.GetString(r.ReadBytes((int)chars * 2));
        }
        if (r.Remaining != 0)
            throw new PacketFormatException(
                $"C2S 806 has {r.Remaining} trailing byte(s).",
                payload.Length - r.Remaining);
        return new InventoryViewRequest(unknown0, unknown1, records);
    }
}
