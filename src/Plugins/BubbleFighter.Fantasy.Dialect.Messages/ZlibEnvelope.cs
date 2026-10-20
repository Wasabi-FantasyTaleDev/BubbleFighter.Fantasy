using System.Buffers.Binary;
using System.IO.Compression;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Wraps a bag body the way the modern family's inventory readers expect it: one <c>u32</c> holding
/// the compressed length, then one zlib stream. kr-1256 sub_FA5520 reads the length first and feeds
/// exactly that many bytes to its zlib stream; the later builds read the plain body instead.
/// </summary>
internal static class ZlibEnvelope
{
    /// <summary>Compresses one plain body and prepends its compressed length.</summary>
    /// <param name="plain">The body the build's structure wrote.</param>
    /// <returns>The enveloped body.</returns>
    internal static byte[] Wrap(byte[] plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        using var target = new MemoryStream();
        using (var zlib = new ZLibStream(target, CompressionLevel.Fastest))
            zlib.Write(plain);
        var compressed = target.ToArray();
        var body = new byte[4 + compressed.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, checked((uint)compressed.Length));
        compressed.CopyTo(body, 4);
        return body;
    }
}
