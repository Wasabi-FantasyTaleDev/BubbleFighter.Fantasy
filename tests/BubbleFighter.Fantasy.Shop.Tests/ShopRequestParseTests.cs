using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Pins the request reads of the shop dialect binding: the same bytes the client builders write, read through
/// the packet tables, with every trailing or missing byte refused.
/// </summary>
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
        var parsed = ShopCatalogBinding.ReadEnterShop(ShopWire.Tables, "kr-1256", payload);
        Assert.Equal(0x1122334455667788L, parsed.Token);
        Assert.Equal(13, parsed.MainCategory);
        Assert.Throws<PacketFormatException>(() => ShopCatalogBinding.ReadEnterShop(ShopWire.Tables, "kr-1256", [.. payload, 0]));
        Assert.Throws<PacketFormatException>(() => ShopCatalogBinding.ReadEnterShop(ShopWire.Tables, "kr-1256", payload[..8]));
    }

    [Fact]
    public void EnterShopTokenOnlyBuildsNormalizeTheMainCategoryToZero()
    {
        var payload = WireLengthSupport.Written(w => w.WriteInt64(0x1122334455667788L));
        Assert.Equal(8, payload.Length);
        // id-400's writer sub_63B750 sends the token alone; the row resolves its table fields.
        var parsed = ShopCatalogBinding.ReadEnterShop(ShopWire.Tables, "id-400", payload);
        Assert.Equal(0x1122334455667788L, parsed.Token);
        Assert.Equal(0, parsed.MainCategory);
        Assert.Throws<PacketFormatException>(() => ShopCatalogBinding.ReadEnterShop(ShopWire.Tables, "id-400", [.. payload, 0]));
    }

    [Fact]
    public void EnterShopCn406PinsTheTokenOnlyBodyAndRejectsTail()
    {
        // cn-406's writer cn-406 sub_5C3830 sends the u64 token alone (id-400's form); the token-only parse is
        // pinned for the build itself, not only for its base.
        var payload = WireLengthSupport.Written(w => w.WriteInt64(0x1122334455667788L));
        Assert.Equal(8, payload.Length);
        var parsed = ShopCatalogBinding.ReadEnterShop(ShopWire.Tables, "cn-406", payload);
        Assert.Equal(0x1122334455667788L, parsed.Token);
        Assert.Equal(0, parsed.MainCategory);
        Assert.Throws<PacketFormatException>(() => ShopCatalogBinding.ReadEnterShop(ShopWire.Tables, "cn-406", [.. payload, 0]));
    }

    [Fact]
    public void Cn406CashCheckPinsTheEmptyBody()
    {
        // cn-406 sub_83F450 stamps C2S 825 on an empty packet (serializer nullsub_1 cn-406 sub_5129E0);
        // any body byte fails loud instead of being parsed.
        _ = ShopBinding.ReadBalanceRequest([]);
        Assert.Throws<PacketFormatException>(() => ShopBinding.ReadBalanceRequest([0]));
    }

    [Fact]
    public void Cn406ShopBuyReplyPinsEightBytes()
    {
        // ShopPurchaseReply is the kr-812 baseline row; cn-406's cn-406 sub_72ACE0 -> cn-406 sub_420FC0 reads it
        // unchanged: one u32 shop key, one u32 result, result 1 is success.
        var payload = ShopBinding.WriteReply(ShopWire.Tables, "cn-406", new ShopPurchaseReplied(0x0F45EE, true));
        Assert.Equal(8, payload.Length);
        Assert.Equal("EE450F00" + "01000000", Convert.ToHexString(payload));
    }

    [Fact]
    public void EnterShopEmptyBodyBuildsNormalizeTokenAndMainToZero()
    {
        // cn-324's writer is a nullsub, so the body is empty; cn-279 inherits that shape.
        var parsed = ShopCatalogBinding.ReadEnterShop(ShopWire.Tables, "cn-324", []);
        Assert.Equal(0, parsed.Token);
        Assert.Equal(0, parsed.MainCategory);
        Assert.Throws<PacketFormatException>(() => ShopCatalogBinding.ReadEnterShop(ShopWire.Tables, "cn-324", [0]));
    }

    [Fact]
    public void EnterTCShopPinsEightBytesAndRejectsTail()
    {
        var payload = WireLengthSupport.Written(w => w.WriteInt64(0x1122334455667788L));
        Assert.Equal(8, payload.Length);
        var parsed = ShopCatalogBinding.ReadCashShopEntry(ShopWire.Tables, "kr-1256", payload);
        Assert.Equal(0x1122334455667788L, parsed.Token);
        Assert.Throws<PacketFormatException>(() => ShopCatalogBinding.ReadCashShopEntry(ShopWire.Tables, "kr-1256", [.. payload, 0]));
        Assert.Throws<PacketFormatException>(() => ShopCatalogBinding.ReadCashShopEntry(ShopWire.Tables, "kr-1256", payload[..4]));
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
        var parsed = ShopCatalogBinding.ReadModernPurchase(ShopWire.Tables, "kr-1256", payload);
        Assert.Equal(42, parsed.Key);
        Assert.False(parsed.IsOrdinaryPurchase);
        Assert.Throws<PacketFormatException>(() => ShopCatalogBinding.ReadModernPurchase(ShopWire.Tables, "kr-1256", [.. payload, 0]));
        Assert.Throws<PacketFormatException>(() => ShopCatalogBinding.ReadModernPurchase(ShopWire.Tables, "kr-1256", payload[..4]));
    }

    [Fact]
    public void ShopItemCardInfoPinsFourBytesAndRejectsTail()
    {
        var payload = WireLengthSupport.Written(w => w.WriteInt32(42));
        Assert.Equal(4, payload.Length);
        var parsed = ShopCatalogBinding.ReadCardInfo(ShopWire.Tables, "kr-1256", payload);
        Assert.Equal(42, parsed.Key);
        Assert.Throws<PacketFormatException>(() => ShopCatalogBinding.ReadCardInfo(ShopWire.Tables, "kr-1256", [.. payload, 0]));
        Assert.Throws<PacketFormatException>(() => ShopCatalogBinding.ReadCardInfo(ShopWire.Tables, "kr-1256", []));
    }

    [Fact]
    public void RealCouponUsePinsOnePrefixedWideStringAndRejectsTail()
    {
        var empty = WireLengthSupport.Written(w => w.WriteWideStringPrefixed(string.Empty));
        Assert.Equal(string.Empty, ShopCatalogBinding.ReadCouponUse(ShopWire.Tables, "kr-1256", empty).Code);
        var payload = WireLengthSupport.Written(w => w.WriteWideStringPrefixed("x"));
        Assert.Equal("x", ShopCatalogBinding.ReadCouponUse(ShopWire.Tables, "kr-1256", payload).Code);
        Assert.Throws<PacketFormatException>(() => ShopCatalogBinding.ReadCouponUse(ShopWire.Tables, "kr-1256", [.. payload, 0]));
    }

    [Fact]
    public void CheckCashPinsOnePrefixedWideStringAndRejectsTail()
    {
        var empty = WireLengthSupport.Written(w => w.WriteWideStringPrefixed(string.Empty));
        Assert.Equal(string.Empty, ShopCatalogBinding.ReadCashCheck(ShopWire.Tables, "kr-1256", empty).Code);
        var payload = WireLengthSupport.Written(w => w.WriteWideStringPrefixed("y"));
        Assert.Equal("y", ShopCatalogBinding.ReadCashCheck(ShopWire.Tables, "kr-1256", payload).Code);
        Assert.Throws<PacketFormatException>(() => ShopCatalogBinding.ReadCashCheck(ShopWire.Tables, "kr-1256", [.. payload, 0]));
    }
}
