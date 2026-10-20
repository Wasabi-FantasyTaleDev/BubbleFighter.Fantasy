using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InGame.Wire;

/// <summary>Helpers shared by in-session codecs. Not a public protocol surface.</summary>
internal static class WireUtil
{
    internal static byte[] Written(PacketWriter w) => ((ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();

    internal static void RequireConsumed(PacketReader r, string name)
    {
        if (r.Remaining != 0)
            throw new PacketFormatException($"{name} has {r.Remaining} trailing byte(s).", r.Offset);
    }
}
