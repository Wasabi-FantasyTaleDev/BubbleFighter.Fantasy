using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Equipment.Tests;

/// <summary>
/// Shared fixtures for Equipment wire length pins. Helpers stay internal so they do not
/// become a documented public surface.
/// </summary>
internal static class WireLengthSupport
{
    internal static byte[] Written(Action<PacketWriter> write)
    {
        var w = PacketWriter.Create();
        write(w);
        return ((ArrayBufferWriter<byte>)w.Buffer).WrittenSpan.ToArray();
    }
}
