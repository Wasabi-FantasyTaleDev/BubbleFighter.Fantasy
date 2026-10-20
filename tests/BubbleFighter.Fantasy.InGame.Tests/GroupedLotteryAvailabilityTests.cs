using BubbleFighter.Fantasy.InGame.Data;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks guaranteed draw groups against the prize families that a native workbook actually supplies.</summary>
public sealed class GroupedLotteryAvailabilityTests
{
    /// <summary>Absent native groups do not manufacture currency or equipment display identities.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void MissingGuaranteedGroupsUseOnlyTheAvailableNativePool(bool crew, bool coins)
    {
        var items = Enumerable.Range(1, 4).Select(i => new LotteryItem
            { Group = "common", ItemId = 100000 + i, DisplayId = 1000000 + i, Days = 3 }).ToList();
        if (crew) items.Add(new() { Group = "crew", ItemId = 100203, DisplayId = 1000920, Days = 3 });
        if (coins)
            foreach (var amount in new[] { 100, 300 })
                items.Add(new() { Group = "coin", DisplayId = 2100000 + amount, Lucci = amount });
        var options = new InGameOptions
        {
            LotteryItems = [.. items], LotteryGroupWeights = [new() { Group = "common", Weight = 100 }],
            LotteryCrewCountWeights = [70, 25, 5], LotteryCoinCountWeights = [45, 30, 18, 7],
        };
        Assert.True(GroupedLottery.IsValid(options));
        for (var run = 0; run < 100; run++)
        {
            var cards = GroupedLottery.Draw(options);
            Assert.Equal(10, cards.Length);
            Assert.All(cards, card => Assert.Contains(card, items));
            Assert.InRange(cards.Count(card => card.Group == "crew"), crew ? 1 : 0, crew ? 3 : 0);
            Assert.InRange(cards.Count(card => card.Group == "coin"), coins ? 1 : 0, coins ? 4 : 0);
            Assert.All(cards.GroupBy(card => card.ItemId > 0 ? card.ItemId : card.DisplayId),
                group => Assert.InRange(group.Count(), 1, options.LotteryMaxDuplicates));
        }
    }

    /// <summary>Dropping guaranteed groups raises the required filler capacity rather than weakening the duplicate limit.</summary>
    [Fact]
    public void InsufficientRemainingNativeFamiliesAreRejected()
    {
        var options = new InGameOptions
        {
            LotteryItems = Enumerable.Range(1, 3).Select(i => new LotteryItem
                { Group = "common", ItemId = i, DisplayId = 1000000 + i, Days = 3 }).ToArray(),
            LotteryGroupWeights = [new() { Group = "common", Weight = 100 }],
            LotteryCrewCountWeights = [70, 25, 5], LotteryCoinCountWeights = [45, 30, 18, 7],
        };
        Assert.False(GroupedLottery.IsValid(options));
    }
}
