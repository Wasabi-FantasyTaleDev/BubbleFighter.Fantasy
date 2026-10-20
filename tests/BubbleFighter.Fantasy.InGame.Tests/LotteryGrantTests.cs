using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.InGame;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.InGame.Data;
using System.Xml.Linq;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks repeat reward ownership, renewal deadlines and bounded card allocation.</summary>
public sealed class LotteryGrantTests
{
    /// <summary>Database time consumes the reveal interval instead of extending the native modal's lifetime.</summary>
    [Theory]
    [InlineData(7000, 5000)]
    [InlineData(9000, 3000)]
    [InlineData(12000, 0)]
    [InlineData(14000, 0)]
    public void DisplayDeadlineIncludesAwardProcessing(int elapsed, int expected) =>
        Assert.Equal(expected, RoundFlow.LotteryRemainingDisplayMs(1000, 1000 + elapsed, 7000, 5000));

    /// <summary>Native card sale identifiers must resolve to the actual item and matching displayed amount.</summary>
    [Fact]
    public void CardDisplayRejectsEquipmentIdsMissingSalesAndWrongAmounts()
    {
        var workbook = XDocument.Parse("""
            <Workbook xmlns="urn:schemas-microsoft-com:office:spreadsheet" xmlns:ss="urn:schemas-microsoft-com:office:spreadsheet">
              <Worksheet ss:Name="Stock"><Table>
                <Row><Cell><Data>stockId</Data></Cell><Cell><Data>itemId</Data></Cell><Cell><Data>type</Data></Cell><Cell><Data>duration</Data></Cell></Row>
                <Row><Cell><Data>1013069</Data></Cell><Cell><Data>100443</Data></Cell><Cell><Data>1</Data></Cell><Cell><Data>5</Data></Cell></Row>
              </Table></Worksheet>
            </Workbook>
            """);
        var prize = new LotteryItem { DisplayId = 1013069, ItemId = 100443, Quantity = 5, Stackable = true };
        LotteryCatalog.Validate(workbook, [prize]);
        prize.DisplayId = 100443;
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.ConfigException>(() => LotteryCatalog.Validate(workbook, [prize]));
        prize.DisplayId = 1013069;
        prize.Quantity = 10;
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.ConfigException>(() => LotteryCatalog.Validate(workbook, [prize]));
    }
    /// <summary>Repeated consumable rewards increment the existing stack with a conditional transaction.</summary>
    [Fact]
    public void ConsumableGrantIncrementsExistingQuantity()
    {
        var existing = new InventorySlotRecord(1, InventoryBag.CharacterBag, 99, 100443, 7, 0)
            { Lifetime = InventoryLifetime.Permanent };
        var grant = RoundFlow.PlanLotteryGrant(new() { ItemId = 100443, Quantity = 5, Stackable = true }, existing, DateTimeOffset.UtcNow);
        Assert.Equal(12, grant.Quantity);
        Assert.Equal(existing, grant.ExpectedSlot);
        Assert.Equal(ShopGrantMode.MatchItem, grant.Mode);
        Assert.Equal(InventoryLifetime.Permanent, grant.Lifetime);
        Assert.Equal(int.MaxValue, RoundFlow.PlanLotteryGrant(new() { ItemId = 100443, Quantity = 5, Stackable = true },
            existing with { Quantity = int.MaxValue }, DateTimeOffset.UtcNow).Quantity);
    }

    /// <summary>Timed prizes renew remaining ownership without degrading permanent equipment.</summary>
    [Fact]
    public void EquipmentGrantExtendsExpiryAndKeepsPermanentOwnership()
    {
        var now = DateTimeOffset.Parse("2026-10-04T00:00:00Z");
        var existing = new InventorySlotRecord(1, InventoryBag.CharacterBag, 99, 202207, 1, 0)
            { Lifetime = InventoryLifetime.Until(now.AddDays(2)) };
        var prize = new LotteryItem { ItemId = 202207, Days = 1 };
        Assert.Equal(InventoryLifetime.Until(now.AddDays(3)), RoundFlow.PlanLotteryGrant(prize, existing, now).Lifetime);
        Assert.Equal(InventoryLifetime.Until(now.AddDays(1)), RoundFlow.PlanLotteryGrant(prize,
            existing with { Lifetime = InventoryLifetime.Until(now.AddDays(-1)) }, now).Lifetime);
        Assert.Equal(InventoryLifetime.Permanent, RoundFlow.PlanLotteryGrant(prize,
            existing with { Lifetime = InventoryLifetime.Permanent }, now).Lifetime);
        Assert.Equal(1, RoundFlow.PlanLotteryGrant(new() { ItemId = 202207, Days = 0 }, existing, now).Quantity);
    }

    /// <summary>The native dialog always allocates exactly ten cards, all drawn from the configured pool.</summary>
    [Fact]
    public void CardDrawIsBoundedAndUsesOnlyConfiguredItems()
    {
        var pool = new[] { new LotteryItem { ItemId = 100443, Weight = 2 }, new LotteryItem { ItemId = 202207, Weight = 1 } };
        var cards = RoundFlow.DrawLotteryCards(pool);
        Assert.Equal(10, cards.Length);
        Assert.All(cards, card => Assert.Contains(card, pool));
        Assert.Throws<BubbleFighter.Fantasy.Abstractions.ConfigException>(() => RoundFlow.DrawLotteryCards([]));
    }
    /// <summary>Reserved card counts and duplicate caps hold across duration variants and repeated boards.</summary>
    [Fact]
    public void GroupedBoardsReserveRewardsAndCapEquipmentAcrossDurations()
    {
        var options = new InGameOptions
        {
            LotteryCrewCountWeights = [70, 25, 5], LotteryCoinCountWeights = [45, 30, 18, 7],
            LotteryGroupWeights = [new() { Group = "common", Weight = 800 }], LotteryMaxDuplicates = 3,
            LotteryItems = Enumerable.Range(1, 4).SelectMany(id => new[]
                {
                    new LotteryItem { Group = "crew", ItemId = id, Days = 3, Weight = 60 },
                    new LotteryItem { Group = "crew", ItemId = id, Days = 7, Weight = 12 },
                    new LotteryItem { Group = "common", ItemId = id + 10 },
                }).Concat(new[] { new LotteryItem { Group = "coin", Lucci = 100, Weight = 65 },
                    new LotteryItem { Group = "coin", Lucci = 300, Weight = 25 },
                    new LotteryItem { Group = "coin", Lucci = 500, Weight = 10 } }).ToArray(),
        };
        for (var board = 0; board < 500; board++)
        {
            var cards = GroupedLottery.Draw(options);
            Assert.Equal(10, cards.Length);
            Assert.InRange(cards.Count(c => c.Group == "crew"), 1, 3);
            Assert.InRange(cards.Count(c => c.Group == "coin"), 1, 4);
            Assert.All(cards.Where(c => c.Lucci == 0).GroupBy(c => c.ItemId), g => Assert.InRange(g.Count(), 1, 3));
            Assert.All(cards.Where(c => c.Lucci > 0).GroupBy(c => c.Lucci), g => Assert.InRange(g.Count(), 1, 3));
        }
    }
}
