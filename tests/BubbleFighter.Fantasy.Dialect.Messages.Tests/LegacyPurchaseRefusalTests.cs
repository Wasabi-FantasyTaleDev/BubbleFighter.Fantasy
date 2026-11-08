using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Dialect.Messages;
using Xunit;

namespace BubbleFighter.Fantasy.Dialect.Messages.Tests;

/// <summary>Pins the result word each legacy build's purchase dialog explains for every named refusal.</summary>
public sealed class LegacyPurchaseRefusalTests
{
    // kr-812 sub_827550 and the same switch in the other builds: -13 Lucci, 38 cash, 11 owned, 17 limit, 22 no more.
    [Theory]
    [InlineData("kr-812", 38)]
    [InlineData("cn-717", 38)]
    [InlineData("cn-324", 38)]
    [InlineData("cn-406", 38)]
    [InlineData("cn-404", 38)]
    [InlineData("cn-408", 38)]
    [InlineData("cn-705", 38)]
    [InlineData("cn-706", 38)]
    [InlineData("id-400", 38)]
    [InlineData("id-1809", 38)]
    [InlineData("cn-279", 36)] // cn-279 sub_6F09D0 explains a short cash balance as 36.
    public void EachRefusalWritesItsBuildCode(string build, int cashShort)
    {
        Assert.Equal(-13, Result(build, ShopPurchaseRefusal.LucciShort));
        Assert.Equal(cashShort, Result(build, ShopPurchaseRefusal.CashShort));
        Assert.Equal(11, Result(build, ShopPurchaseRefusal.AlreadyOwned));
        Assert.Equal(17, Result(build, ShopPurchaseRefusal.LimitReached));
        Assert.Equal(22, Result(build, ShopPurchaseRefusal.NoMoreAllowed));
        Assert.Equal(0, Result(build, ShopPurchaseRefusal.Unspecified));
        Assert.Equal("EE450F0001000000", Convert.ToHexString(ShopBinding.WriteReply(TestBuilds.Packets, build,
            new(0x0F45EE, true) { Refusal = ShopPurchaseRefusal.AlreadyOwned })));
    }

    [Fact]
    public void TheWholeReplyIsTheKeyThenTheCode()
    {
        Assert.Equal("EE450F00F3FFFFFF", Convert.ToHexString(ShopBinding.WriteReply(TestBuilds.Packets, "kr-812",
            new(0x0F45EE, false) { Refusal = ShopPurchaseRefusal.LucciShort })));
    }

    private static int Result(string build, ShopPurchaseRefusal refusal) =>
        BitConverter.ToInt32(ShopBinding.WriteReply(TestBuilds.Packets, build, new(1, false) { Refusal = refusal }), 4);
}
