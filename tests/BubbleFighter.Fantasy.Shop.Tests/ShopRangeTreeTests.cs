using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Wire;
using Xunit;

namespace BubbleFighter.Fantasy.Shop.Tests;

/// <summary>
/// Pins the id-range tab tree of S2C 800 against the real kr-1256 workbook: every published tab has pages, every
/// card sits on a page of its own tab, and pages land under the main their id falls in even when names repeat.
/// </summary>
[Trait("Category", "LocalClientData")]
public sealed class ShopRangeTreeRealTests : IClassFixture<RealCatalogFixture>
{
    private readonly RealCatalogFixture _fixture;

    /// <summary>Initializes the test class with the shared fixture.</summary>
    /// <param name="fixture">The shared real-workbook fixture.</param>
    public ShopRangeTreeRealTests(RealCatalogFixture fixture) => _fixture = fixture;

    [Fact]
    public void EveryTabHasPagesAndEveryCardSitsOnAPageOfItsTab()
    {
        var catalog = _fixture.Catalog!;
        var (tree, cards) = ShopWire.BuildRangeTree(catalog, catalog.ItemShopCards.ToList());
        Assert.NotEmpty(tree);
        Assert.NotEmpty(cards);
        Assert.All(tree, main => Assert.NotEmpty(main.Subcategories));
        Assert.All(tree, main => Assert.All(main.Subcategories, page => Assert.Equal(main.Id, page.Id / 100 * 100)));
        Assert.All(cards, card => Assert.Contains(tree, main => main.Id == card.MainCategoryId
            && main.Subcategories.Any(page => page.Id == card.SubCategoryId)));
        Assert.Equal(tree.Count, tree.Select(main => main.Id).Distinct().Count());
    }

    [Fact]
    public void RepeatedPageNamesStayUnderTheirOwnTab()
    {
        var catalog = _fixture.Catalog!;
        var (tree, _) = ShopWire.BuildRangeTree(catalog, catalog.ItemShopCards.ToList());
        // 801 and 1003 are both named "Packages"; 1003 belongs to Main (1000), never to Packages (800).
        Assert.DoesNotContain(tree, main => main.Id != 1000 && main.Subcategories.Any(page => page.Id == 1003));
        Assert.DoesNotContain(tree, main => main.Id == 1000 && main.Subcategories.Any(page => page.Id is 303 or 304));
    }
}

/// <summary>Pins the selected category and the content revision of the id-range reply.</summary>
public sealed class ShopRangeReplyTests
{
    private static readonly IReadOnlyList<ShopCategoryRow> Tree =
    [
        new(300, "Weapons", 1002, [new ShopSubcategoryRow(300, "All", 1300, 1, 2), new ShopSubcategoryRow(302, "Rifle", 1301, 1, 0)]),
        new(100, "Clothes", 1004, [new ShopSubcategoryRow(107, "Sets", 1101, 1, 0)]),
    ];

    [Theory]
    [InlineData(100, 100)]   // a published main is echoed
    [InlineData(302, 302)]   // so is a page inside one
    [InlineData(1000, 300)]  // the task bar's 1000 is not published: the first tab by display order
    public void TheReplySelectsTheRequestedCategoryWhenPublished(int requested, int expected)
    {
        Assert.Equal(expected, ShopWire.SelectEnterShopCategory(requested, Tree));
    }

    [Fact]
    public void TheRevisionFollowsTheContentAndIsNeverZero()
    {
        var body = ShopWire.RepEnterShop(0, [], Tree, 0);
        Assert.Equal(ShopWire.CatalogRevision(body), ShopWire.CatalogRevision(body));
        Assert.NotEqual(0, ShopWire.CatalogRevision(body));
        Assert.NotEqual(ShopWire.CatalogRevision(body), ShopWire.CatalogRevision(ShopWire.RepEnterShop(0, [], Tree.Take(1).ToList(), 0)));
    }
}
