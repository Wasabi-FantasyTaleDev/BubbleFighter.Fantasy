using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Shop.Data;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Shop.Handlers;

/// <summary>
/// Serves the legacy item-shop catalog: remembers the open, keeps the term-filtered eligible card list and
/// publishes it as a typed catalog message. The dialect sender applies the build's catalog layout from
/// <c>packets/builds.yaml</c>, bounds the card vector to the frame budget and encodes the reply; this handler
/// never sees a byte.
/// </summary>
internal sealed class ShopCatalogHandler
{
    private readonly ShopCatalog _catalog;
    private readonly ShopOpenScope _opens;
    private readonly IShopSessionStore _store;
    private readonly IMessageSender<ShopCatalogPublished> _sender;
    private readonly IReadOnlyList<ShopCardRow> _eligible;
    private readonly int _byteBudget;
    private readonly int _cardCap;
    private readonly ILogger _logger;

    internal ShopCatalogHandler(ShopCatalog catalog, int maxBytes, int maxCards, ShopOpenScope opens,
        IShopSessionStore store, IMessageSender<ShopCatalogPublished> sender, ILogger logger,
        bool preferLongestDuration = true, ShopOptions? termPolicy = null)
    {
        _catalog = catalog;
        _opens = opens;
        _store = store;
        _sender = sender;
        _logger = logger;
        _byteBudget = maxBytes;
        _cardCap = maxCards;
        var offers = termPolicy is not null ? LongestDurationCards(catalog.Cards, termPolicy)
            : preferLongestDuration ? LongestDurationCards(catalog.Cards) : catalog.Cards;
        _eligible = offers;
        _logger.LogInformation(
            "Legacy shop catalog: {Original} original, {Eligible} after term filtering; budget {Budget} bytes, card cap {Cap}.",
            catalog.Cards.Count, offers.Count, maxBytes, maxCards);
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

    /// <summary>Remembers the open and publishes the catalog for the session's build.</summary>
    /// <param name="session">The session that opened the shop.</param>
    /// <param name="request">The typed catalog request.</param>
    /// <param name="ct">Cancellation token.</param>
    internal async ValueTask HandleAsync(ISession session, ShopCatalogRequested request, CancellationToken ct)
    {
        var record = new ShopOpenRecord(ShopPlugin.TryGetUserNum(session), request.Token, request.MainCategory, ShopKind.ItemShop);
        _opens.Remember(session.Connection.Id, record);
        await _store.RememberOpenAsync(record, ct).ConfigureAwait(false);
        await _sender.SendAsync(session, new(request.Token, request.MainCategory, _eligible, _catalog.Categories,
            _catalog.ShopVisibleMains, _catalog.ShopVisibleSubcategories, _catalog.MainDefaultSubcategoryIds)
        {
            ByteBudget = _byteBudget, CardCap = _cardCap,
        }, ct).ConfigureAwait(false);
    }
}
