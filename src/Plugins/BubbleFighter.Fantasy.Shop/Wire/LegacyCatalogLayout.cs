namespace BubbleFighter.Fantasy.Shop.Wire;

/// <summary>Which category tree a legacy <c>gspRepEnterShop</c> carries.</summary>
public enum LegacyCategoryTree
{
    /// <summary>The workbook's card-linked mains, written in <c>displayOrder</c> so the open main leads.</summary>
    Ordered,

    /// <summary>The card-linked mains plus the open main 1000 when it has no card.</summary>
    Landing,

    /// <summary>As <see cref="Landing"/>, plus each main's default subcategory rows.</summary>
    LandingDefaults,
}

/// <summary>What a legacy <c>gspRepEnterShop</c> carries after its category tree.</summary>
public enum LegacyCatalogTrailer
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>An empty counted <c>u32</c> vector.</summary>
    Ids,

    /// <summary>The empty counted vector, then the echoed main category.</summary>
    IdsMain,
}

/// <summary>
/// A legacy client's <c>gspRepEnterShop</c> layout as structural choices, read from the build's
/// <c>shop.catalog</c> values in <c>packets/builds.yaml</c> so that a new build states only how its reader differs.
/// </summary>
/// <param name="Token">Whether a has-data <c>bool32</c> and the request's cache token follow the result.</param>
/// <param name="WideStockType">Whether each stock's type field is a <c>u32</c> (28-byte stocks) instead of a <c>u16</c>.</param>
/// <param name="Tree">The category tree the reply carries.</param>
/// <param name="MainPageType">Whether every subcategory is written with page mode 2 (cards of the selected main) instead of its workbook mode.</param>
/// <param name="Trailer">What follows the category tree.</param>
public sealed record LegacyCatalogLayout(
    bool Token, bool WideStockType, LegacyCategoryTree Tree, bool MainPageType, LegacyCatalogTrailer Trailer);
