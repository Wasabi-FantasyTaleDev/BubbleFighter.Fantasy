using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Shop.Wire;

namespace BubbleFighter.Fantasy.Shop.Dialects;

/// <summary>What a legacy client sends in its C2S 801 shop-entry request.</summary>
internal enum LegacyEnterShopRequest
{
    /// <summary>No body; every open receives the same catalog.</summary>
    Empty,

    /// <summary>One u64 token.</summary>
    Token,

    /// <summary>The u64 token, then the u32 main tab.</summary>
    TokenMain,
}

/// <summary>
/// A legacy build's item-shop layouts, read from the <c>shop</c> values of <c>packets/builds.yaml</c>
/// so that a new build inherits its base's layouts without code.
/// </summary>
/// <param name="Request">The C2S 801 request layout.</param>
/// <param name="Catalog">The S2C catalog layout.</param>
internal sealed record LegacyShopShape(LegacyEnterShopRequest Request, LegacyCatalogLayout Catalog)
{
    /// <summary>Reads the shape from a build's merged <c>builds.yaml</c> values.</summary>
    /// <param name="build">The packet-table build, for messages.</param>
    /// <param name="values">The build's values laid over its bases'.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="ConfigException">Thrown when a value is missing or unknown.</exception>
    public static LegacyShopShape From(string build, IReadOnlyDictionary<string, object?> values)
    {
        if (!values.TryGetValue("shop", out var raw) || raw is not IReadOnlyDictionary<string, object?> shop)
            throw new ConfigException($"Build '{build}' must define shop.enter_request and shop.catalog in packets/builds.yaml.");
        var request = Text(shop, "enter_request", "shop.enter_request") switch
        {
            "empty" => LegacyEnterShopRequest.Empty,
            "token" => LegacyEnterShopRequest.Token,
            "token_main" => LegacyEnterShopRequest.TokenMain,
            var other => throw new ConfigException($"Build '{build}' has unknown shop.enter_request '{other}'."),
        };
        if (!shop.TryGetValue("catalog", out var rawCatalog) || rawCatalog is not IReadOnlyDictionary<string, object?> catalog)
            throw new ConfigException($"Build '{build}' must define the map shop.catalog in packets/builds.yaml.");
        var layout = new LegacyCatalogLayout(
            Token: Text(catalog, "token", "shop.catalog.token") switch
            {
                "true" => true,
                "false" => false,
                var other => throw Unknown("token", other),
            },
            WideStockType: Text(catalog, "stock_type", "shop.catalog.stock_type") switch
            {
                "u16" => false,
                "u32" => true,
                var other => throw Unknown("stock_type", other),
            },
            Tree: Text(catalog, "tree", "shop.catalog.tree") switch
            {
                "ordered" => LegacyCategoryTree.Ordered,
                "landing" => LegacyCategoryTree.Landing,
                "landing_defaults" => LegacyCategoryTree.LandingDefaults,
                var other => throw Unknown("tree", other),
            },
            MainPageType: Text(catalog, "page_type", "shop.catalog.page_type") switch
            {
                "sheet" => false,
                "main" => true,
                var other => throw Unknown("page_type", other),
            },
            Trailer: Text(catalog, "trailer", "shop.catalog.trailer") switch
            {
                "none" => LegacyCatalogTrailer.None,
                "ids" => LegacyCatalogTrailer.Ids,
                "ids_main" => LegacyCatalogTrailer.IdsMain,
                var other => throw Unknown("trailer", other),
            });
        return new(request, layout);

        string Text(IReadOnlyDictionary<string, object?> map, string key, string name) =>
            map.TryGetValue(key, out var value) && value is string text ? text
                : throw new ConfigException($"Build '{build}' must define {name} in packets/builds.yaml.");

        ConfigException Unknown(string key, string value) =>
            new($"Build '{build}' has unknown shop.catalog.{key} '{value}'.");
    }
}
