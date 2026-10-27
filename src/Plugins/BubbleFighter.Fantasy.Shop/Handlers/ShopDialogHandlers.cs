using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Purchasing;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Shop.Handlers;

/// <summary>
/// The catalog-schema shop dialogs: the item-shop and cash-shop entries, the card-info, coupon and cash-check
/// requests, and the inventory-dialog flush of held equip records. Every reply leaves through a typed sender;
/// the handler never sees an opcode or a byte.
/// </summary>
internal sealed class ShopDialogHandlers(
    ShopOptions options,
    IShopSessionStore store,
    ShopCatalog catalog,
    IReadOnlyList<ShopCardRow> itemCards,
    IReadOnlyList<ShopCardRow> cashCards,
    WalletStore wallets,
    ShopPurchaser purchaser,
    bool persistPurchases,
    IPlayerWalletRepository? money,
    ShopOpenScope opens,
    IMessageSender<ShopCatalogPublished> catalogSender,
    IMessageSender<ShopCashCatalogPublished> cashCatalogSender,
    IMessageSender<ShopCardInfoReplied> cardInfoSender,
    IMessageSender<ShopCouponUseReplied> couponSender,
    IMessageSender<ShopCashCheckReplied> cashCheckSender,
    IMessageSender<ShopPurchaseReplied> buyReplySender,
    ILogger logger)
{

    /// <summary>Handles the item-shop entry: remember the open and publish the catalog when the row sends it.</summary>
    /// <param name="session">The session that opened the shop.</param>
    /// <param name="request">The typed catalog request.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask CatalogAsync(ISession session, ShopCatalogRequested request, CancellationToken ct)
    {
        var record = new ShopOpenRecord(ShopPlugin.TryGetUserNum(session), request.Token, request.MainCategory, ShopKind.ItemShop);
        opens.Remember(session.Connection.Id, record);
        await store.RememberOpenAsync(record, ct).ConfigureAwait(false);
        logger.LogInformation(
            "Session {Id} gcpEnterShop token={Token} cards={Cards} categories={Cats}.",
            session.Id, request.Token, itemCards.Count, catalog.Categories.Count);

        // gspRepEnterShop is the only packet that carries shop content: nothing
        // client-local fills the dialog, so withholding the reply is what made
        // the shop blank by construction. An earlier run that sent it and still
        // saw nothing was our own layout bug - the counted i32 vector that sits
        // before the trailing field was never written, so the trailing value was
        // consumed as that vector's count, the selected category stayed zero, and
        // the fill returned without drawing a card.
        if (!options.SendShopCatalog)
            return;
        if (options.CatalogTree == "id_ranges")
        {
            var (tree, placed) = ShopRangeTree.BuildRangeTree(catalog, itemCards);
            var selected = ShopRangeTree.SelectEnterShopCategory(request.MainCategory, tree);
            logger.LogInformation(
                "Session {Id} gspRepEnterShop: {Mains} tab(s), {Placed} card(s), selected {Selected} (requested {Requested}).",
                session.Id, tree.Count, placed.Count, selected, request.MainCategory);
            await catalogSender.SendAsync(session, new(0, selected, placed, tree, [], [], EmptyDefaults)
            {
                ContentRevisionToken = true,
                CardLimit = options.CatalogCardLimit,
                ByteBudget = options.CatalogPayloadBytes,
            }, ct).ConfigureAwait(false);
            return;
        }
        if (catalog.Categories.All(c => c.Id != options.EnterShopMainCategory))
            logger.LogWarning("Session {Id} selected shop category {Category} is absent from the catalog.",
                session.Id, options.EnterShopMainCategory);
        await catalogSender.SendAsync(session, new(request.Token, options.EnterShopMainCategory, itemCards,
            catalog.Categories, [], [], EmptyDefaults)
        {
            CardLimit = options.CatalogCardLimit,
            ByteBudget = options.CatalogPayloadBytes,
        }, ct).ConfigureAwait(false);
    }

    private static readonly IReadOnlyDictionary<int, int> EmptyDefaults = new Dictionary<int, int>();

    /// <summary>Handles the cash-shop entry: remember the open and publish the cash catalog.</summary>
    /// <param name="session">The session that opened the cash shop.</param>
    /// <param name="request">The typed cash catalog request.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask CashCatalogAsync(ISession session, ShopCashCatalogRequested request, CancellationToken ct)
    {
        var record = new ShopOpenRecord(ShopPlugin.TryGetUserNum(session), request.Token, 0, ShopKind.CashShop);
        opens.Remember(session.Connection.Id, record);
        await store.RememberOpenAsync(record, ct).ConfigureAwait(false);
        logger.LogInformation(
            "Session {Id} gcpEnterTCShop_ token={Token} cashCards={Cards}.",
            session.Id, request.Token, cashCards.Count);
        await cashCatalogSender.SendAsync(session, new(request.Token, cashCards), ct).ConfigureAwait(false);
    }

    /// <summary>Handles a purchase request: persist and publish, or answer from the in-plugin wallet.</summary>
    /// <param name="session">The buying session.</param>
    /// <param name="request">The typed purchase request.</param>
    /// <param name="ct">Cancellation token.</param>
    public ValueTask BuyAsync(ISession session, ShopPurchaseRequested request, CancellationToken ct)
    {
        // Persistence active: charge, log, grant, push. Otherwise the row
        // answers from the in-memory wallet and logs that nothing is stored.
        return persistPurchases
            ? purchaser.PurchaseAsync(session, request.Key, ct)
            : ReplyBuyAsync(session, request.Key, ct);
    }

    /// <summary>Handles a card-info request with the catalog row, or the missing-row refusal.</summary>
    /// <param name="session">The asking session.</param>
    /// <param name="request">The typed card-info request.</param>
    /// <param name="ct">Cancellation token.</param>
    public ValueTask CardInfoAsync(ISession session, ShopCardInfoRequested request, CancellationToken ct)
    {
        var card = catalog.Find(request.Key);
        if (card is null)
        {
            logger.LogError("Session {Id} shop card key={Key} not in catalog. gspRepShopItemCardInfo result=-1.", session.Id, request.Key);
            return cardInfoSender.SendAsync(session, new(-1, null), ct);
        }
        logger.LogInformation(
            "Session {Id} shop card key={Key} stockSet={Set} item={Item}. gspRepShopItemCardInfo result=1.",
            session.Id, request.Key, card.StockSetId, card.PrimaryItemId);
        return cardInfoSender.SendAsync(session, new(1, card), ct);
    }

    /// <summary>Handles a coupon submission; the workbook has no coupon sheet, so the answer is the listed error.</summary>
    /// <param name="session">The asking session.</param>
    /// <param name="request">The typed coupon request.</param>
    /// <param name="ct">Cancellation token.</param>
    public ValueTask CouponAsync(ISession session, ShopCouponUseRequested request, CancellationToken ct)
    {
        logger.LogError(
            "Session {Id} gcpReqRealCouponUse unknown0Length={Length}: no coupon table in ShopItem2.xml. Replying gspRepRealCouponUse unknown0=-1.",
            session.Id, request.Code.Length);
        return couponSender.SendAsync(session, new(-1), ct);
    }

    /// <summary>
    /// Handles the coupon dialog's cash probe. With the persistence row a first-seen player is seeded now, so
    /// this is the first place the client learns a real balance; the money snapshot rides along because the
    /// probe's own reply only writes the cash dword.
    /// </summary>
    /// <param name="session">The asking session.</param>
    /// <param name="request">The typed cash-check request.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask CashCheckAsync(ISession session, ShopCashCheckRequested request, CancellationToken ct)
    {
        var user = ShopPlugin.TryGetUserNum(session);
        if (money is not null)
        {
            var stored = await money.FindByUserNumAsync(user, ct).ConfigureAwait(false);
            var wallet = stored ?? ShopPurchaser.SeedWallet(options, user);
            if (stored is null)
                await money.UpsertAsync(wallet, ct).ConfigureAwait(false);
            logger.LogInformation(
                "Session {Id} Unknown834 unknown1Length={Length} cash={Cash} lucci={Lucci} (stored wallet).",
                session.Id, request.Code.Length, wallet.Cash, wallet.Lucci);
            // gspRepCheckCash only writes the cash dword (+0x50); the buy pre-check reads the cached lucci
            // dword (+0x4C), so re-push the full six-field snapshot the login burst uses.
            await cashCheckSender.SendAsync(session, new(wallet.Cash, wallet), ct).ConfigureAwait(false);
            return;
        }

        var cash = wallets.Get(ShopPlugin.TryGetUserNum(session)).Cash;
        logger.LogInformation(
            "Session {Id} Unknown834 unknown1Length={Length} cash={Cash} (in-memory).",
            session.Id, request.Code.Length, cash);
        await cashCheckSender.SendAsync(session, new(cash, null), ct).ConfigureAwait(false);
    }

    private ValueTask ReplyBuyAsync(ISession session, int key, CancellationToken ct)
    {
        var card = catalog.FindPurchase(key);
        if (card is null)
        {
            logger.LogError("Session {Id} shop buy key={Key} not in catalog. gspRepShopBuy result=0.", session.Id, key);
            return buyReplySender.SendAsync(session, new(key, false), ct);
        }

        var user = ShopPlugin.TryGetUserNum(session);
        if (!wallets.TrySpend(user, card))
        {
            logger.LogError(
                "Session {Id} shop buy key={Key} item={Item} refused: wallet cannot pay {Price} {Money}.",
                session.Id, key, card.Stocks[0].ItemId, card.Price, card.MoneyType);
            return buyReplySender.SendAsync(session, new(key, false), ct);
        }

        logger.LogInformation(
            "Session {Id} shop buy key={Key} stockSet={Set} item={Item} name={Name}. gspRepShopBuy result=1. Inventory grant is not sent.",
            session.Id, key, card.StockSetId, card.Stocks[0].ItemId, card.Name);
        return buyReplySender.SendAsync(session, new(key, true), ct);
    }
}

/// <summary>
/// The in-plugin wallet of a catalog-schema row without the persistence row: the configured seed per player and
/// an atomic spend against it. A deployment with the persistence row never consults it.
/// </summary>
/// <param name="startingLucci">The configured Lucci seed of a first-seen player.</param>
/// <param name="startingCash">The configured cash seed of a first-seen player.</param>
internal sealed class WalletStore(int startingLucci, int startingCash)
{
    private readonly ConcurrentDictionary<long, (int Lucci, int Cash)> _byUser = new();

    /// <summary>Reads one player's balance, seeding it on first sight.</summary>
    /// <param name="userNum">The player.</param>
    /// <returns>The Lucci and cash balances.</returns>
    public (int Lucci, int Cash) Get(long userNum) =>
        _byUser.GetOrAdd(userNum, _ => (startingLucci, startingCash));

    /// <summary>Charges one card's price when the balance covers it.</summary>
    /// <param name="userNum">The buying player.</param>
    /// <param name="card">The selected offer.</param>
    /// <returns>Whether the price was charged.</returns>
    public bool TrySpend(long userNum, ShopPurchaseOffer card)
    {
        var cashPriced = string.Equals(card.MoneyType, "cash", StringComparison.OrdinalIgnoreCase);
        var price = card.Price;
        while (true)
        {
            var current = Get(userNum);
            if (cashPriced)
            {
                if (current.Cash < price)
                    return false;
                if (_byUser.TryUpdate(userNum, (current.Lucci, current.Cash - price), current))
                    return true;
            }
            else
            {
                if (current.Lucci < price)
                    return false;
                if (_byUser.TryUpdate(userNum, (current.Lucci - price, current.Cash), current))
                    return true;
            }
        }
    }
}
