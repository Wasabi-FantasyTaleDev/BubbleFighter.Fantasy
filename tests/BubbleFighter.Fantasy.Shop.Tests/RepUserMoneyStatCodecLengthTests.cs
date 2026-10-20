using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Shop.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Direct byte-length pin for S2C 106 <see cref="ShopWire.RepUserMoneyStat"/> (A1 gap).
/// Length comes from the codec XML: six proven <c>i32</c> fields (24 bytes).
/// </summary>
public sealed class RepUserMoneyStatCodecLengthTests
{
    [Fact]
    public void RepUserMoneyStatPinsTwentyFourBytesInWireOrder()
    {
        // Six i32 in wire order (case 0x9D3328): four UI switches the server sends as 0, then
        // Lucci and Cash.
        var money = new PlayerWallet(UserNum: 99, Lucci: 5, Cash: 6);
        var payload = ShopWire.RepUserMoneyStat(money);
        Assert.Equal(24, payload.Length);
        Assert.Equal(
            "00000000" + "00000000" + "00000000" + "00000000" + "05000000" + "06000000",
            Convert.ToHexString(payload));
    }
}
