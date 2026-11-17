using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.Match;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.InGame;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.InGame.Handlers;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Tests;
using BubbleFighter.Fantasy.Tests.Shared;
using BubbleFighter.Fantasy.Packets;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks restored native reward pools against configuration and each build's own catalog.</summary>
public sealed class NativeLotteryProfileTests
{
    /// <summary>Workbook bounds remain finite validated deployment settings, including when cards are disabled.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1023)]
    [InlineData(536870913)]
    public void InvalidWorkbookLimitFailsConfigurationBinding(int maximumBytes)
    {
        var row = new PluginRow { Id = "ingame", Plugin = "ingame", Source = "test",
            Config = new Dictionary<string, object?> { ["lottery_maximum_file_bytes"] = maximumBytes } };
        Assert.Throws<ConfigException>(() => new PluginConfig(row).Bind<InGameOptions>());
    }

    /// <summary>Only the completed pirate mode opens the audited KR award dialog, with native-compatible item terms.</summary>
    [Fact]
    public void ModernKoreanLotteryIsLimitedToSuccessfulPirateRounds()
    {
        var rows = Compose("kr-1256");
        var rooms = new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.rooms")).Bind<RoomsOptions>();
        var options = Options(rows);
        Assert.True(rooms.HordeLottery);
        Assert.Contains(101, rooms.ResultScoreboardModes);
        Assert.Equal([101], options.LotteryModes);
        Assert.Equal(73, options.LotteryItems.Length);
        Assert.Equal(134217728, options.LotteryMaximumFileBytes);
        Assert.Equal("Config/ShopItem2.xml", options.LotteryWorkbookClientPath);
        Assert.True(GroupedLottery.IsValid(options));
        Assert.All(options.LotteryItems, prize => Assert.True(LotteryCatalog.IsValidPrize(prize)));
        foreach (var reason in Enum.GetValues<RoundEndReason>())
            Assert.Equal(reason == RoundEndReason.WavesCleared, RoundFlow.LotteryAllowed(options, 101, reason));
        foreach (var mode in new[] { 1, 102, 103, 106 })
            Assert.False(RoundFlow.LotteryAllowed(options, mode, RoundEndReason.WavesCleared));
    }

    /// <summary>The modern CN and TW descendants do not inherit KR award-dialog activation or close behavior.</summary>
    [Theory]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void OtherModernBuildsKeepLotteryDisabled(string build)
    {
        var rows = Compose(build);
        Assert.False(new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.rooms")).Bind<RoomsOptions>().HordeLottery);
        Assert.Empty(Options(rows).LotteryModes);
        var tables = PacketTables.Load(SharedDialectRows.PacketsDirectory());
        Assert.False(tables.CapabilityOf(build, "horde_lottery"));
        Assert.False(tables.CapabilityOf(build, "horde_lottery_close_on_room_return"));
    }

    /// <summary>Each restored profile can fill ten cards within its configured duplicate limit.</summary>
    [Theory]
    [InlineData("cn-279", 28)]
    [InlineData("cn-406", 93)]
    [InlineData("cn-705", 93)]
    [InlineData("cn-706", 93)]
    public void ComposedPoolsFillBoundedBoards(string build, int count)
    {
        var rows = Compose(build);
        Assert.True(new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.rooms")).Bind<RoomsOptions>().HordeLottery);
        var options = Options(rows);
        Assert.Equal([101, 102, 103], options.LotteryModes);
        Assert.Equal(count, options.LotteryItems.Length);
        Assert.True(options.LotteryGroupedDraw);
        Assert.True(GroupedLottery.IsValid(options));
        Assert.All(options.LotteryItems, prize => Assert.True(LotteryCatalog.IsValidPrize(prize)));
        for (var run = 0; run < 100; run++)
        {
            var board = GroupedLottery.Draw(options);
            Assert.Equal(10, board.Length);
            Assert.All(board, prize => Assert.Contains(prize, options.LotteryItems));
            Assert.All(board.GroupBy(prize => prize.ItemId > 0 ? prize.ItemId : -prize.Lucci),
                group => Assert.InRange(group.Count(), 1, options.LotteryMaxDuplicates));
        }
    }

    /// <summary>A display identity must resolve to the configured item, term or currency in its own build.</summary>
    [Theory]
    [Trait("Category", "LocalClientData")]
    [InlineData("kr-1256")]
    [InlineData("cn-279")]
    [InlineData("cn-406")]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    public void ConfiguredRewardsExistInTheNativeWorkbook(string build)
    {
        var options = Options(Compose(build));
        var bytes = LocalClientData.Bytes(build, options.LotteryWorkbookClientPath);
        if (build == "kr-1256") Assert.True(bytes.Length > LotteryCatalog.DefaultMaximumFileBytes);
        LotteryCatalog.Validate(bytes, options.LotteryItems, options.LotteryMaximumFileBytes);
    }

    private static InGameOptions Options(IReadOnlyList<PluginRow> rows) =>
        new PluginConfig(Assert.Single(rows, row => row.Id == "ingame")).Bind<InGameOptions>();

    private static IReadOnlyList<PluginRow> Compose(string build)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "packets", "builds.yaml")))
            root = root.Parent ?? throw new InvalidOperationException("Repository packet tables were not found.");
        return new ProfileComposer(Path.Combine(root.FullName, "profiles"),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null).Compose(build, []);
    }
}
