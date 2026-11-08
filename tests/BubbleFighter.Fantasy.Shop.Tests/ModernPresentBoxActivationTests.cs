using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Shop;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>Validates the composed gift-box activation on the three modern profiles.</summary>
public sealed class ModernPresentBoxActivationTests
{
    public static IEnumerable<object[]> Builds() =>
        new[] { new object[] { "kr-1256" }, new object[] { "cn-774" }, new object[] { "tw-454" } };

    [Theory]
    [MemberData(nameof(Builds))]
    public void ComposedActivationCarriesBothRowsAndResolvesTheRoutes(string build)
    {
        var composer = new ProfileComposer(RepositoryRoot("profiles"),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null);
        var rows = composer.Compose(build, []);

        var shop = new PluginConfig(Assert.Single(rows, row => row.Id == "shop")).Bind<ShopOptions>();
        Assert.True(shop.PresentBox, $"shop present_box={shop.PresentBox} keys=[{string.Join(",", Assert.Single(rows, row => row.Id == "shop").Config!.Keys)}]");
        var messages = new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.messages")).Bind<MessagesOptions>();
        Assert.True(messages.PresentBox, $"messages present_box={messages.PresentBox}");
        // The modern purchase routes belong to the shop row's tableCatalog path, not the legacy
        // enable_purchases dialect block, which the modern bundles never state.
        Assert.False(messages.EnablePurchases);

        // The claim path rides the inventory push service; the activation is dead without it.
        var lobby = Assert.Single(rows, row => row.Id == "lobby");
        Assert.Equal("mysql", $"{lobby.Config!["inventory_source"]}");
    }

    private static string RepositoryRoot(string leaf)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "packets", "builds.yaml")))
                return Path.Combine(directory.FullName, leaf);
        throw new InvalidOperationException("Repository packet tables were not found.");
    }
}
