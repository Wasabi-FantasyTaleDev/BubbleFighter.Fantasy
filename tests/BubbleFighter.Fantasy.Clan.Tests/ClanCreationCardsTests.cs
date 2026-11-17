using Xunit;

namespace BubbleFighter.Fantasy.Clan.Tests;

/// <summary>
/// Reading the creation cards from the ShopItem sheet: function 22 rows are cards whose parameter is the
/// capacity; other functions are ignored and a malformed card row fails the load.
/// </summary>
public sealed class ClanCreationCardsTests
{
    private static readonly string[] Header = ["itemId", "itemName", "function", "parameter"];

    /// <summary>kr-1256's three function-22 rows become cards of 30, 30 and 10; a function-1 row and comment rows do not.</summary>
    [Fact]
    public void FunctionTwentyTwoRowsAreCardsWithTheirParameterAsCapacity()
    {
        var cards = ClanCreationCardsPlugin.Parse(
        [
            Header,
            ["100819", "card 30", "22", "30"],
            ["101826", "card once", "22", "30"],
            ["101979", "card 10", "22", "10"],
            ["100001", "spray", "1", "30"],
            ["#100002", "comment", "22", "5"],
            ["", "", "", ""],
        ], 8);
        Assert.True(cards.TryGetCapacity(100819, out var thirty));
        Assert.True(cards.TryGetCapacity(101826, out var once));
        Assert.True(cards.TryGetCapacity(101979, out var ten));
        Assert.Equal((30, 30, 10), (thirty, once, ten));
        Assert.False(cards.TryGetCapacity(100001, out _));
        Assert.False(cards.TryGetCapacity(100002, out _));
    }

    /// <summary>A card without a positive capacity, a repeated card, too many cards or a missing column fail the load.</summary>
    [Fact]
    public void MalformedCardTablesFail()
    {
        Assert.Throws<InvalidOperationException>(() => ClanCreationCardsPlugin.Parse([Header, ["100819", "", "22", "0"]], 8));
        Assert.Throws<InvalidOperationException>(() => ClanCreationCardsPlugin.Parse([Header, ["100819", "", "22", "30"], ["100819", "", "22", "10"]], 8));
        Assert.Throws<InvalidOperationException>(() => ClanCreationCardsPlugin.Parse([Header, ["1", "", "22", "1"], ["2", "", "22", "1"]], 1));
        Assert.Throws<InvalidOperationException>(() => ClanCreationCardsPlugin.Parse([["itemId", "function"], ["1", "22"]], 8));
        Assert.Throws<InvalidOperationException>(() => ClanCreationCardsPlugin.Parse([Header, ["x", "", "22", "1"]], 8));
    }

    /// <summary>
    /// kr-812's card states no member count: with a configured capacity it takes that number, while a stated parameter
    /// still wins over it.
    /// </summary>
    [Fact]
    public void CardWithoutParameterTakesTheConfiguredCapacity()
    {
        var cards = ClanCreationCardsPlugin.Parse([Header, ["100819", "", "22", "0"], ["101979", "", "22", "10"], ["100820", "", "22", ""]], 8, 30);
        Assert.True(cards.TryGetCapacity(100819, out var plain));
        Assert.True(cards.TryGetCapacity(101979, out var stated));
        Assert.True(cards.TryGetCapacity(100820, out var blank));
        Assert.Equal((30, 10, 30), (plain, stated, blank));
    }
}
