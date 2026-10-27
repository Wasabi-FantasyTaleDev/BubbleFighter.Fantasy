using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Pins the C2S 2059 scene-bootstrap notice the shop entry emits. The builder
/// <c>sub_92F6C0</c> latches at <c>dword_18E566C+984</c> and sends once, and no
/// reply path exists, so the dialect row only has to accept the body.
/// </summary>
public sealed class SceneBootstrapTests
{
    [Fact]
    public void SceneBootstrapPinsTheFiveByteShopEntryForm()
    {
        // sub_9328B0 calls sub_92F6C0(0xD, 0), so the live body is 0D 00 00 00 00.
        var body = new byte[] { 0x0D, 0x00, 0x00, 0x00, 0x00 };
        var values = PacketCodec.ReadScalars(ShopWire.Tables, "kr-1256", "Unknown2059", body);

        Assert.Equal((byte)0x0D, values["unknown_0"]);
        Assert.Equal(0, values["unknown_4"]);
        Assert.Equal(5, body.Length);
    }

    [Fact]
    public void SceneBootstrapRejectsATrailingByte()
    {
        var body = new byte[] { 0x0D, 0x00, 0x00, 0x00, 0x00, 0x01 };
        Assert.Throws<PacketFormatException>(
            () => PacketCodec.ReadScalars(ShopWire.Tables, "kr-1256", "Unknown2059", body));
    }

    [Fact]
    public void SceneBootstrapRejectsAShortBody()
    {
        var body = new byte[] { 0x0D, 0x00, 0x00 };
        Assert.Throws<PacketFormatException>(
            () => PacketCodec.ReadScalars(ShopWire.Tables, "kr-1256", "Unknown2059", body));
    }
}
