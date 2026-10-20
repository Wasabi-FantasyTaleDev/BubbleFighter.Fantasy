using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Shop.Data;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

public sealed class LevelRewardTableTests
{
    private static byte[] Guide(string lines) => Encoding.Unicode.GetPreamble()
        .Concat(Encoding.Unicode.GetBytes("Lv\tItemID\tMsg\n" + lines)).ToArray();

    private static ShopCatalog Catalog(params int[] keys) => new("test", [], [])
    {
        Gifts = keys.ToDictionary(key => key, key => new ShopGift(key, key, 50, [])),
    };

    [Fact]
    public void RepeatedPreviewsYieldOneMilestoneAndNumericPrefixesResolveGiftSets()
    {
        var rewards = LevelRewardTable.Read(Guide("1\t100_\t2 reward\n2\t200\t4 reward\n3\t200\t4 reward\n"),
            Catalog(100, 200), _ => Assert.Fail("No unresolved gifts expected."));
        Assert.Equal([2, 4], rewards.Select(reward => reward.Level));
        Assert.Equal([100, 200], rewards.SelectMany(reward => reward.StockKeys));
    }

    [Fact]
    public void PlaceholderKeysNeverBecomeInventedGifts()
    {
        var messages = new List<string>();
        Assert.Empty(LevelRewardTable.Read(Guide("1\t66666666\t3 reward\n2\t66666666\t3 reward"), Catalog(), messages.Add));
        Assert.Contains("66666666", Assert.Single(messages));
    }

    [Theory]
    [InlineData("1\t100\t3 reward")]
    [InlineData("2\t100\t3 reward")]
    [InlineData("1\t100\t2 reward\n1\t100\t2 reward")]
    [InlineData("1\t100xyz\t2 reward")]
    public void AmbiguousLevelsOrKeysAreRefused(string rows) =>
        Assert.Throws<ConfigException>(() => LevelRewardTable.Read(Guide(rows), Catalog(100), _ => { }));
}
