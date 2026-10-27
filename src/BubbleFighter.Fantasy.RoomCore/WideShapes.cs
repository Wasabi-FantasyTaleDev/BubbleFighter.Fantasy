using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.RoomCore;

/// <summary>
/// The counted-container and wstring-cap helpers the session and map-meta readers share. The client
/// rejects a wstring longer than 0x4000 characters (<c>kr-1256 sub_1126430</c>, reached on the
/// map-meta path through <c>kr-1256 sub_113D030</c> -> <c>kr-1256 sub_1119C10</c>). Its counted
/// vectors carry no fixed client cap — the kr-1256 readers (<c>kr-1256 sub_113FE90</c>,
/// <c>kr-1256 sub_10CB6C0</c>, <c>kr-1256 sub_10D2EA0</c>) enforce only the standard library's
/// maximum-size guard, which scales with element width — so the vector bound below is this
/// server's own safety limit, stricter than the client requires (bounded reads, so a malformed
/// install packet cannot allocate the session object out of memory).
/// </summary>
public static class WideShapes
{
    /// <summary>Client wstring cap: <c>N &gt; 0x4000</c> is rejected (kr-1256 sub_1126430, bounded read).</summary>
    public const int MaxWideChars = 0x4000;

    /// <summary>Server-side cap on counted vectors inside <c>MapMeta</c> / room blobs; the client enforces no fixed cap of its own.</summary>
    public const int MaxVectorCount = 1024;

    /// <summary>Reads one length-prefixed Unicode string under the client's cap.</summary>
    /// <param name="r">The reader.</param>
    /// <param name="field">The field name error messages cite.</param>
    public static string ReadWide(ref PacketReader r, string field)
    {
        var n = r.ReadUInt32();
        if (n > MaxWideChars)
            throw new PacketFormatException($"{field} wstring length {n} exceeds {MaxWideChars}.", r.Offset);
        return System.Text.Encoding.Unicode.GetString(r.ReadBytes(checked((int)n * 2)));
    }

    /// <summary>Reads one counted i32 vector under the client's cap.</summary>
    /// <param name="r">The reader.</param>
    /// <param name="field">The field name error messages cite.</param>
    public static int[] ReadInt32Vector(ref PacketReader r, string field)
    {
        var count = r.ReadUInt32();
        RequireCount(count, field);
        var values = new int[count];
        for (var i = 0; i < values.Length; i++)
            values[i] = r.ReadInt32();
        return values;
    }

    /// <summary>Writes one counted i32 vector.</summary>
    /// <param name="w">The writer.</param>
    /// <param name="values">The vector.</param>
    public static void WriteInt32Vector(PacketWriter w, int[] values)
    {
        w.WriteUInt32((uint)values.Length);
        foreach (var value in values)
            w.WriteInt32(value);
    }

    /// <summary>Rejects a counted vector over the server cap before any allocation.</summary>
    /// <param name="count">The declared count.</param>
    /// <param name="field">The field name error messages cite.</param>
    public static void RequireCount(uint count, string field)
    {
        if (count > MaxVectorCount)
            throw new PacketFormatException($"{field} count {count} exceeds {MaxVectorCount}.", 0);
    }
}
