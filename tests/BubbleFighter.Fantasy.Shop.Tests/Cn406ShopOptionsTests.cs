using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Shop;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>Validates the composed cn-406 full-shop configuration against the plugin option rules.</summary>
public sealed class Cn406ShopOptionsTests
{
    [Fact]
    public void ComposedShopConfigurationBindsAndValidates()
    {
        var composer = new ProfileComposer(RepositoryRoot("profiles"),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null);
        var rows = composer.Compose("cn-406", []);

        var shop = new PluginConfig(Assert.Single(rows, row => row.Id == "shop")).Bind<ShopOptions>();
        Assert.False(shop.InventoryOnly);
        Assert.True(shop.SendShopCatalog);
        Assert.True(shop.GrantPurchasedItems);
        Assert.True(shop.PresentBox);
        Assert.False(shop.LifeItems);
        Assert.Equal("legacy", shop.CatalogSchema);
        Assert.Equal("Config/ShopItem2.xml", shop.ShopItemClientPath);
        Assert.Equal("name", shop.CatalogDisplayColumn);
        Assert.Equal(65000, shop.MaxEnterShopFrameBytes);
        Assert.Equal(0, shop.MaxEnterShopCards);

        var messages = new PluginConfig(Assert.Single(rows, row => row.Id == "dialect.messages")).Bind<MessagesOptions>();
        Assert.True(messages.EnablePurchases);
        // cn-406 sub_738290 case 107 -> sub_71BFB0 shows the notice.
        Assert.True(messages.ServerNotices);
        Assert.True(messages.Party);
        Assert.False(messages.CustomizedKeys);

        // The first-clear gift: cn-406's MapMeta carries IsItemAward on all 24 numbered tutorial stages
        // and every gift id resolves in this build's own workbook, so the composed ingame row enables it.
        var ingame = Assert.Single(rows, row => row.Id == "ingame");
        Assert.Equal("true", $"{ingame.Config!["tutorial_gifts"]}");
    }

    [Fact]
    public void ComposedOpcodeTableResolvesTheShopFamily()
    {
        var composer = new ProfileComposer(RepositoryRoot("profiles"),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null);
        var rows = composer.Compose("cn-406", []);
        var protocol = Assert.Single(rows, row => row.Id == "protocol");
        var table = OpcodeTableLoader.Load(Path.Combine(RepositoryRoot(""), (string)protocol.Config!["opcode_table_path"]!),
            new ClientProfile("cn-406", "cn", 406, 0, 0, "cn-406")).ForConnection(OpcodeConnection.Game);
        Assert.Equal(801, table.C2S("gcpEnterShop"));
        Assert.Equal(800, table.S2C("gspRepEnterShop"));
        Assert.Equal(825, table.C2S("gcpReqCheckCash"));
        Assert.Equal(808, table.C2S("gcpReqShopBuy"));
        Assert.Equal(813, table.S2C("gspRepShopBuy"));
    }

    private static string RepositoryRoot(string leaf)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "packets", "builds.yaml")))
                return Path.Combine(directory.FullName, leaf);
        throw new InvalidOperationException("Repository packet tables were not found.");
    }
}
