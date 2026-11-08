using System.Buffers.Binary;
using System.IO.Compression;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>
/// The gold-standard pin for the native DynamicConfig generation: the per-slot content rows in
/// the build's dynamic_config block are marshaled through their packet schemas and must reproduce
/// a private reference generation byte for byte at the inflated layer. The reference is maintainer
/// evidence outside the repository, so that test runs only where it is present (LocalClientData).
/// Its compressed streams come from a different zlib build, so the compressed layer is verified by
/// round trip instead.
/// </summary>
public sealed class DynamicConfigGenerationTests
{
    private const string Build = "kr-1256";

    /// <summary>The registry bits the profile serves: every fully decoded slot, nothing else.</summary>
    private static readonly int[] ServedBits = [1, 2, 3, 4, 5, 6, 8, 9, 12, 13, 14, 16];

    /// <summary>
    /// Parses the private reference generation: a mask, then one compressed blob per set bit
    /// ascending. Each blob's inflated bytes are the reference the marshaller must reproduce.
    /// </summary>
    private static SortedDictionary<int, byte[]> ReferenceSlots()
    {
        var data = File.ReadAllBytes(FixturePath());
        var mask = BinaryPrimitives.ReadInt32LittleEndian(data);
        var slots = new SortedDictionary<int, byte[]>();
        var offset = 4;
        for (var bit = 0; bit < 32; bit++)
        {
            if ((mask >> bit & 1) == 0) continue;
            var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            using var input = new MemoryStream(data, offset + 4, length, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            slots[bit] = output.ToArray();
            offset += 4 + length;
        }
        Assert.Equal(data.Length, offset);
        return slots;
    }

    private static string FixturePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "private", "captures", "kr-1256", "dynamic-config-reference.bin");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("The private DynamicConfig reference generation was not found.");
    }

    /// <summary>Frames the generation the wire would answer a fully stale request with.</summary>
    private static byte[] FullReply(DynamicConfigWire wire)
    {
        var mask = 0;
        foreach (var bit in ServedBits) mask |= 1 << bit;
        var body = new byte[4 + 4 * ServedBits.Length];
        BinaryPrimitives.WriteInt32LittleEndian(body, mask);
        var frame = wire.Reply(body);
        Assert.NotNull(frame);
        return frame!;
    }

    /// <summary>
    /// Every served slot's marshaled body must equal the reference bytes exactly, and the
    /// unserved bits (7, 10, 11, 15) must stay out of the mask.
    /// </summary>
    [Fact]
    [Trait("Category", "LocalClientData")]
    public void MarshaledSlotsReproduceTheReferenceBodiesByteForByte()
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, Build);
        Assert.Equal(ServedBits, wire.ServedBits.OrderBy(bit => bit).ToArray());
        var reference = ReferenceSlots();
        var frame = FullReply(wire);
        var mask = BinaryPrimitives.ReadInt32LittleEndian(frame);
        var expectedMask = 0;
        foreach (var bit in ServedBits) expectedMask |= 1 << bit;
        Assert.Equal(expectedMask, mask);
        var offset = 4;
        for (var bit = 0; bit < 32; bit++)
        {
            if ((mask >> bit & 1) == 0) continue;
            var length = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(offset));
            var blob = frame[(offset + 4)..(offset + 4 + length)];
            using var input = new MemoryStream(blob, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            var plain = output.ToArray();
            Assert.True(reference.ContainsKey(bit), $"Bit {bit} is framed but the reference generation has no such slot.");
            Assert.Equal(reference[bit].Length, plain.Length);
            Assert.Equal(reference[bit], plain);
            offset += 4 + length;
        }
        Assert.Equal(frame.Length, offset);
    }

    /// <summary>
    /// The stamps the client reports back are the seed-zero Adler-32 over the compressed bytes it
    /// holds, so a cached request must be answered with no frame at all.
    /// </summary>
    [Fact]
    public void CachedStampsAnswerWithNoFrame()
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, Build);
        var body = new byte[4 + 4 * ServedBits.Length];
        var mask = 0;
        var offset = 4;
        foreach (var bit in ServedBits)
        {
            mask |= 1 << bit;
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset), unchecked((int)wire.StampOf(bit)));
            offset += 4;
        }
        BinaryPrimitives.WriteInt32LittleEndian(body, mask);
        Assert.Null(wire.Reply(body));
    }

    /// <summary>A stale stamp must re-frame exactly the stale categories and nothing else.</summary>
    [Fact]
    public void StaleStampsReframeOnlyTheStaleCategories()
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, Build);
        var body = new byte[4 + 4 * ServedBits.Length];
        var mask = 0;
        var offset = 4;
        foreach (var bit in ServedBits)
        {
            mask |= 1 << bit;
            var stamp = wire.StampOf(bit);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset), unchecked((int)(bit == 3 ? stamp ^ 1 : stamp)));
            offset += 4;
        }
        BinaryPrimitives.WriteInt32LittleEndian(body, mask);
        var frame = wire.Reply(body);
        Assert.NotNull(frame);
        Assert.Equal(1 << 3, BinaryPrimitives.ReadInt32LittleEndian(frame));
    }

    /// <summary>An independent seed-zero Adler-32 must agree with the wire's stamp for one slot.</summary>
    [Fact]
    public void StampIsTheSeedZeroAdlerOverTheCompressedBytes()
    {
        var wire = new DynamicConfigWire(TestBuilds.Packets, Build);
        var frame = FullReply(wire);
        var offset = 4;
        var length = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(offset));
        var blob = frame[(offset + 4)..(offset + 4 + length)];
        uint low = 0, high = 0;
        foreach (var value in blob)
        {
            low = (low + value) % 65521;
            high = (high + low) % 65521;
        }
        Assert.Equal((high << 16) | low, wire.StampOf(1));
    }
}
