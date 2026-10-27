using BubbleFighter.Fantasy.Contracts.Shop;

namespace BubbleFighter.Fantasy.Shop.Data;

/// <summary>
/// The id-range catalog tree: pure content selection over the workbook, with no wire knowledge. The dialect
/// sender encodes whatever tree this class picks.
/// </summary>
internal static class ShopRangeTree
{
    /// <summary>
    /// Builds the tab tree by SubCategory id range: a page belongs to the main whose id is its own id rounded down to
    /// the hundred (kr-1256 ShopItem2: 1xx under Clothes 100, 4xx under Characters 400, 10xx under Main 1000). The
    /// workbook has no parent column and its page names repeat (801 and 1003 are both "Packages"), so a tree linked
    /// by card names puts pages under the wrong tab. A main is published only with at least one card on one of its
    /// pages; a page is kept when the sheet shows it or a card uses it; cards on unpublished pages are dropped.
    /// kr-1256 sub_646600 builds one tab per published main and kr-1256 sub_64DAB0 walks that main's page list.
    /// </summary>
    /// <param name="catalog">The loaded catalog.</param>
    /// <param name="cards">The cards to place.</param>
    /// <returns>The published mains and the cards on their pages.</returns>
    internal static (IReadOnlyList<ShopCategoryRow> Categories, IReadOnlyList<ShopCardRow> Cards) BuildRangeTree(
        ShopCatalog catalog, IReadOnlyList<ShopCardRow> cards)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(cards);
        var mains = new List<ShopCategoryRow>();
        var placed = new List<ShopCardRow>();
        foreach (var main in catalog.ShopVisibleMains)
        {
            var mainCards = cards.Where(card => card.MainCategoryId == main.Id).ToList();
            var pages = catalog.AllSubcategories
                .Where(sub => sub.Id / 100 * 100 == main.Id
                    && (sub.ShopView != 0 || mainCards.Any(card => card.SubCategoryId == sub.Id)))
                .ToList();
            var pageIds = pages.Select(sub => sub.Id).ToHashSet();
            var onPages = mainCards.Where(card => pageIds.Contains(card.SubCategoryId)).ToList();
            if (onPages.Count == 0)
                continue;
            mains.Add(main with { Subcategories = pages });
            placed.AddRange(onPages);
        }
        return (mains, placed);
    }

    /// <summary>
    /// The selected category of the reply: the one the client asked for in C2S 801 when the published tree carries it
    /// as a main or a page, otherwise the first main by display order. The fill selects it through kr-1256 sub_64DAB0,
    /// which returns without drawing when the key is not in the dialog's category map.
    /// </summary>
    /// <param name="requested">The category the client sent.</param>
    /// <param name="categories">The published mains.</param>
    /// <returns>The category to send.</returns>
    internal static int SelectEnterShopCategory(int requested, IReadOnlyList<ShopCategoryRow> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        if (categories.Count == 0)
            return requested;
        if (categories.Any(main => main.Id == requested || main.Subcategories.Any(sub => sub.Id == requested)))
            return requested;
        return categories.OrderBy(main => main.DisplayOrder).ThenBy(main => main.Id).First().Id;
    }
}
