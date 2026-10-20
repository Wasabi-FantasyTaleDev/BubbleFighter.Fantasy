using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Shop.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

public sealed class ShopRequestParseTests
{
    [Fact]
    public void EnterShopPinsTwelveBytesAndRejectsTail()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt64(0x1122334455667788L);
            w.WriteInt32(13);
        });
        Assert.Equal(12, payload.Length);
        var parsed = EnterShopRequest.Parse(payload);
        Assert.Equal(0x1122334455667788L, parsed.Token);
        Assert.Equal(13, parsed.Unknown4);
        Assert.Throws<PacketFormatException>(() => EnterShopRequest.Parse([.. payload, 0]));
        Assert.Throws<PacketFormatException>(() => EnterShopRequest.Parse(payload[..8]));
    }

    [Fact]
    public void EnterTCShopPinsEightBytesAndRejectsTail()
    {
        var payload = WireLengthSupport.Written(w => w.WriteInt64(0x1122334455667788L));
        Assert.Equal(8, payload.Length);
        var parsed = EnterTCShopRequest.Parse(payload);
        Assert.Equal(0x1122334455667788L, parsed.Token);
        Assert.Throws<PacketFormatException>(() => EnterTCShopRequest.Parse([.. payload, 0]));
        Assert.Throws<PacketFormatException>(() => EnterTCShopRequest.Parse(payload[..4]));
    }

    [Fact]
    public void ShopBuyPinsEightBytesAndRejectsTail()
    {
        var payload = WireLengthSupport.Written(w =>
        {
            w.WriteInt32(42);
            w.WriteInt32(1);
        });
        Assert.Equal(8, payload.Length);
        var parsed = ShopBuyRequest.Parse(payload);
        Assert.Equal(42, parsed.ItemId);
        Assert.Equal(1, parsed.Unknown4);
        Assert.Throws<PacketFormatException>(() => ShopBuyRequest.Parse([.. payload, 0]));
        Assert.Throws<PacketFormatException>(() => ShopBuyRequest.Parse(payload[..4]));
    }

    [Fact]
    public void ShopItemCardInfoPinsFourBytesAndRejectsTail()
    {
        var payload = WireLengthSupport.Written(w => w.WriteInt32(42));
        Assert.Equal(4, payload.Length);
        var parsed = ShopItemCardInfoRequest.Parse(payload);
        Assert.Equal(42, parsed.ItemId);
        Assert.Throws<PacketFormatException>(() => ShopItemCardInfoRequest.Parse([.. payload, 0]));
        Assert.Throws<PacketFormatException>(() => ShopItemCardInfoRequest.Parse([]));
    }

    [Fact]
    public void RealCouponUsePinsPrefixedWideAndRejectsTail()
    {
        var empty = WireLengthSupport.Written(w => w.WriteWideStringPrefixed(string.Empty));
        Assert.Equal(4, empty.Length);
        Assert.Equal(string.Empty, RealCouponUseRequest.Parse(empty).Unknown0);

        var payload = WireLengthSupport.Written(w => w.WriteWideStringPrefixed("x"));
        Assert.Equal(6, payload.Length);
        Assert.Equal("x", RealCouponUseRequest.Parse(payload).Unknown0);
        Assert.Throws<PacketFormatException>(() => RealCouponUseRequest.Parse([.. payload, 0]));
    }

    [Fact]
    public void CheckCashPinsPrefixedWideAndRejectsTail()
    {
        var empty = WireLengthSupport.Written(w => w.WriteWideStringPrefixed(string.Empty));
        Assert.Equal(4, empty.Length);
        Assert.Equal(string.Empty, CheckCashRequest.Parse(empty).Unknown1);

        var payload = WireLengthSupport.Written(w => w.WriteWideStringPrefixed("y"));
        Assert.Equal(6, payload.Length);
        Assert.Equal("y", CheckCashRequest.Parse(payload).Unknown1);
        Assert.Throws<PacketFormatException>(() => CheckCashRequest.Parse([.. payload, 0]));
    }

    [Fact]
    public void EnterShop400PinsEightBytesAndRejectsTail()
    {
        var payload = WireLengthSupport.Written(w => w.WriteInt64(0x1122334455667788L));
        Assert.Equal(8, payload.Length);
        var parsed = EnterShopRequest400.Parse(payload);
        Assert.Equal(0x1122334455667788L, parsed.Token);
        Assert.Throws<PacketFormatException>(() => EnterShopRequest400.Parse([.. payload, 0]));
        Assert.Throws<PacketFormatException>(() => EnterShopRequest400.Parse(payload[..4]));
    }

    [Fact]
    public void EnterShopEmptyPinsTheEmptyBody()
    {
        Assert.NotNull(EnterShopRequestEmpty.Parse([]));
        Assert.Throws<PacketFormatException>(() => EnterShopRequestEmpty.Parse([0]));
    }
}
