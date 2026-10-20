using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.InventoryView.Tests;

/// <summary>
/// Shared fixtures for InventoryView wire length pins. Helpers stay internal so
/// they do not become a documented public surface.
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
