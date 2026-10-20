using System.Buffers;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Protocol.Tests;

/// <summary>
/// Pins the <c>gcpFirstConnect</c> shape the patch handler accepts on the legacy builds:
/// <c>u32, 7 wstr, u32, 15 wstr</c> (kr-812 <c>sub_845700</c>, id-400 <c>sub_797350</c>).
/// </summary>
public sealed class FirstConnectShapeTests
{
    private static byte[] Body(int version, params string[] strings)
    {
        Assert.Equal(22, strings.Length);
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new PacketWriter(buffer);
        writer.WriteInt32(version);
        for (var i = 0; i < 7; i++)
            writer.WriteWideStringPrefixed(strings[i]);
        writer.WriteInt32(0);
        for (var i = 7; i < 22; i++)
            writer.WriteWideStringPrefixed(strings[i]);
        return buffer.WrittenSpan.ToArray();
    }

    [Fact]
    public void EmptyStringsAreTheNinetySixByteForm()
    {
        var body = Body(812, Enumerable.Repeat("", 22).ToArray());
        Assert.Equal(96, body.Length);
        Assert.True(ProtocolPlugin.IsFirstConnect(body));
    }

    [Fact]
    public void FilledStringsAreAccepted()
    {
        var body = Body(400, Enumerable.Range(0, 22).Select(i => new string('x', i * 7)).ToArray());
        Assert.True(body.Length > 96);
        Assert.True(ProtocolPlugin.IsFirstConnect(body));
    }

    [Fact]
    public void Cn279FirstConnectUsesTheSameStringShapeWithoutMachineIdentifiers()
    {
        // cn-279 0x44E390: 1,310 bytes, including 607 UTF-16 units.
        var strings = Enumerable.Repeat("", 22).ToArray();
        strings[0] = new string('x', 607);
        var body = Body(279, strings);
        Assert.Equal(1310, body.Length);
        Assert.True(ProtocolPlugin.IsFirstConnect(body));
    }

    [Fact]
    public void Id1809FirstConnectKeepsTheId400FieldList()
    {
        // sub_7BD040 writes u32, 7 wstr, u32, 15 wstr, the field list of id-400 sub_797350.
        var body = Body(1809, Enumerable.Repeat("", 22).ToArray());
        Assert.Equal(96, body.Length);
        Assert.Equal(1809, BitConverter.ToInt32(body, 0));
        Assert.True(ProtocolPlugin.IsFirstConnect(body));
    }

    [Fact]
    public void Cn706FirstConnectKeepsTheId400FieldList()
    {
        // sub_79F6B0 writes u32, 7 wstr, u32, 15 wstr, the field list of id-400 sub_797350;
        // the builder sub_7A4FB0 stores 706 in the first u32.
        var body = Body(706, Enumerable.Repeat("", 22).ToArray());
        Assert.Equal(96, body.Length);
        Assert.Equal(706, BitConverter.ToInt32(body, 0));
        Assert.True(ProtocolPlugin.IsFirstConnect(body));
    }

    [Fact]
    public void TrailingOrMissingBytesAreRejected()
    {
        var body = Body(400, Enumerable.Repeat("ab", 22).ToArray());
        Assert.False(ProtocolPlugin.IsFirstConnect([.. body, 0]));
        Assert.False(ProtocolPlugin.IsFirstConnect(body.AsSpan(0, body.Length - 1)));
        Assert.False(ProtocolPlugin.IsFirstConnect(new byte[49]));
    }
}
