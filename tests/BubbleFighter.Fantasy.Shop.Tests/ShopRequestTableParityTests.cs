using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Shop.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Pins every kr-1256 shop request table to the hand-written parser that is live today: the values
/// <see cref="PacketCodec.ReadScalars"/> returns from the same bytes must be the values
/// <see cref="ShopRequests"/> parses, and both must reject a body one byte short and one byte long.
/// The tables therefore cannot drift from the live parsers while no request is rewired.
/// </summary>
public sealed class ShopRequestTableParityTests
{
    private static readonly PacketTables Tables = LoadTables();

    [Fact]
    public void EnterShopRequestTableMatchesTheParser()
    {
        var payload = Payload(writer =>
        {
            writer.WriteInt64(0x1122334455667788);
            writer.WriteInt32(1000);
        });
        Assert.Equal(12, payload.Length);
        var parsed = EnterShopRequest.Parse(payload);
        var values = PacketCodec.ReadScalars(Tables, "kr-1256", "gcpEnterShop", payload);
        Assert.Equal(parsed.Token, Assert.IsType<long>(values["token"]));
        Assert.Equal(parsed.Unknown4, Assert.IsType<int>(values["unknown_16"]));
        AssertBothReject("gcpEnterShop", payload, body => EnterShopRequest.Parse(body));
    }

    [Fact]
    public void EnterTCShopRequestTableMatchesTheParser()
    {
        var payload = Payload(writer => writer.WriteInt64(-9000000001L));
        Assert.Equal(8, payload.Length);
        var parsed = EnterTCShopRequest.Parse(payload);
        var values = PacketCodec.ReadScalars(Tables, "kr-1256", "gcpEnterTCShop_", payload);
        Assert.Equal(parsed.Token, Assert.IsType<long>(values["token"]));
        AssertBothReject("gcpEnterTCShop_", payload, body => EnterTCShopRequest.Parse(body));
    }

    [Fact]
    public void ShopBuyRequestTableMatchesTheParser()
    {
        var payload = Payload(writer =>
        {
            writer.WriteInt32(4000001);
            writer.WriteInt32(0);
        });
        Assert.Equal(8, payload.Length);
        var parsed = ShopBuyRequest.Parse(payload);
        var values = PacketCodec.ReadScalars(Tables, "kr-1256", "ShopPurchaseRequest", payload);
        Assert.Equal(parsed.ItemId, Assert.IsType<int>(values["key"]));
        Assert.Equal(parsed.Unknown4, Assert.IsType<int>(values["unknown_4"]));
        AssertBothReject("ShopPurchaseRequest", payload, body => ShopBuyRequest.Parse(body));
    }

    [Fact]
    public void ItemCardInfoRequestTableMatchesTheParser()
    {
        var payload = Payload(writer => writer.WriteInt32(42));
        Assert.Equal(4, payload.Length);
        var parsed = ShopItemCardInfoRequest.Parse(payload);
        var values = PacketCodec.ReadScalars(Tables, "kr-1256", "gcpReqShopItemCardInfo", payload);
        Assert.Equal(parsed.ItemId, Assert.IsType<int>(values["unknown_0"]));
        AssertBothReject("gcpReqShopItemCardInfo", payload, body => ShopItemCardInfoRequest.Parse(body));
    }

    [Fact]
    public void RealCouponUseRequestTableMatchesTheParser()
    {
        var payload = Payload(writer => writer.WriteWideStringPrefixed("AB"));
        Assert.Equal(8, payload.Length);
        var parsed = RealCouponUseRequest.Parse(payload);
        var values = PacketCodec.ReadScalars(Tables, "kr-1256", "gcpReqRealCouponUse", payload);
        Assert.Equal(parsed.Unknown0, Assert.IsType<string>(values["unknown_0"]));
        AssertBothReject("gcpReqRealCouponUse", payload, body => RealCouponUseRequest.Parse(body));
    }

    [Fact]
    public void Unknown834RequestTableMatchesTheParser()
    {
        var payload = Payload(writer => writer.WriteWideStringPrefixed("12345678901234567890"));
        Assert.Equal(44, payload.Length);
        var parsed = CheckCashRequest.Parse(payload);
        var values = PacketCodec.ReadScalars(Tables, "kr-1256", "Unknown834", payload);
        Assert.Equal(parsed.Unknown1, Assert.IsType<string>(values["unknown_0"]));
        AssertBothReject("Unknown834", payload, body => CheckCashRequest.Parse(body));
    }

    [Fact]
    public void Unknown2059RequestTableMatchesTheParser()
    {
        var payload = Payload(writer =>
        {
            writer.WriteByte(0x0D);
            writer.WriteInt32(0);
        });
        Assert.Equal(5, payload.Length);
        var parsed = SceneBootstrapRequest.Parse(payload);
        var values = PacketCodec.ReadScalars(Tables, "kr-1256", "Unknown2059", payload);
        Assert.Equal(parsed.Unknown0, Assert.IsType<byte>(values["unknown_0"]));
        Assert.Equal(parsed.Unknown4, Assert.IsType<int>(values["unknown_4"]));
        AssertBothReject("Unknown2059", payload, body => SceneBootstrapRequest.Parse(body));
    }

    private static byte[] Payload(Action<PacketWriter> write) => WireLengthSupport.Written(write);

    private static void AssertBothReject(string structure, byte[] payload, Func<byte[], object> parse)
    {
        var shortBody = payload[..^1];
        var longBody = new byte[payload.Length + 1];
        payload.CopyTo(longBody, 0);

        Assert.Throws<PacketFormatException>(() => parse(shortBody));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(Tables, "kr-1256", structure, shortBody));
        Assert.Throws<PacketFormatException>(() => parse(longBody));
        Assert.Throws<PacketFormatException>(() => PacketCodec.ReadScalars(Tables, "kr-1256", structure, longBody));
    }

    private static PacketTables LoadTables()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BubbleFighter.Fantasy.sln")))
                return PacketTables.Load(Path.Combine(dir.FullName, "packets"));
        throw new InvalidOperationException("Repository root not found above the test output.");
    }
}
