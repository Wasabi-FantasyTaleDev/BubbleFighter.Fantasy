using System.Buffers;
using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins later modern settings against independent native-reader byte fixtures.</summary>
public sealed class ModernGameSettingTableTests
{
    /// <summary>The distinct headers, nested collections and tail widths decode without borrowing the Korean layout.</summary>
    [Theory]
    [InlineData("cn-774", 373, 329, 342, 354, 376, 392, 400, 488)]
    [InlineData("tw-454", 351, 313, 326, 338, 348, 368, 376, 456)]
    public void CompleteSettingPinsNestedValuesAndTail(string build, int length, int pairsOffset,
        int textsOffset, int flagsOffset, int byteOffset, int wideOffset, int stringsOffset, int finalOffset)
    {
        var body = Fixture(build).Body;
        Assert.Equal(length, body.Length);
        var fields = PacketCodec.Read(TestBuilds.Packets, build, "GameSetting", body,
            requireCanonicalBooleans: true);
        var pairs = Assert.IsType<object?[]>(fields[$"unknown_{pairsOffset}"]);
        Assert.Equal(2, pairs.Length);
        Assert.Equal(unchecked((int)0xFEDCBA98), Map(pairs[0])["unknown_0"]);
        Assert.Equal(0x11223344, Map(pairs[0])["unknown_4"]);
        Assert.Equal(7, Map(pairs[1])["unknown_0"]);
        Assert.Equal(9, Map(pairs[1])["unknown_4"]);
        var texts = Assert.IsType<object?[]>(fields[$"unknown_{textsOffset}"]);
        Assert.Equal(2, texts.Length);
        Assert.Equal(41, Map(texts[0])["unknown_0"]);
        Assert.Equal("A\u03A9", Map(texts[0])["unknown_4"]);
        Assert.Equal("", Map(texts[1])["unknown_4"]);
        Assert.Equal(new object?[] { true, false, true, false, true },
            Assert.IsType<object?[]>(fields[$"unknown_{flagsOffset}"]));
        Assert.Equal((byte)0xFE, fields[$"unknown_{byteOffset}"]);
        Assert.Equal(unchecked((long)0xFEDCBA9876543210UL), fields[$"unknown_{wideOffset}"]);
        Assert.Equal(new object?[] { "left", "\u03A9", "" },
            Assert.IsType<object?[]>(fields[$"unknown_{stringsOffset}"]));
        Assert.Equal(0x13572468, fields[$"unknown_{finalOffset}"]);
        if (build == "cn-774")
        {
            Assert.Equal((ushort)0x8123, fields["unknown_4"]);
            Assert.Equal("map\u03A9", fields["unknown_52"]);
            Assert.Equal("fifth", fields["unknown_148"]);
            var rows = Assert.IsType<object?[]>(fields["unknown_360"]);
            Assert.Equal(2, rows.Length);
            Assert.Equal((ushort)0xABCD, Map(rows[0])["unknown_0"]);
            Assert.Equal(new object?[] { 19, -1 }, Assert.IsType<object?[]>(Map(rows[0])["unknown_4"]));
            Assert.Empty(Assert.IsType<object?[]>(Map(rows[1])["unknown_4"]));
            Assert.False(fields.ContainsKey("unknown_428"));
        }
        else
        {
            Assert.Equal(unchecked((int)0x81234567), fields["unknown_0"]);
            Assert.Equal("map\u03A9", fields["unknown_36"]);
            Assert.Equal("fifth", fields["unknown_132"]);
            Assert.True(Assert.IsType<bool>(fields["unknown_352"]));
            Assert.DoesNotContain(TestBuilds.Packets.Resolve(build, "GameSetting"),
                field => field.Type == "list<GameSettingCountedRow>");
        }
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "GameSetting", fields));
    }

    /// <summary>Every truncation and extra byte is rejected, and collection or string limits apply before allocation.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void CompleteSettingRejectsMalformedAndUnboundedBodies(string build)
    {
        var (body, pairCount, textCount, rowCount, stringCount, firstString) = Fixture(build);
        for (var count = 0; count < body.Length; count++)
        {
            var truncated = body[..count];
            Assert.Throws<PacketFormatException>(() => Read(truncated));
        }
        Assert.Throws<PacketFormatException>(() => Read([.. body, 0]));
        foreach (var offset in new[] { pairCount, textCount, stringCount, rowCount }.Where(value => value >= 0))
        {
            var excessive = (byte[])body.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(excessive.AsSpan(offset), uint.MaxValue);
            Assert.Throws<PacketFormatException>(() => Read(excessive));
        }
        var longString = (byte[])body.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(longString.AsSpan(firstString), 1025);
        Assert.Throws<PacketFormatException>(() => Read(longString));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, "GameSetting", body,
            new PacketReadLimits(MaxCollectionElements: 1)));
        Assert.Throws<PacketFormatException>(() => PacketCodec.Read(TestBuilds.Packets, build, "GameSetting", body,
            new PacketReadLimits(MaxValues: 1)));

        void Read(byte[] bytes) => PacketCodec.Read(TestBuilds.Packets, build, "GameSetting", bytes);
    }

    private static Dictionary<string, object?> Map(object? value) => Assert.IsType<Dictionary<string, object?>>(value);

    private static (byte[] Body, int PairCount, int TextCount, int RowCount, int StringCount, int FirstString)
        Fixture(string build)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        int firstString;
        // cn-774 sub_492EB0 starts with a two-byte second field; tw-454 sub_48ADF0 uses three full words.
        if (build == "cn-774")
        {
            writer.WriteInt32(0x10203040);
            writer.WriteUInt16(0x8123);
            writer.WriteInt32(8);
            writer.WriteBool32(true);
            writer.WriteInt32(24);
            writer.WriteBool32(false);
            foreach (var value in new[] { 32, 36, 40, 44, 48 }) writer.WriteInt32(value);
            firstString = buffer.WrittenCount;
            foreach (var text in new[] { "map\u03A9", "second", "third", "fourth", "fifth" })
                writer.WriteWideStringPrefixed(text);
            writer.WriteInt32(172);
            foreach (var text in new[] { "r1", "a1", "r2", "a2", "r3", "a3" })
                writer.WriteWideStringPrefixed(text);
            writer.WriteInt32(320);
            writer.WriteInt32(324);
        }
        else
        {
            writer.WriteInt32(unchecked((int)0x81234567));
            writer.WriteInt32(4);
            writer.WriteInt32(8);
            writer.WriteBool32(true);
            writer.WriteBool32(false);
            writer.WriteBool32(true);
            foreach (var value in new[] { 16, 20, 24, 28, 32 }) writer.WriteInt32(value);
            firstString = buffer.WrittenCount;
            foreach (var text in new[] { "map\u03A9", "second", "third", "fourth", "fifth" })
                writer.WriteWideStringPrefixed(text);
            writer.WriteInt32(156);
            foreach (var text in new[] { "r1", "a1", "r2", "a2", "r3", "a3" })
                writer.WriteWideStringPrefixed(text);
            writer.WriteInt32(304);
            writer.WriteInt32(308);
        }
        // cn-774 sub_494560 and tw-454 sub_48C510 have identical wire element shapes.
        writer.WriteBool32(true);
        var pairCount = buffer.WrittenCount;
        writer.WriteInt32(2);
        writer.WriteInt32(unchecked((int)0xFEDCBA98));
        writer.WriteInt32(0x11223344);
        writer.WriteInt32(7);
        writer.WriteInt32(9);
        writer.WriteBool32(false);
        var textCount = buffer.WrittenCount;
        writer.WriteInt32(2);
        writer.WriteInt32(41);
        writer.WriteWideStringPrefixed("A\u03A9");
        writer.WriteInt32(42);
        writer.WriteWideStringPrefixed("");
        foreach (var flag in new[] { true, false, true, false, true }) writer.WriteBool32(flag);
        var rowCount = -1;
        if (build == "cn-774")
        {
            // cn-774 sub_494610: two rows, one containing two words and the other empty.
            rowCount = buffer.WrittenCount;
            writer.WriteInt32(2);
            writer.WriteUInt16(0xABCD);
            writer.WriteInt32(2);
            writer.WriteInt32(19);
            writer.WriteInt32(-1);
            writer.WriteUInt16(3);
            writer.WriteInt32(0);
        }
        writer.WriteBool32(true);
        writer.WriteBool32(false);
        writer.WriteByte(0xFE);
        if (build == "tw-454") writer.WriteBool32(true);
        writer.WriteInt32(0x12345678);
        writer.WriteInt64(unchecked((long)0xFEDCBA9876543210UL));
        var stringCount = buffer.WrittenCount;
        writer.WriteInt32(3);
        foreach (var text in new[] { "left", "\u03A9", "" }) writer.WriteWideStringPrefixed(text);
        foreach (var value in build == "cn-774" ? new[] { 412, 416, 420, 424, 432, 436 } : new[] { 388, 392, 396, 400, 404 })
            writer.WriteInt32(value);
        writer.WriteWideStringPrefixed("tail1");
        writer.WriteWideStringPrefixed("tail2");
        writer.WriteInt32(0x13572468);
        return (buffer.WrittenSpan.ToArray(), pairCount, textCount, rowCount, stringCount, firstString);
    }
}
