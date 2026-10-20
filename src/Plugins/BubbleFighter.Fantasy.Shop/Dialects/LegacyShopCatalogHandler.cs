using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Wire;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Shop.Dialects;

/// <summary>Serves the legacy catalog with the request and response shape selected at activation.</summary>
internal sealed class LegacyShopCatalogHandler
{
    private readonly LegacyShopShape _shape;
    private readonly ushort _reply;
    private readonly ShopCatalog _catalog;
    private readonly IReadOnlyList<ShopCardRow> _cards;
    private readonly ShopOpenScope _opens;
    private readonly IShopSessionStore _store;
    private readonly byte[]? _cachedReply;

    internal LegacyShopCatalogHandler(LegacyShopShape shape, ushort reply, ShopCatalog catalog, int maxBytes,
        ShopOpenScope opens, IShopSessionStore store, ILogger logger, int maxCards = ShopWire.LegacyEnterShopCardCap,
        bool preferLongestDuration = true, ShopOptions? termPolicy = null)
    {
        _shape = shape;
        _reply = reply;
        _catalog = catalog;
        _opens = opens;
        _store = store;
        var fixedBytes = Encode(new EnterShopRequest(0, 0), []).Length;
        if (fixedBytes > maxBytes)
            throw new InvalidOperationException($"Legacy shop category tree needs {fixedBytes} bytes, exceeding the {maxBytes}-byte payload budget.");
        var offers = termPolicy is not null ? LongestDurationCards(catalog.Cards, termPolicy)
            : preferLongestDuration ? LongestDurationCards(catalog.Cards) : catalog.Cards;
        var (cards, dropped) = ShopWire.SelectBoundedCards(offers, maxBytes - fixedBytes, shape.Catalog.WideStockType, maxCards: maxCards);
        _cards = cards;
        // A body-less request cannot vary the reply, so it is built once.
        if (shape.Request == LegacyEnterShopRequest.Empty) _cachedReply = Encode(new(0, 0), cards);
        logger.LogInformation("Legacy shop catalog: {Original} original, {Eligible} after term filtering, {Sent} sent, {Dropped} omitted; payload {Bytes}/{Budget} bytes.",
            catalog.Cards.Count, offers.Count, cards.Count, dropped, (_cachedReply ?? Encode(new(0, 0), cards)).Length, maxBytes);
        if (dropped > 0)
            logger.LogWarning("Legacy shop catalog sends {Sent} of {Total} cards within {Budget} bytes; {Dropped} cards are not exposed.",
                cards.Count, offers.Count, maxBytes, dropped);
    }

    internal static IReadOnlyList<ShopCardRow> LongestDurationCards(IReadOnlyList<ShopCardRow> cards, ShopOptions? policy = null)
    {
        bool Compact(ShopCardRow card) => policy is null
            || card.Stocks.Any(stock => policy.LongestTermItemIds.Contains(stock.ItemId))
            || (policy.PreferLongestDuration && card.StockSetId != 0 && policy.PreferLongestPackages)
            || (policy.PreferLongestDuration && card.StockSetId == 0 && card.GrantCategories.Count > 0 && card.GrantCategories.All(category => category switch
            {
                "guns" => policy.PreferLongestGuns,
                "clothes" => policy.PreferLongestClothes,
                "grenades" => policy.PreferLongestGrenades,
                "hammers" => policy.PreferLongestHammers,
                _ => false,
            }));
        // Item ids and grant multiplicity identify a product; category and currency retain distinct offers.
        var winners = cards.Where(card => card.StockSetId != 0 && card.Stocks.Count > 0 && Compact(card))
            .GroupBy(card => (card.MainCategoryId, card.SubCategoryId, card.MoneyType,
                Contents: string.Join(",", card.Stocks.Select(stock => stock.ItemId).Order())))
            .Select(group => group.OrderByDescending(card => card.Stocks.Count > 0
                    && card.Stocks.All(stock => stock.Duration == -1))
                .ThenByDescending(card => card.Stocks.Count == 0 ? 0 : card.Stocks.Min(stock => stock.Duration))
                .ThenBy(card => card.DisplayOrder).ThenBy(card => card.CardId).First())
            .ToHashSet();
        return cards.Where(card => card.StockSetId == 0 || card.Stocks.Count == 0 || !Compact(card) || winners.Contains(card))
            .Select(card =>
            {
                if (card.StockSetId != 0 || card.Stocks.Count <= 1 || !Compact(card)) return card;
                var term = card.Stocks.OrderByDescending(stock => stock.Duration == -1)
                    .ThenByDescending(stock => stock.Duration).ThenBy(stock => stock.StockId).First();
                return card with { Stocks = [term], ShowPrice = term.ShowPrice,
                    SellPrice = term.SellPrice, SellPriceCash = term.SellPriceCash };
            }).ToArray();
    }

    internal async ValueTask HandleAsync(ISession session, InboundPacket packet, CancellationToken ct)
    {
        var request = Parse(packet.Payload.Span);
        var record = new ShopOpenRecord(ShopPlugin.TryGetUserNum(session), request.Token, request.Unknown4, ShopKind.ItemShop);
        _opens.Remember(session.Connection.Id, record);
        await _store.RememberOpenAsync(record, ct).ConfigureAwait(false);
        await session.SendAsync(_reply, _cachedReply ?? Encode(request, _cards), ct).ConfigureAwait(false);
    }

    internal void ValidateRequest(ReadOnlySpan<byte> body) => Parse(body);

    private EnterShopRequest Parse(ReadOnlySpan<byte> body)
    {
        // Writers: kr-812 0x6DA560, id-400 0x63B750, id-1809 sub_676990, cn-706 sub_656D60, cn-324 0x5C7C90, cn-279 0x481D60.
        return _shape.Request switch
        {
            LegacyEnterShopRequest.Empty => Empty(body),
            LegacyEnterShopRequest.Token => new(EnterShopRequest400.Parse(body).Token, 0),
            _ => EnterShopRequest.Parse(body),
        };

        static EnterShopRequest Empty(ReadOnlySpan<byte> body)
        {
            EnterShopRequestEmpty.Parse(body);
            return new(0, 0);
        }
    }

    private byte[] Encode(EnterShopRequest request, IReadOnlyList<ShopCardRow> cards)
    {
        var layout = _shape.Catalog;
        var categories = layout.Tree switch
        {
            LegacyCategoryTree.Ordered => _catalog.Categories,
            LegacyCategoryTree.Landing => CategoriesWithLanding(),
            _ => ShopWire.MergeMainClickDefaults(CategoriesWithLanding(), _catalog.ShopVisibleSubcategories,
                _catalog.MainDefaultSubcategoryIds),
        };
        var main = layout.Trailer == LegacyCatalogTrailer.IdsMain
            ? ShopWire.ResolveEnterShopMainCategory(request.Unknown4, cards, _catalog.Categories)
            : ShopWire.LegacyEnterShopMainCategory;
        return ShopWire.RepEnterShopLegacy(layout, request.Token, cards, categories, main);
    }

    /// <summary>
    /// cn-279 / cn-324 open on main 1000 (<c>sub_66B390</c> / <c>sub_7087A0</c>) and
    /// the tab-click handlers (<c>sub_66B830</c> / <c>sub_7F82B0</c>) require that id
    /// in the tab list before calling the filter - the same gate as id-400
    /// <c>sub_8E1F40</c>. cn-324 also uses the sheet's page type and default-subcategory
    /// rows to choose the initial list on each main-tab click.
    /// </summary>
    private IReadOnlyList<ShopCategoryRow> CategoriesWithLanding() =>
        ShopWire.EnsureEnterShopLandingMain(_catalog.Categories, _catalog.ShopVisibleMains);
}
