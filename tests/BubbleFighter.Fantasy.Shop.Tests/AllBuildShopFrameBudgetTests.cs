using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Dialect.Messages;
using BubbleFighter.Fantasy.Kernel;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Protocol;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Handlers;
using BubbleFighter.Fantasy.Tests;
using Xunit;
using Xunit.Abstractions;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>Audits real catalogs against each build's configured payload budget and native record widths.</summary>
[Trait("Category", "LocalClientData")]
public sealed class AllBuildShopFrameBudgetTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("cn-279")]
    [InlineData("cn-324")]
    [InlineData("cn-406")]
    [InlineData("cn-404")]
    [InlineData("cn-408")]
    [InlineData("cn-705")]
    [InlineData("cn-706")]
    [InlineData("cn-717")]
    [InlineData("id-400")]
    [InlineData("id-1809")]
    [InlineData("kr-812")]
    [InlineData("kr-1256")]
    [InlineData("cn-774")]
    [InlineData("tw-454")]
    public void ActualCatalogFitsItsFrameAndRetainsItsCategories(string build)
    {
        var root = FindRoot();
        var tables = PacketTables.Load(Path.Combine(root, "packets"));
        var composer = new ProfileComposer(Path.Combine(root, "profiles"),
            key => key is "BFF_MYSQL_PASSWORD" or "BFF_ADMIN_TOKEN" ? "isolated-test-placeholder" : null);
        var rows = composer.Compose(build, []);
        var options = new PluginConfig(Assert.Single(rows, row => row.Id == "shop")).Bind<ShopOptions>();
        var protocol = new PluginConfig(Assert.Single(rows, row => row.Id == "protocol")).Bind<ProtocolOptions>();
        var catalog = ShopCatalogLoader.Load(WorkbookFile.FromBytes(options.ShopItemClientPath,
            LocalClientData.Bytes(build, options.ShopItemClientPath)), options.MaxShopItemBytes,
            options.CatalogSchema == "legacy" ? ShopCatalogSchema.Legacy : ShopCatalogSchema.Kr1256,
            options.CatalogDisplayColumn.Length == 0 ? null : options.CatalogDisplayColumn, null,
            options.SupplementalOffers, options.ClientProfile);
        byte[] body;
        int sent;
        int eligible;
        if (tables.Resolve(build, "EnterShopReply").Count > 0)
        {
            var (tree, cards) = ShopRangeTree.BuildRangeTree(catalog, catalog.Cards);
            eligible = cards.Count;
            var written = ShopCatalogBinding.WriteBudgetedCatalog(tables, build, 0,
                ShopRangeTree.SelectEnterShopCategory(0, tree), cards, tree, true, options.CatalogPayloadBytes);
            body = written.Body;
            sent = written.Sent;
            if (options.CatalogPayloadBytes > 0) Assert.InRange(body.Length, 1, options.CatalogPayloadBytes);
        }
        else
        {
            Assert.Equal(0, options.MaxEnterShopCards);
            var layout = LegacyLayouts.Of(build);
            var categories = layout.Tree switch
            {
                LegacyCategoryTree.Ordered => catalog.Categories,
                LegacyCategoryTree.Landing => ShopWire.EnsureEnterShopLandingMain(catalog.Categories, catalog.ShopVisibleMains),
                _ => ShopWire.MergeMainClickDefaults(ShopWire.EnsureEnterShopLandingMain(catalog.Categories, catalog.ShopVisibleMains),
                    catalog.ShopVisibleSubcategories, catalog.MainDefaultSubcategoryIds),
            };
            var cards = ShopCatalogHandler.LongestDurationCards(catalog.Cards, options);
            eligible = cards.Count;
            var fixedBytes = ShopWire.RepEnterShopLegacy(layout, 0, [], categories).Length;
            var (selected, _) = ShopWire.SelectBoundedCards(cards, options.MaxEnterShopFrameBytes - fixedBytes, layout.WideStockType);
            body = ShopWire.RepEnterShopLegacy(layout, 0, selected, categories);
            sent = selected.Count;
            Assert.InRange(body.Length, 1, options.MaxEnterShopFrameBytes);
            foreach (var category in cards.Select(card => (card.MainCategoryId, card.SubCategoryId)).Distinct())
                Assert.Contains(selected, card => (card.MainCategoryId, card.SubCategoryId) == category);
        }
        var frame = (string)Assert.Single(rows, row => row.Id == "dialect").Config!["frame"]!;
        // A 16-bit length caps a legacy frame; any frame must also fit the session's send queue to be sent at all.
        if (frame.StartsWith("legacy_", StringComparison.Ordinal))
            Assert.InRange(body.Length, 1, ushort.MaxValue);
        Assert.InRange(body.Length, 1, protocol.SendQueueBytes);
        Assert.True(sent > 0);
        output.WriteLine($"{build}: {sent}/{eligible} cards, {body.Length} payload bytes");
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "packets", "builds.yaml"))) return directory.FullName;
        throw new InvalidOperationException("Repository packet tables were not found.");
    }
}
