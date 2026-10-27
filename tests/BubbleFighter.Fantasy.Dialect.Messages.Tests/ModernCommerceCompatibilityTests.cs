using System.Buffers.Binary;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins short-frame modern commerce against its native requests and compact purchase consumers.</summary>
public sealed class ModernCommerceCompatibilityTests
{
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public async Task NativeShopRequestsReachSharedContractsAndUnregister(string build)
    {
        var table = TestBuilds.Opcodes(build);
        using var fixture = new MatchMessagesMountTests.Fixture(table);
        var context = fixture.Context(build, build + "/game");
        var session = new MatchMessagesMountTests.Session(table.Profile);
        var binding = new ShopCatalogBinding(build + "/game", table, TestBuilds.Packets, build, NullLogger.Instance);
        binding.MountModern(context, fixture.Messages);
        try
        {
            await Request("gcpEnterShop", "0807060504030201E8030000");
            Assert.Equal(new ShopCatalogRequested(0x0102030405060708L, 1000), fixture.Messages.Received[0]);
            await Request("gcpReqShopBuy", "0403020100000000");
            Assert.Equal(new ShopPurchaseRequested(0x01020304, true), fixture.Messages.Received[1]);
            await Request("gcpReqShopItemCardInfo", "04030201");
            Assert.Equal(new ShopCardInfoRequested(0x01020304), fixture.Messages.Received[2]);
            await Request("gcpReqRealCouponUse", "0200000041004200");
            Assert.Equal(new ShopCouponUseRequested("AB"), fixture.Messages.Received[3]);
            await Request("gcpEnterInventory", "");
            Assert.IsType<ShopInventoryRequested>(fixture.Messages.Received[4]);
            await Assert.ThrowsAsync<PacketFormatException>(() => Request("gcpEnterShop", "00000000"));
            Assert.Equal(5, fixture.Messages.Received.Count);
            Assert.Single(fixture.Services.GetAll<IMessageSender<ShopBalanceUpdated>>());
            await binding.SendAsync(session, new ShopPurchaseReplied(0x01020304, true), default);
            Assert.Equal(table.S2C("gspRepShopBuy"), Assert.Single(session.Sent));
            // cn-774 sub_BB4C80 / tw-454 sub_A92370: key, result, empty string, byte, one word.
            Assert.Equal("0403020101000000000000000000000000", Convert.ToHexString(Assert.Single(session.Bodies)));
        }
        finally { await context.UnwindAsync(); }
        Assert.Empty(fixture.Packets.Handlers);
        Assert.Empty(fixture.Services.GetAll<IMessageSender<ShopCatalogPublished>>());

        Task Request(string name, string hex) => fixture.Packets.DispatchAsync(build + "/game", session,
            new InboundPacket(table.C2S(name), Convert.FromHexString(hex)), default).AsTask();
    }

    [Theory]
    [InlineData("kr-1256", 25)]
    [InlineData("cn-774", 17)]
    [InlineData("tw-454", 17)]
    public void PurchaseReplyKeepsOnlyTheNativeTrailingWords(string build, int length)
    {
        var body = ShopCatalogBinding.WriteModernReply(TestBuilds.Packets, build, new(0x01020304, false) { ReplyText = "AB" });
        Assert.Equal(length + 4, body.Length);
        Assert.Equal(0x01020304, BinaryPrimitives.ReadInt32LittleEndian(body));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal("0200000041004200", Convert.ToHexString(body.AsSpan(8, 8)));
        Assert.All(body.Skip(16), value => Assert.Equal((byte)0, value));
    }

    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void CatalogBudgetUsesActualRecordBytesAndKeepsTheTreeAndRevision(string build)
    {
        var cards = new[] { Card("A"), Card("Longer") };
        ShopCategoryRow[] categories = [new(1000, "All", 0, [new(1001, "Items", 0, 1, 0)])];
        var one = ShopCatalogBinding.WriteEnterShopReply(TestBuilds.Packets, build, 0, 1000, [cards[0]], categories, true);
        var (body, sent) = ShopCatalogBinding.WriteBudgetedCatalog(TestBuilds.Packets, build, 0, 1000,
            cards, categories, true, one.Length);
        Assert.Equal(1, sent);
        Assert.Equal(one, body);
        Assert.NotEqual(0L, BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(8)));
        var full = ShopCatalogBinding.WriteEnterShopReply(TestBuilds.Packets, build, 0, 1000, cards, categories, true);
        Assert.Equal(full, ShopCatalogBinding.WriteBudgetedCatalog(TestBuilds.Packets, build, 0, 1000,
            cards, categories, true, full.Length).Body);
        Assert.Equal(full, ShopCatalogBinding.WriteBudgetedCatalog(TestBuilds.Packets, build, 0, 1000,
            cards, categories, true, 0).Body);
        Assert.Throws<InvalidOperationException>(() => ShopCatalogBinding.WriteBudgetedCatalog(TestBuilds.Packets,
            build, 0, 1000, cards, categories, false, 1));
        Assert.Equal(0, ShopCatalogBinding.WriteBudgetedCatalog(TestBuilds.Packets, build, 0, 1000,
            [Card(new string('X', 32740))], [], false, ushort.MaxValue).Sent);
    }

    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void EquipmentKeepsTheEightByteNativeKeyAndTwelveByteResult(string build)
    {
        EquipmentRequestWire.RequireLayout(TestBuilds.Packets, build);
        var body = Convert.FromHexString("0403020100000000");
        var request = EquipmentRequestWire.ReadRequest(TestBuilds.Packets, build, body, true);
        Assert.Equal(0x01020304, request.Reference);
        Assert.Equal(0x01020304, request.ItemId);
        Assert.Equal("000000000403020100000000", Convert.ToHexString(
            EquipmentRequestWire.WriteResult(TestBuilds.Packets, build, 0, request.Reference)));
        Assert.Throws<PacketFormatException>(() => EquipmentRequestWire.ReadRequest(TestBuilds.Packets, build, body[..4], true));
    }

    private static ShopCardRow Card(string name) => new(0, name, "lucci", true, false, 10, 10, 0,
        "Weapon", "Rifle", 203001, [new(100001, 203001, true, 1, 7, 10, 10, 0, 0)], 1000, 1001);
}
