using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.InGame;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Tests;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Checks restored native reward pools against configuration and each build's own catalog.</summary>
public sealed class NativeLotteryProfileTests
{
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
    [InlineData("cn-279")]
    [InlineData("cn-406")]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    public void ConfiguredRewardsExistInTheNativeWorkbook(string build)
    {
        var options = Options(Compose(build));
        LotteryCatalog.Validate(LocalClientData.Bytes(build, options.LotteryWorkbookClientPath), options.LotteryItems);
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
