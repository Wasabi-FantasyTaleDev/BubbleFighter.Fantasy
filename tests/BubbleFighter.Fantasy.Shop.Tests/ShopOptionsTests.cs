using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Shop;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

public sealed class ShopOptionsTests
{
    [Fact]
    public void ALeftoverShopItemXmlKeyFailsToBind()
    {
        // The path option was removed with S2; an operator patch that still sets it must fail loudly, naming the key,
        // instead of being ignored (the options binder rejects unknown keys).
        var row = new PluginRow
        {
            Id = "shop",
            Plugin = "shop",
            Source = "test",
            Config = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["shop_item_client_path"] = "Config/ShopItem2.xml",
                ["shop_item_xml"] = "C:/old/client/ShopItem2.xml",
            },
        };
        var error = Assert.Throws<ConfigException>(() => new PluginConfig(row).Bind<ShopOptions>());
        Assert.Contains("unknown key 'shop_item_xml'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryOnlyDoesNotRequireAnImplementedShopDialect()
    {
        var options = new ShopOptions { InventoryOnly = true, VersionMajor = 406, ShopItemClientPath = "unused.xml" };
        Assert.Empty(options.Validate());
        var results = new List<ValidationResult>();
        Assert.True(Validator.TryValidateObject(options, new ValidationContext(options), results, true));
    }

    [Fact]
    public void ValidateAcceptsDialect1256()
    {
        var options = new ShopOptions { VersionMajor = 1256, ShopItemClientPath = "Config/ShopItem2.xml" };
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void ValidateRejectsUnknownCatalogSchema()
    {
        var options = new ShopOptions { CatalogSchema = "kr812", ShopItemClientPath = "Config/ShopItem2.xml" };
        var failures = options.Validate().ToList();
        Assert.Contains(failures, f => f.MemberNames.Contains(nameof(ShopOptions.CatalogSchema)));
    }

    [Fact]
    public void ValidateAcceptsAnyVersionForALegacyCatalog()
    {
        var options = new ShopOptions { CatalogSchema = "legacy", VersionMajor = 1, ShopItemClientPath = "Config/ShopItem2.xml" };
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void TheGiftBoxNeedsTheItemGrantPathBecauseAReceiverTakesAGiftThroughIt()
    {
        var off = new ShopOptions { CatalogSchema = "legacy", ShopItemClientPath = "x.xml", PresentBox = true, GrantPurchasedItems = false };
        Assert.Contains(off.Validate(), f => f.MemberNames.Contains(nameof(ShopOptions.PresentBox)));
        var on = new ShopOptions { CatalogSchema = "legacy", ShopItemClientPath = "x.xml", PresentBox = true, GrantPurchasedItems = true };
        Assert.Empty(on.Validate());
        Assert.False(new ShopOptions().PresentBox);
    }

    [Theory]
    [InlineData(nameof(ShopOptions.MaxPresentsPerPlayer), 0)]
    [InlineData(nameof(ShopOptions.MaxPresentsPerPlayer), 4097)]
    [InlineData(nameof(ShopOptions.MaxPresentMessageChars), 0)]
    [InlineData(nameof(ShopOptions.MaxPresentMessageChars), 1025)]
    public void TheGiftBoxBoundsAreRangeChecked(string property, int value)
    {
        var options = new ShopOptions { ShopItemClientPath = "x.xml" };
        typeof(ShopOptions).GetProperty(property)!.SetValue(options, value);
        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), results, true));
        Assert.Contains(results, r => r.MemberNames.Contains(property));
    }

    [Fact]
    public void ValidateRejectsEmptyCatalogPath()
    {
        var options = new ShopOptions { VersionMajor = 1256, ShopItemClientPath = " " };
        var failures = options.Validate().ToList();
        Assert.Contains(failures, f => f.MemberNames.Contains(nameof(ShopOptions.ShopItemClientPath)));
    }

    [Fact]
    public void DataAnnotationsRequireProtocolAndProfile()
    {
        var options = new ShopOptions { Protocol = "", ClientProfile = "" };
        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        Assert.False(ok);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void DataAnnotationsRejectNonPositiveMaxOpenSessions()
    {
        var options = new ShopOptions
        {
            VersionMajor = 1256,
            ShopItemClientPath = "Config/ShopItem2.xml",
            MaxOpenSessions = 0,
        };
        var results = new List<ValidationResult>();
        var ok = Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        Assert.False(ok);
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(ShopOptions.MaxOpenSessions)));
    }

    [Fact]
    public void MaxOpenSessionsDefaultsToListenerSizedCap()
    {
        var options = new ShopOptions { VersionMajor = 1256, ShopItemClientPath = "Config/ShopItem2.xml" };
        Assert.Equal(512, options.MaxOpenSessions);
        Assert.Empty(options.Validate());
    }

    private static ShopOptions Legacy() => new() { CatalogSchema = "legacy", ShopItemClientPath = "x.xml", PresentBox = true, GrantPurchasedItems = true };

    [Fact]
    public void LevelRewardsAreOffByDefaultAndAcceptADistinctLevelTable()
    {
        Assert.Empty(new ShopOptions().LevelRewards);
        var options = Legacy();
        options.LevelRewards =
        [
            new ShopLevelReward { Level = 5, StockKeys = [1001] },
            new ShopLevelReward { Level = 10, StockKeys = [1002, 1003] },
        ];
        Assert.Empty(options.Validate());
        var results = new List<ValidationResult>();
        Assert.True(Validator.TryValidateObject(options, new ValidationContext(options), results, true));
    }

    [Theory]
    [InlineData(1, new[] { 1001 })]
    [InlineData(10_000, new[] { 1001 })]
    [InlineData(5, new int[0])]
    [InlineData(5, new[] { 0 })]
    [InlineData(5, new[] { -3 })]
    public void LevelRewardsRefuseAnOutOfRangeLevelOrAnEmptyOrNonPositiveStockList(int level, int[] keys)
    {
        var options = Legacy();
        options.LevelRewards = [new ShopLevelReward { Level = level, StockKeys = keys }];
        Assert.Contains(options.Validate(), f => f.MemberNames.Contains(nameof(ShopOptions.LevelRewards)));
    }

    [Fact]
    public void LevelRewardsRefuseARepeatedLevelTooManyGiftsForOneLevelAndTooManyEntries()
    {
        var options = Legacy();
        options.LevelRewards = [new() { Level = 5, StockKeys = [1] }, new() { Level = 5, StockKeys = [2] }];
        Assert.Contains(options.Validate(), f => f.MemberNames.Contains(nameof(ShopOptions.LevelRewards)));
        options.LevelRewards = [new() { Level = 5, StockKeys = Enumerable.Range(1, 17).ToArray() }];
        Assert.Contains(options.Validate(), f => f.MemberNames.Contains(nameof(ShopOptions.LevelRewards)));
        options.LevelRewards = [.. Enumerable.Range(2, ShopOptions.MaxLevelRewardEntries + 1).Select(level => new ShopLevelReward { Level = level, StockKeys = [1] })];
        Assert.Contains(options.Validate(), f => f.MemberNames.Contains(nameof(ShopOptions.LevelRewards)));
    }

    [Fact]
    public void TheLevelRewardMessageMustFitTheGiftMessageLimitOnceTheLevelIsFilledIn()
    {
        var options = Legacy();
        options.LevelRewards = [new ShopLevelReward { Level = 5, StockKeys = [1001] }];
        options.MaxPresentMessageChars = 10;
        options.LevelRewardMessage = "Level {level} reward";
        Assert.Contains(options.Validate(), f => f.MemberNames.Contains(nameof(ShopOptions.LevelRewardMessage)));
        options.LevelRewardMessage = "Lv {level}";
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void TheLevelRewardSenderNeedsANameThatFitsTheNicknameColumn()
    {
        var options = Legacy();
        options.LevelRewardSender = "";
        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), results, true));
        options.LevelRewardSender = new string('s', 65);
        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), results, true));
        options.LevelRewardSender = new string('s', 64);
        Assert.True(Validator.TryValidateObject(options, new ValidationContext(options), results = [], true));
    }
}
