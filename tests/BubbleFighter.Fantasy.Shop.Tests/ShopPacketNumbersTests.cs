using BubbleFighter.Fantasy.Abstractions.Protocol;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Pins the packet numbers the shop's dialect binding resolves by name from the kr-1256 opcode table, so a table
/// edit that moves one is a visible change rather than a silent wire change.
/// </summary>
public sealed class ShopPacketNumbersTests
{
    [Theory]
    [InlineData("gspRepEnterShop", 800)]
    [InlineData("gspRepEnterTCShop", 801)]
    [InlineData("gspInventoryUpdateInventorySlots", 803)]
    [InlineData("gspRepShopBuy", 816)]
    [InlineData("gspRepShopItemCardInfo", 817)]
    [InlineData("gspUserMoneyStat", 106)]
    [InlineData("gspRepRealCouponUse", 833)]
    [InlineData("gspRepCheckCash", 834)]
    public void RepliesResolveByName(string name, ushort expected) =>
        Assert.Equal(expected, ShopTablePackets.Table.S2C(name));

    [Theory]
    [InlineData("gcpEnterShop", 801)]
    [InlineData("gcpEnterTCShop_", 802)]
    [InlineData("gcpEnterInventory", 803)]
    [InlineData("gcpReqShopBuy", 809)]
    [InlineData("gcpReqShopItemCardInfo", 810)]
    [InlineData("gcpReqRealCouponUse", 833)]
    [InlineData("Unknown834", 834)]
    [InlineData("Unknown2059", 2059)]
    public void RequestsResolveByName(string name, ushort expected) =>
        Assert.Equal(expected, ShopTablePackets.Table.C2S(name));

    /// <summary>
    /// The two requests the row used to read as a second buy and a second card request are the gift sent-list page
    /// (kr-1256 sub_E49DC0) and the refund request (kr-1256 sub_D3E0B0); the shop row registers neither.
    /// </summary>
    [Fact]
    public void GiftListAndRefundAreNotShopRequests()
    {
        Assert.Equal((ushort)816, ShopTablePackets.Table.C2S("gcpReqPresentSendSubList"));
        Assert.Equal((ushort)817, ShopTablePackets.Table.C2S("gcpReqRefund"));
        Assert.Null(ShopTablePackets.Table.Find(PacketDirection.ClientToServer, "gcpReqShopBuySecond"));
    }
}
