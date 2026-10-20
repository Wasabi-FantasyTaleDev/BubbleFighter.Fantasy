using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins cash-slot wire widths independently of native object stride.</summary>
public sealed class CashInventoryTableTests
{
    /// <summary>The native object-size change moves metadata without adding wire padding.</summary>
    [Theory]
    [InlineData("kr-812", "unknown_52", 52)]
    [InlineData("kr-1256", "unknown_48", 48)]
    [InlineData("cn-774", "unknown_48", 48)]
    [InlineData("tw-454", "unknown_48", 48)]
    public void SlotHasTwentyFiveFixedBytesPlusUtf16Text(string build, string tailName, int nativeOffset)
    {
        var values = SlotValues(tailName);
        var body = PacketCodec.Write(TestBuilds.Packets, build, "CashInventorySlot", values);
        Assert.Equal(Convert.FromHexString(
            "4433221188776655AA99CCBB00FFEEDD785634120200000041005CD57F"), body);
        Assert.Equal(29, body.Length);
        var fields = PacketCodec.Read(TestBuilds.Packets, build, "CashInventorySlot", body);
        Assert.Equal(8, fields.Count);
        Assert.Equal(0x11223344, fields["cash_slot_id"]);
        Assert.Equal("A\uD55C", fields["unknown_24"]);
        Assert.Equal((byte)0x7F, fields[tailName]);
        var tail = Assert.Single(TestBuilds.Packets.Resolve(build, "CashInventorySlot"),
            field => field.Name == tailName);
        Assert.Equal(nativeOffset, tail.Offset);
        Assert.Equal("u8", tail.Type);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "CashInventorySlot", fields));
    }

    /// <summary>The full update contains a word count and tightly packed variable-length records.</summary>
    [Theory]
    [InlineData("kr-812", "unknown_52")]
    [InlineData("kr-1256", "unknown_48")]
    [InlineData("cn-774", "unknown_48")]
    [InlineData("tw-454", "unknown_48")]
    public void CountedUpdatePreservesRecordsAndRejectsTruncation(string build, string tailName)
    {
        var first = SlotValues(tailName);
        var second = SlotValues(tailName);
        second["cash_slot_id"] = 9;
        second["unknown_24"] = "";
        var body = PacketCodec.Write(TestBuilds.Packets, build, "CashInventoryUpdate",
            new Dictionary<string, object?> { ["slots"] = new[] { first, second } });
        Assert.Equal(58, body.Length);
        Assert.Equal(Convert.FromHexString("02000000"), body[..4]);
        Assert.Equal(Convert.FromHexString("09000000"), body[33..37]);
        var values = PacketCodec.Read(TestBuilds.Packets, build, "CashInventoryUpdate", body);
        var records = Assert.IsType<object?[]>(values["slots"]);
        Assert.Equal(2, records.Length);
        Assert.Equal(body, PacketCodec.Write(TestBuilds.Packets, build, "CashInventoryUpdate", values));
        for (var length = 0; length < body.Length; length++)
        {
            var truncated = body[..length];
            Assert.Throws<PacketFormatException>(() =>
                PacketCodec.Read(TestBuilds.Packets, build, "CashInventoryUpdate", truncated));
        }
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(TestBuilds.Packets, build, "CashInventoryUpdate", [.. body, 0]));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(TestBuilds.Packets, build, "CashInventoryUpdate", body,
                new PacketReadLimits(MaxCollectionElements: 1)));
    }

    /// <summary>An empty list is exactly its count, and oversized counts fail before allocation.</summary>
    [Theory]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void EmptyAndOversizedCountsHaveExplicitBounds(string build)
    {
        var empty = PacketCodec.Write(TestBuilds.Packets, build, "CashInventoryUpdate",
            new Dictionary<string, object?> { ["slots"] = Array.Empty<object>() });
        Assert.Equal(new byte[4], empty);
        var decoded = PacketCodec.Read(TestBuilds.Packets, build, "CashInventoryUpdate", empty);
        Assert.Empty(Assert.IsType<object?[]>(decoded["slots"]));
        Assert.Throws<PacketFormatException>(() =>
            PacketCodec.Read(TestBuilds.Packets, build, "CashInventoryUpdate", [255, 255, 255, 255]));
    }

    private static Dictionary<string, object?> SlotValues(string tailName) => new()
    {
        ["cash_slot_id"] = 0x11223344,
        ["unknown_8"] = 0x55667788,
        ["unknown_12"] = 0x99AA,
        ["unknown_14"] = 0xBBCC,
        ["unknown_16"] = 0xDDEEFF00u,
        ["item_key"] = 0x12345678,
        ["unknown_24"] = "A\uD55C",
        [tailName] = 0x7F,
    };
}
