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
    /// Byte ranges that are server policy rather than reference content: the four plaza booth prices of slot 3
    /// (basketball_cost, fishing_cost, fishing_second_cost, fortune_cost), which the server charges as served, and
    /// the Mysterystore's open window of slot 4 (start, end), which the server keeps open instead of the reference's
    /// past dates.
    /// </summary>
    private static readonly (int Bit, int Offset, int Length)[] PolicyRanges = [(3, 4, 2), (3, 16, 4), (3, 40, 2), (4, 0, 16)];

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
    /// Every served slot's marshaled body must equal the reference bytes exactly outside the policy ranges, and
    /// the unserved bits (7, 10, 11, 15) must stay out of the mask.
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
            var expected = reference[bit].ToArray();
            foreach (var range in PolicyRanges.Where(range => range.Bit == bit))
                plain.AsSpan(range.Offset, range.Length).CopyTo(expected.AsSpan(range.Offset));
            Assert.Equal(expected, plain);
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

    /// <summary>
    /// Content dates written as text load as the OLE automation day numbers the f64 fields carry, the values the
    /// Mysterystore window and the roulette dates were served as before.
    /// </summary>
    [Fact]
    public void TextDatesLoadAsOleAutomationDays()
    {
        var store = DynamicConfigContent.PublishedSlot(TestBuilds.Packets, Build, 4, "MysteryStoreContext");
        Assert.NotNull(store);
        Assert.Equal(43831.0, store["start"]);
        Assert.Equal(73050.0, store["end"]);
        var roulette = DynamicConfigContent.PublishedSlot(TestBuilds.Packets, Build, 9, "TwirlingRouletteContext");
        Assert.NotNull(roulette);
        Assert.Equal(42361.0, roulette["start"]);
        Assert.Equal(58317.0, roulette["end"]);
    }
}
