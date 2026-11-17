using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.MysteryStore;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Purchasing;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Shop;

/// <summary>
/// Serves the Mysterystore: six cards drawn from the configured pool, a countdown to the next free rotation, a paid
/// refresh and card purchases paid in the configured owned item. Every change commits through <see cref="IMysteryStore"/>
/// in one account transaction; the committed inventory is published before the reply that shows the receipt.
/// </summary>
internal sealed class MysteryStoreFlow(ShopMysteryStoreOptions options, InventoryLoadoutScope scope,
    IReadOnlyList<MysteryStoreOffer> pool, string catalogRevision, IMysteryStore store,
    IMessageSender<MysteryStoreReplied> replies, IMessageSender<InventoryReplaced> inventories, TimeProvider time,
    Func<int, int> choose, ILogger logger, IEquipmentArticleResolver? articles = null, IPetEntitlements? pets = null)
{
    private const int Cards = 6;

    /// <summary>Gets the fingerprint of the pool, prices and grant terms; a changed one replaces stored selections.</summary>
    internal string CatalogRevision => catalogRevision;

    /// <summary>Resolves the configured keys into card offers, failing the mount for anything the store cannot grant.</summary>
    internal static IReadOnlyList<MysteryStoreOffer> ResolvePool(ShopMysteryStoreOptions options, IReadOnlyList<int> keys,
        ShopCatalog catalog, Func<ShopStockRow, PurchaseGrantTerm> resolve, InventoryLoadoutScope scope, ILogger logger)
    {
        if (!scope.Supports(options.CoinItemId))
            throw new ConfigException($"shop.mystery_store.coin_item_id {options.CoinItemId} is not an item of this build.");
        var offers = new List<MysteryStoreOffer>(keys.Count);
        foreach (var key in keys)
        {
            if (!catalog.Gifts.TryGetValue(key, out var gift))
                throw new ConfigException($"shop.mystery_store.offers key {key} is not a Stock or StockSet that grants items.");
            if (gift.Lucci != 0)
            {
                logger.LogWarning("Mysterystore offer {Key} grants Lucci and is excluded because its wallet effect is not supported.", key);
                continue;
            }
            if (gift.Stocks.Count == 0)
                throw new ConfigException($"shop.mystery_store.offers key {key} has no inventory grants.");
            InventoryGrantTerm[] grants;
            try
            {
                grants = gift.Stocks.Select(resolve).Select(term => new InventoryGrantTerm(term.ItemId, term.Kind switch
                {
                    PurchaseGrantKind.Count => InventoryGrantKind.Count,
                    PurchaseGrantKind.Days => InventoryGrantKind.Days,
                    PurchaseGrantKind.Permanent => InventoryGrantKind.Permanent,
                    PurchaseGrantKind.Life => InventoryGrantKind.Life,
                    _ => throw new PurchaseRejectedException($"Grant kind {term.Kind} is unsupported."),
                }, term.Amount, term.Maximum, term.RejectOwned)).ToArray();
            }
            catch (PurchaseRejectedException error)
            {
                throw new ConfigException($"shop.mystery_store.offers key {key} has an item without purchase terms: {error.Message}");
            }
            if (grants.Any(term => !scope.Supports(term.ItemId)))
                throw new ConfigException($"shop.mystery_store.offers key {key} grants an item outside this build's catalog.");
            // The row word and text have no audited consumer beyond display metadata; zero and empty are neutral.
            offers.Add(new(key, options.OfferCoinPrice, 0, "", grants));
        }
        if (offers.Count < Cards)
            throw new ConfigException("shop.mystery_store.offers has fewer than six supported inventory-only offers.");
        return offers;
    }

    /// <summary>Fingerprints everything a stored board depends on, so a changed pool or price rotates selections free.</summary>
    internal static string Fingerprint(ShopMysteryStoreOptions options, IReadOnlyList<MysteryStoreOffer> pool)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{options.CoinItemId}|{options.RefreshCoinCost}|{options.GroupStockVariants}");
        foreach (var offer in pool)
        {
            text.Append(CultureInfo.InvariantCulture, $";{offer.NativeKey}:{offer.CoinPrice}");
            foreach (var term in offer.Grants)
                text.Append(CultureInfo.InvariantCulture, $",{term.ItemId}/{term.Kind}/{term.Amount}/{term.Maximum}/{term.RejectOwned}");
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>Answers one request of the logged-in player.</summary>
    internal async ValueTask HandleAsync(ISession session, MysteryStoreRequested request, CancellationToken ct)
    {
        var origin = session.Feature<IPlayerSession>();
        var user = origin.Identity.UserNum;
        switch (request.Action)
        {
            case MysteryStoreAction.Open:
            case MysteryStoreAction.Query:
            {
                var ensured = await store.EnsureAsync(new(user, scope, Candidate(), options.RotationSeconds), ct).ConfigureAwait(false);
                if (!Current(session, origin)) return;
                if (ensured.Outcome == MysteryStoreOutcome.TokenExhausted || ensured.Selection is null)
                {
                    await ReplyAsync(session, request.Action, MysteryStoreStatus.Unavailable, null, 0, ct).ConfigureAwait(false);
                    return;
                }
                // An unchanged periodic query keeps the client's state: restating the countdown would restart the
                // client's query timer (kr-1256 sub_E5F6B0 resets it on every countdown section).
                if (request.Action == MysteryStoreAction.Query && ensured.Outcome == MysteryStoreOutcome.Unchanged
                    && request.Token == ensured.Selection.Token)
                {
                    await replies.SendAsync(session, new(request.Action, MysteryStoreStatus.Done, null, null, null, null, 0), ct).ConfigureAwait(false);
                    return;
                }
                var rotated = request.Action == MysteryStoreAction.Query && request.Token != 0
                    && ensured.Outcome == MysteryStoreOutcome.SelectionCreated;
                await ReplyAsync(session, request.Action, rotated ? MysteryStoreStatus.Rotated : MysteryStoreStatus.Done,
                    ensured.Selection, 0, ct).ConfigureAwait(false);
                return;
            }
            case MysteryStoreAction.Refresh:
            {
                var result = await store.RefreshAsync(new(Inventory(user), request.Token, request.QuotedCoins, Candidate(),
                    options.RotationSeconds), ct).ConfigureAwait(false);
                await CompleteAsync(session, request, origin, result, ct).ConfigureAwait(false);
                return;
            }
            case MysteryStoreAction.Purchase:
            {
                var result = await store.PurchaseAsync(new(Inventory(user), catalogRevision, request.Token,
                    request.NativeKey, request.QuotedCoins), ct).ConfigureAwait(false);
                await CompleteAsync(session, request, origin, result, ct).ConfigureAwait(false);
                return;
            }
            default:
                throw new InvalidOperationException($"Mysterystore action {request.Action} is unknown.");
        }
    }

    private async ValueTask CompleteAsync(ISession session, MysteryStoreRequested request, IPlayerSession origin, MysteryStoreResult result,
        CancellationToken ct)
    {
        if (!Current(session, origin)) return;
        var user = origin.Identity.UserNum;
        if (result.Outcome is MysteryStoreOutcome.Refreshed or MysteryStoreOutcome.Purchased)
        {
            // The coins and the rewards are committed; install them before the receipt dialog reads the bag.
            var inventory = result.Inventory!;
            await inventories.SendAsync(session, new(inventory.CharacterBag, inventory.EquippedBag) { IsCompleteSnapshot = true }, ct)
                .ConfigureAwait(false);
            if (!Current(session, origin)) return;
            logger.LogInformation("Mysterystore {Outcome} for player {Player}: key {Key}, token {Token}.",
                result.Outcome, user, result.ReceiptNativeKey, result.Selection!.Token);
            await ReplyAsync(session, request.Action, MysteryStoreStatus.Done, result.Selection,
                result.Outcome == MysteryStoreOutcome.Purchased ? result.ReceiptNativeKey : 0, ct).ConfigureAwait(false);
            return;
        }
        if (result.Outcome == MysteryStoreOutcome.AlreadyPurchased)
        {
            var inventory = result.Inventory!;
            await inventories.SendAsync(session, new(inventory.CharacterBag, inventory.EquippedBag) { IsCompleteSnapshot = true }, ct)
                .ConfigureAwait(false);
            if (!Current(session, origin)) return;
        }
        var status = Status(result, request.NativeKey);
        logger.LogInformation("Mysterystore {Action} refused for player {Player}: {Outcome}.", request.Action, user, result.Outcome);
        // The client applies the reply's sections before its status dialog (kr-1256 sub_E5F6B0), so a refusal restates
        // the current selection, installing a fresh one first when the old one is gone or out of date.
        var selection = result.Selection;
        if (result.Outcome is MysteryStoreOutcome.MissingSelection or MysteryStoreOutcome.Expired or MysteryStoreOutcome.CatalogChanged)
            selection = (await store.EnsureAsync(new(user, scope, Candidate(), options.RotationSeconds), ct).ConfigureAwait(false)).Selection;
        if (!Current(session, origin)) return;
        await ReplyAsync(session, request.Action, status, selection, 0, ct).ConfigureAwait(false);
    }

    private static bool Current(ISession session, IPlayerSession origin) =>
        session.TryFeature<IPlayerSession>(out var current) && ReferenceEquals(origin, current);

    private MysteryStoreStatus Status(MysteryStoreResult result, int key) => result.Outcome switch
    {
        MysteryStoreOutcome.MissingSelection or MysteryStoreOutcome.StaleToken or MysteryStoreOutcome.Expired
            or MysteryStoreOutcome.CatalogChanged => MysteryStoreStatus.ListChanged,
        MysteryStoreOutcome.UnknownOffer => MysteryStoreStatus.InvalidProduct,
        MysteryStoreOutcome.QuoteMismatch => MysteryStoreStatus.InvalidRequest,
        MysteryStoreOutcome.TokenExhausted => MysteryStoreStatus.Unavailable,
        MysteryStoreOutcome.InsufficientCoins => MysteryStoreStatus.InsufficientCoins,
        MysteryStoreOutcome.AlreadyPurchased => MysteryStoreStatus.AlreadyPurchased,
        MysteryStoreOutcome.GrantRefused => result.GrantRefusal switch
        {
            ShopPurchaseRefusal.AlreadyOwned => MysteryStoreStatus.AlreadyOwned,
            ShopPurchaseRefusal.NoMoreAllowed => MysteryStoreStatus.LifeLimit,
            ShopPurchaseRefusal.LimitReached => GrantsDays(result.Selection, key) ? MysteryStoreStatus.DurationLimit : MysteryStoreStatus.QuantityLimit,
            _ => MysteryStoreStatus.InvalidProduct,
        },
        _ => throw new InvalidOperationException($"Mysterystore outcome {result.Outcome} is not a refusal."),
    };

    private static bool GrantsDays(MysteryStoreSelection? selection, int key) =>
        selection?.Board.Offers.FirstOrDefault(offer => offer.NativeKey == key)?.Grants
            .Any(term => term.Kind == InventoryGrantKind.Days) == true;

    private ValueTask ReplyAsync(ISession session, MysteryStoreAction action, MysteryStoreStatus status,
        MysteryStoreSelection? selection, int receipt, CancellationToken ct)
    {
        if (selection is null)
            return replies.SendAsync(session, new(action, status, null, null, null, null, receipt), ct);
        var board = selection.Board;
        var remaining = Math.Clamp((selection.ExpiresAtUnixMilliseconds - time.GetUtcNow().ToUnixTimeMilliseconds() + 999) / 1000,
            0, ushort.MaxValue);
        // Cards and bought bits travel together: the client rebuilds its bought map from this reply's cards.
        return replies.SendAsync(session, new(action, status,
            new(selection.Token, board.CoinItemId, board.RefreshCoinCost, board.GroupStockVariants), (ushort)remaining,
            board.Offers.Select(offer => new MysteryStoreReplyOffer(offer.NativeKey, offer.CoinPrice, offer.Unknown4, offer.UnknownName)).ToArray(),
            selection.PurchasedMask, receipt), ct);
    }

    private InventoryMetadataPreparationCommand Inventory(long user)
    {
        var now = time.GetUtcNow();
        return new(user, scope)
        {
            ArticleInitialization = articles?.CreateInitialization(user, now),
            PetInitialization = pets?.CreateInitialization(user, now),
        };
    }

    // Draws six distinct cards; the order is the bought-bit order.
    private MysteryStoreBoard Candidate()
    {
        var indexes = Enumerable.Range(0, pool.Count).ToArray();
        for (var index = 0; index < Cards; index++)
        {
            var pick = index + choose(indexes.Length - index);
            (indexes[index], indexes[pick]) = (indexes[pick], indexes[index]);
        }
        return new(catalogRevision, options.CoinItemId, options.RefreshCoinCost, options.GroupStockVariants,
            indexes.Take(Cards).Select(index => pool[index]).ToArray());
    }
}
