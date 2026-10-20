using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Shop.Tests;

internal static class WireLengthSupport
{
    internal static byte[] Written(Action<PacketWriter> write)
    {
        var w = PacketWriter.Create();
        write(w);
        return ((ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
    }
}
