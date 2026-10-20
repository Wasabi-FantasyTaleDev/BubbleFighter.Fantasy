using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>Recurring counted-container and optional-tag helpers used by room codecs.</summary>
internal static class WireShapes
{
    /// <summary>Client optional-present tag used by <c>sub_10D4E40</c> and C2S 402.</summary>
    internal const byte OptionalPresent = 0x3A;

    /// <summary>C2S 402 absent-optional tag written when <c>BYTE4(a9)</c> is clear.</summary>
    internal const byte OptionalAbsent402 = 0xBA;

    /// <summary>Client wstring cap: <c>N &gt; 0x4000</c> is rejected (bounded read).</summary>
    internal const int MaxWideChars = 0x4000;

    /// <summary>Hard cap on counted vectors inside <c>MapMeta</c> / room blobs.</summary>
    internal const int MaxVectorCount = 1024;

    internal static void WriteEmptyVector(PacketWriter w) => w.WriteUInt32(0);

    internal static void WriteOptionalAbsent(PacketWriter w) => w.WriteByte(0);

    internal static void WriteZeroInt32(PacketWriter w, int count)
    {
        for (var i = 0; i < count; i++)
            w.WriteInt32(0);
    }

    internal static void WriteEmptySocialSystemInfo(PacketWriter w)
    {
        WriteZeroInt32(w, 7);
        w.WriteBool32(false);
        w.WriteInt64(0);
        w.WriteBool32(false);
        w.WriteBool32(false);
    }

    internal static string ReadWide(ref PacketReader r, string field)
    {
        var n = r.ReadUInt32();
        if (n > MaxWideChars)
            throw new PacketFormatException($"{field} wstring length {n} exceeds {MaxWideChars}.", r.Offset);
        return System.Text.Encoding.Unicode.GetString(r.ReadBytes(checked((int)n * 2)));
    }

    internal static int[] ReadInt32Vector(ref PacketReader r, string field)
    {
        var count = r.ReadUInt32();
        RequireCount(count, field);
        var values = new int[count];
        for (var i = 0; i < values.Length; i++)
            values[i] = r.ReadInt32();
        return values;
    }

    internal static void WriteInt32Vector(PacketWriter w, int[] values)
    {
        w.WriteUInt32((uint)values.Length);
        foreach (var value in values)
            w.WriteInt32(value);
    }

    internal static void RequireCount(uint count, string field)
    {
        if (count > MaxVectorCount)
            throw new PacketFormatException($"{field} count {count} exceeds {MaxVectorCount}.", 0);
    }
}
