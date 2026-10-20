using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Shop.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

public sealed class ShopWireByteLengthTests
{
    [Fact]
    public void RepEnterShopEmptyPinsFortyBytesAndDocumentedHex()
    {
        var payload = ShopWire.RepEnterShopEmpty(0);
        Assert.Equal(ShopWire.RepEnterShopEmptyBytes, payload.Length);
        Assert.Equal(40, payload.Length);
        // result, has-data, i64 token, an empty card vector, the i64 that sits
        // between the two vectors (sub_99B9A0), an empty category vector, the
        // counted i32 vector the parser builds at a1+56 (sub_99B9A0 through
        // sub_999C00), and the selected main category at a1+68 (sub_99B9A0).
        Assert.Equal(
            "01000000" + "01000000" + "0000000000000000" + "00000000" + "0000000000000000"
            + "00000000" + "00000000" + "00000000",
            Convert.ToHexString(payload));

        var withToken = ShopWire.RepEnterShopEmpty(0x0102030405060708L);
        Assert.Equal(40, withToken.Length);
        Assert.Equal("0807060504030201", Convert.ToHexString(withToken.AsSpan(8, 8)));
    }

    [Fact]
    public void RepEnterTCShopEmptyPinsTwentyBytes()
    {
        var payload = ShopWire.RepEnterTCShopEmpty(0);
        Assert.Equal(ShopWire.RepEnterTCShopEmptyBytes, payload.Length);
        Assert.Equal(20, payload.Length);
        Assert.Equal("00000000" + "01000000" + "0000000000000000" + "00000000", Convert.ToHexString(payload));
    }

    [Fact]
    public void RepShopBuyRefusedPinsTwentyFiveBytes()
    {
        var payload = ShopWire.RepShopBuyRefused(42);
        Assert.Equal(ShopWire.RepShopBuyRefusedBytes, payload.Length);
        Assert.Equal(25, payload.Length);
        Assert.Equal("2A000000" + "00000000" + "00000000" + "00" + "00000000" + "00000000" + "00000000", Convert.ToHexString(payload));
    }

    [Fact]
    public void RepShopBuySuccessEchoesTheStockKeyAndPinsReferenceTail()
    {
        // 0x9B6A19 passes the request key to dialog+1660.
        var payload = ShopWire.RepShopBuy(1000731, 1, string.Empty);
        Assert.Equal(25, payload.Length);
        Assert.Equal("1B450F00010000000000000000000000000000000000000000", Convert.ToHexString(payload));
        var r = new PacketReader(payload);

        Assert.Equal(1000731, r.ReadInt32());  // +0 purchase key
        Assert.Equal(1, r.ReadInt32());        // +4 result
        Assert.Equal(string.Empty, r.ReadWideStringPrefixed());
        Assert.Equal((byte)0, r.ReadByte());   // +32: normal-buy dialog variant
        Assert.Equal(0, r.ReadInt32());        // +36
        Assert.Equal(0, r.ReadInt32());        // +40: dialog+1672, not the purchase key
        Assert.Equal(0, r.ReadInt32());        // +44: written, never read
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void RepShopItemCardInfoRefusedPinsSeventyEightBytes()
    {
        var payload = ShopWire.RepShopItemCardInfoRefused();
        Assert.Equal(ShopWire.RepShopItemCardInfoRefusedBytes, payload.Length);
        Assert.Equal(78, payload.Length);
        Assert.Equal(-1, new PacketReader(payload).ReadInt32());
        Assert.Equal(ShopWire.EmptyItemCardBytes, payload.Length - 4);
    }

    [Fact]
    public void EmptyItemCardPinsSeventyFourBytesAndStockDefaultU16()
    {
        var payload = WireLengthSupport.Written(ShopWire.WriteEmptyItemCard);
        Assert.Equal(ShopWire.EmptyItemCardBytes, payload.Length);
        Assert.Equal(74, payload.Length);

        var r = new PacketReader(payload);
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(string.Empty, r.ReadWideStringPrefixed());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.False(r.ReadBool32());
        Assert.Equal(0, r.ReadInt64());
        Assert.Equal(0, r.ReadInt64());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.False(r.ReadBool32());
        Assert.Equal((ushort)1, r.ReadUInt16());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0, r.ReadInt32());
        Assert.Equal(0u, r.ReadUInt32());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void RepRealCouponUseEmptyPinsTwelveZeroBytes()
    {
        var payload = ShopWire.RepRealCouponUseEmpty();
        Assert.Equal(ShopWire.RepRealCouponUseEmptyBytes, payload.Length);
        Assert.Equal(12, payload.Length);
        Assert.Equal(new byte[12], payload);
    }

    [Fact]
    public void RepCheckCashEmptyPinsTwelveZeroBytes()
    {
        var payload = ShopWire.RepCheckCashEmpty();
        Assert.Equal(ShopWire.RepCheckCashEmptyBytes, payload.Length);
        Assert.Equal(12, payload.Length);
        Assert.Equal(new byte[12], payload);
    }

    [Fact]
    public void RepEnterShopAndEnterTCShopPinTheKr1256FormLengths()
    {
        // Shared fixture: two cards (108 + 106 wire bytes: head, inline stock, one
        // extra term) and one category with one subcategory (48 bytes).
        var enter = ShopWire.RepEnterShop(0, ShopWireTestSupport.Cards, ShopWireTestSupport.Categories, 500);
        // i32 + bool32 + i64 + cards + i64 + categories + counted-vector + main.
        Assert.Equal(298, enter.Length);

        var tc = ShopWire.RepEnterTCShop(7, ShopWireTestSupport.Cards);
        // i32 + bool32 + i64 + cards.
        Assert.Equal(234, tc.Length);
        Assert.Throws<InvalidOperationException>(() => ShopWire.RepEnterTCShop(0, []));
    }

    [Fact]
    public void RepShopItemCardInfoSuccessPinsResultPlusCardLength()
    {
        var payload = ShopWire.RepShopItemCardInfo(1, ShopWireTestSupport.Cards[0]);
        // i32 result then the 108-byte card (52-byte head, inline stock, one extra term).
        Assert.Equal(4 + 108, payload.Length);
    }
}
