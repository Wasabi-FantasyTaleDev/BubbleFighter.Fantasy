using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Dialect.Rooms;
using BubbleFighter.Fantasy.InGame;
using BubbleFighter.Fantasy.InGame.Data;
using BubbleFighter.Fantasy.Kernel;
using Xunit;

namespace BubbleFighter.Fantasy.InGame.Tests;

/// <summary>Validates the composed cn-404 and cn-408 reward-card configuration against the option rules.</summary>
public sealed class Cn404Cn408LotteryOptionsTests
{
    /// <summary>Lists the two builds whose native reward workbooks define this pool.</summary>
    public static IEnumerable<object[]> Builds() =>
        new[] { new object[] { "cn-404" }, new object[] { "cn-408" } };

    [Theory]
    [MemberData(nameof(Builds))]
    public void ComposedLotteryConfigurationBindsAndValidates(string build)
    {
        var composer = new ProfileComposer(RepositoryRoot("profiles"),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null);
        var rows = composer.Compose(build, []);

        var rooms = new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.rooms")).Bind<RoomsOptions>();
        Assert.False(rooms.HordeLottery);

        // Binding runs the option rules: pool bounds, IsValidPrize per entry and the grouped-draw
        // capacity contract. An invalid reward value must fail here, before a listener ever loads.
        var options = new PluginConfig(Assert.Single(rows, row => row.Id == "ingame")).Bind<InGameOptions>();
        Assert.Empty(options.LotteryModes);
        Assert.True(options.LotteryGroupedDraw);
        Assert.Equal("Config/ShopItem2.xml", options.LotteryWorkbookClientPath);
        Assert.Equal(3, options.LotteryMaxDuplicates);
        // 85 entries: 16 crew, 20 small_parts, 20 mid_parts, 17 common, nine sets, three coins.
        Assert.Equal(85, options.LotteryItems.Length);
        Assert.True(GroupedLottery.IsValid(options));

        // Workbook-derived pins: the crew singles carry four pieces; the fifth (100205) rides the
        // sets only, and the workbooks' extra terms stay out of the draw.
        var singles = options.LotteryItems.Where(item => item.Components.Length == 0 && item.Lucci == 0).ToArray();
        Assert.DoesNotContain(singles, item => item.Group == "crew" && item.ItemId == 100205);
        Assert.Equal([1011995, 1011996, 1011998, 1011999],
            singles.Where(item => item.Group == "crew" && item.Days == 1).Select(item => item.DisplayId).Order().ToArray());
        Assert.Equal([1012000, 1012001, 1012003, 1012004],
            singles.Where(item => item.Group == "crew" && item.Days == 30).Select(item => item.DisplayId).Order().ToArray());
        Assert.DoesNotContain(singles, item => item.Group == "mid_parts" && item.Days == 15);
        Assert.Equal([1011473, 1011674],
            singles.Where(item => item.Stackable).Select(item => item.DisplayId).Order().ToArray());
        Assert.All(singles.Where(item => item.Stackable), item =>
        {
            Assert.Equal(10, item.Quantity);
            Assert.Equal(0, item.Days);
        });
        var coins = options.LotteryItems.Where(item => item.Group == "coin").ToArray();
        Assert.Equal(3, coins.Length);
        Assert.Equal([2100061, 2100062, 2100063], coins.Select(item => item.DisplayId).Order().ToArray());
        Assert.Equal([100, 300, 500], coins.OrderBy(item => item.DisplayId).Select(item => item.Lucci).ToArray());
        Assert.DoesNotContain(options.LotteryItems, item => item.DisplayId is 2100064 or 2100065);

        var sets = options.LotteryItems.Where(item => item.Components.Length > 0).ToArray();
        Assert.Equal(9, sets.Length);
        Assert.Equal([2100074, 2100143, 2100535, 2100537, 2100538, 2100539, 2100541, 2100542, 2100543],
            sets.Select(item => item.DisplayId).Order().ToArray());
        var crewSet = Assert.Single(sets, item => item.DisplayId == 2100143);
        Assert.Equal(5, crewSet.Components.Length);
        Assert.Contains(crewSet.Components, component => component.ItemId == 100205 && component.DisplayId == 1011415);
    }

    [Fact]
    public void Cn408SharesCn404sRewardPool()
    {
        // One audited table serves both builds: cn-408's workbook is a strict superset of cn-404's.
        var cn404 = BindIngame("cn-404");
        var cn408 = BindIngame("cn-408");
        Assert.Equivalent(cn404.LotteryItems, cn408.LotteryItems, strict: true);
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public void ComposedPoolDrawsFullBoardsWithinBounds(string build)
    {
        var options = BindIngame(build);
        for (var run = 0; run < 100; run++)
        {
            var cards = GroupedLottery.Draw(options);
            Assert.Equal(10, cards.Length);
            Assert.All(cards, card => Assert.Contains(card, options.LotteryItems));
            Assert.InRange(cards.Count(card => card.Group == "crew"), 1, 3);
            Assert.InRange(cards.Count(card => card.Group == "coin"), 1, 4);
            Assert.All(cards.GroupBy(card => card.ItemId > 0 ? card.ItemId : card.DisplayId),
                group => Assert.InRange(group.Count(), 1, options.LotteryMaxDuplicates));
        }
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public void CorruptedRewardWeightIsRejected(string build)
    {
        var composer = new ProfileComposer(RepositoryRoot("profiles"),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null);
        var rows = composer.Compose(build, []);
        var ingame = Assert.Single(rows, row => row.Id == "ingame");
        var mutated = ingame with
        {
            Config = new Dictionary<string, object?>(ingame.Config!)
            {
                ["lottery_items"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["group"] = "crew", ["item_id"] = 100203, ["display_id"] = 1011995,
                        ["days"] = 1, ["weight"] = 0,
                    },
                },
            },
        };
        Assert.Throws<ConfigException>(() => new PluginConfig(mutated).Bind<InGameOptions>());
    }

    private static InGameOptions BindIngame(string build)
    {
        var composer = new ProfileComposer(RepositoryRoot("profiles"),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null);
        var rows = composer.Compose(build, []);
        return new PluginConfig(Assert.Single(rows, row => row.Id == "ingame")).Bind<InGameOptions>();
    }

    private static string RepositoryRoot(string leaf)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "packets", "builds.yaml")))
                return Path.Combine(directory.FullName, leaf);
        throw new InvalidOperationException("Repository packet tables were not found.");
    }
}
