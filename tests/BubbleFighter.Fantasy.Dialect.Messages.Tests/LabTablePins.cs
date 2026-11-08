using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins laboratory operation bodies and independently audited state data; state pins do not enable a UI route.</summary>
public sealed class LabTablePins
{
    private static readonly PacketTables Tables = TestBuilds.Packets;

    /// <summary>The three operation writers encode two u64 keys and one u64 argument in 28 bytes.</summary>
    [Theory]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ScienceLabRequestPinsKeysAndArgument(string build)
    {
        var body = PacketCodec.Write(Tables, build, "ScienceLabRequest", new Dictionary<string, object?>
        { ["keys"] = new object[] { 256L, -1L }, ["argument"] = 5L });
        Assert.Equal(28, body.Length);
        Assert.Equal("020000000001000000000000FFFFFFFFFFFFFFFF0500000000000000", Convert.ToHexString(body));
        var read = PacketCodec.Read(Tables, build, "ScienceLabRequest", body);
        Assert.Equal(new object?[] { 256L, -1L }, (object?[])read["keys"]!);
        Assert.Equal(5L, read["argument"]);
    }

    /// <summary>The closed-lab response is 24 bytes with three explicitly empty vectors.</summary>
    [Theory]
    [InlineData("kr-1256", 5)]
    [InlineData("cn-774", 4)]
    [InlineData("tw-454", 4)]
    public void ScienceLabReplyPinsResultAndEmptyVectors(string build, int result)
    {
        var body = PacketCodec.Write(Tables, build, "ScienceLabReply", new Dictionary<string, object?>
        {
            ["result"] = result, ["unknown_4"] = Array.Empty<object?>(), ["unknown_16"] = Array.Empty<object?>(),
            ["unknown_28"] = Array.Empty<object?>(), ["unknown_40"] = 0L,
        });
        Assert.Equal(24, body.Length);
        Assert.Equal($"{result:X2}0000000000000000000000000000000000000000000000", Convert.ToHexString(body));
    }

    /// <summary>The CN state reader's four-byte scalar adds two bytes to the independently audited KR body.</summary>
    [Theory]
    [InlineData("kr-1256", 34, "4433")]
    [InlineData("cn-774", 36, "44330000")]
    public void LaboratoryStatePinsWidthDifference(string build, int length, string scalar)
    {
        var body = PacketCodec.Write(Tables, build, "LaboratoryState", new Dictionary<string, object?>
        { ["available"] = true, ["unknown_168"] = 0x11223344, ["unknown_172"] = 0x3344 });
        Assert.Equal(length, body.Length);
        Assert.Equal("000000000100000000000000000000000000000044332211" + scalar + "0000000000000000", Convert.ToHexString(body));
        var read = PacketCodec.Read(Tables, build, "LaboratoryState", body);
        Assert.Equal(0x11223344, Convert.ToInt32(read["unknown_168"]));
        Assert.Equal(0x3344, Convert.ToInt32(read["unknown_172"]));
    }

    /// <summary>The KR item-list reader consumes 26 bytes for an empty list and five empty captions.</summary>
    [Fact]
    public void LaboratoryItemsPinsEmptyBody()
    {
        var body = PacketCodec.Write(Tables, "kr-1256", "LaboratoryItems", new Dictionary<string, object?>());
        Assert.Equal(26, body.Length);
        Assert.All(body, value => Assert.Equal(0, value));
    }
}
