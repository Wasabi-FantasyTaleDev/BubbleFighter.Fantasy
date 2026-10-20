using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Dialects;
using BubbleFighter.Fantasy.Shop.Wire;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Shop.Purchasing;

/// <summary>
/// Resolves <c>gcpReqShopBuy</c> Stock/StockSet requests, commits the purchase through the
/// persistence interface, then publishes inventory, money and the modal reply.
/// kr-1256 <c>sub_E1D720</c> writes the request key; kr-1256 <c>sub_9B6A19</c> passes its echo to
/// kr-1256 <c>sub_E4D6A0</c>.
/// </summary>
public sealed class ShopPurchaser(
    ShopOptions options,
    ShopCatalog catalog,
    IShopPurchaseStore? purchases,
    IInventoryPushService? push,
    ILogger logger,
    Data.EquipHoldStore? hold = null)
{
    // A dialect supplies catalog term interpretation; the shared purchase path retains pricing and audit inputs.
    internal ItemPurchasePolicy? ItemPurchasePolicy { get; init; }

    // The reply numbers, resolved by name from the build's opcode table; PurchaseAsync needs them.
    internal ShopPacketNumbers? Packets { get; init; }

    /// <summary>
    /// Second <c>i32</c> of every 802/803 bag slot; kept
    /// verbatim for granted rows.
    /// </summary>
    public const int ReferenceField2 = 99999;

    /// <summary>Base of the granted-slot key range, above the 802/803 markers.</summary>
    public const long PurchaseKeyBase = unchecked((long)0x40F86B0000000000);

    /// <summary>
    /// <c>unknown12</c> value of a container-2 record that means equip / upsert.
    /// Every other value erases the key instead (<c>sub_9AEB10</c>,
    /// <c>sub_5F65B0</c>, <c>sub_5F6C30</c>).
    /// </summary>
    public const int EquipRecordState = 1;

    /// <summary>
    /// Base of the held container-2 identity range. Deliberately the same base as
    /// <see cref="PurchaseKeyBase"/>: one purchase produces one granted bag slot
    /// and one equip record, and both carry the same key, so the two can never
    /// disagree about which item was bought. The base also keeps the value above
    /// the 802/803 marker range.
    /// </summary>
    public const long EquipHoldKeyBase = PurchaseKeyBase;

    /// <summary>
    /// Builds the granted instance key for one purchase. The player id occupies
    /// the high bits and the bag ordinal the low 16, so keys are unique per
    /// player and bag and never collide with the seeded marker rows.
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="ordinal">Zero-based position in the character bag.</param>
    public static long PurchaseSlotKey(long userNum, int ordinal) =>
        unchecked(PurchaseKeyBase + (userNum << 16) + ordinal);

    /// <summary>
    /// Builds the container-2 <c>unknown10</c> identity of the held equip record
    /// for one purchase, from the same player id and bag ordinal as
    /// <see cref="PurchaseSlotKey"/>. Deterministic, so a repeated purchase of
    /// the same key by the same player yields a different identity (the bag
    /// ordinal advances) and two records never collide.
    /// </summary>
    /// <param name="userNum">Login <c>players.user_num</c>.</param>
    /// <param name="ordinal">Zero-based position in the character bag.</param>
    /// <remarks>
    /// The encoding is <c>unverified</c>: the client code proves only that
    /// <c>unknown10</c> is the <c>i64</c> key of the article map at
    /// <c>dword_18E55D4+32</c> and of the equipped-slot list at
    /// <c>fighter+1940</c> (<c>sub_9AEB10</c>, <c>sub_5F65B0</c>), and that it
    /// must not already be present in either. The <c>100000000</c> split in
    /// <c>sub_8063D0</c> / <c>sub_806CE0</c> hints at an article-serial family;
    /// no client site derives the value. The marker-range base is a documented
    /// choice, not a recovered one.
    /// </remarks>
    public static long EquipHoldKey(long userNum, int ordinal) =>
        unchecked(EquipHoldKeyBase + (userNum << 16) + ordinal);

    /// <summary>Commits the selected offer and sends the ordered purchase response.</summary>
    /// <param name="session">The buying session.</param>
    /// <param name="key">Requested Stock or StockSet identity.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask PurchaseAsync(ISession session, int key, CancellationToken ct)
    {
        if (purchases is null || push is null)
            throw new InvalidOperationException("Purchases require IShopPurchaseStore and IInventoryPushService.");
        var packets = Packets ?? throw new InvalidOperationException("Purchases require the build's shop packet numbers.");

        var card = catalog.FindPurchase(key);
        if (card is null)
        {
            logger.LogError("Session {Id} shop buy key={Key} not in catalog. gspRepShopBuy result=0.", session.Id, key);
            await session.SendAsync(packets.ShopBuyReply, ShopWire.RepShopBuy(key, 0, string.Empty), ct)
                .ConfigureAwait(false);
            return;
        }

        var user = ShopPlugin.TryGetUserNum(session);
        if (user <= 0)
        {
            logger.LogError(
                "Session {Id} shop buy key={Key} has no lobby identity (players row missing). gspRepShopBuy result=0; nothing persisted.",
                session.Id, key);
            await session.SendAsync(packets.ShopBuyReply, ShopWire.RepShopBuy(key, 0, card.Name), ct)
                .ConfigureAwait(false);
            return;
        }

        ShopPurchaseResult? result;
        try
        {
            result = await CommitOfferAsync(session, user, card, ct).ConfigureAwait(false);
        }
        catch (PurchaseRejectedException ex)
        {
            // The item terms refuse it before anything is charged (owned permanent item, a limit passed); the client
            // gets the failed reply, as the legacy purchase handler sends.
            logger.LogWarning("Session {Id} shop buy key={Key} refused for player {User}: {Reason}", session.Id, key, user, ex.Message);
            result = null;
        }
        if (result is null)
        {
            await session.SendAsync(packets.ShopBuyReply, ShopWire.RepShopBuy(key, 0, string.Empty), ct)
                .ConfigureAwait(false);
            return;
        }

        // Inventory and money are published before the modal reply.
        // kr-1256 sub_9B6A19 passes the request key to sub_E4D6A0; an equipment id cannot resolve its Stock.
        await push.PushPurchaseAsync(session, user, result.Grants, ct).ConfigureAwait(false);
        await session.SendAsync(
            packets.UserMoneyStat, ShopWire.RepUserMoneyStat(result.Wallet), ct)
            .ConfigureAwait(false);
        await session.SendAsync(
            packets.ShopBuyReply, ShopWire.RepShopBuy(key, 1, string.Empty), ct)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Session {Id} shop buy key={Key} stockSet={Set} item={Item} price={Price} {Money}: charged, logged (slot {Slot}), gspRepShopBuy result=1, wallet + inventory pushed.",
            session.Id, key, card.StockSetId, card.Stocks[0].ItemId, card.Price,
            NormalizeCurrency(card.MoneyType, key), result.Grants[0].SlotKey);
    }

    // The committed result is wire-neutral; each dialect must publish its own inventory,
    // wallet and reply packets after this operation succeeds.
    internal async ValueTask<ShopPurchaseResult?> CommitOfferAsync(
        ISession session, long user, ShopPurchaseOffer card, CancellationToken ct)
    {
        if (purchases is null)
            throw new InvalidOperationException("Purchases require IShopPurchaseStore.");

        var key = card.Key;
        var itemId = card.Stocks[0].ItemId;
        var price = card.Price;
        var currency = NormalizeCurrency(card.MoneyType, key);
        if (price < 0)
            throw new InvalidOperationException($"Purchase {key} has invalid currency or price.");
        if (options.HoldEquipUntilInventoryOpen && hold is null)
            throw new InvalidOperationException("Equip hold is enabled without a hold store.");
        if (options.HoldEquipUntilInventoryOpen && !hold!.CanHold(user))
        {
            logger.LogWarning(
                "Session {Id} shop buy key={Key} refused before charging: the equip hold queue for player {User} is full.",
                session.Id, key, user);
            return null;
        }

        var command = new ShopPurchaseCommand(
            SeedWallet(options, user), key, card.StockSetId, currency, price,
            PurchaseSlotKey(user, 0), card.Stocks.Select(s => new ShopPurchaseGrant(s.ItemId, ReferenceField2)).ToArray());
        var result = ItemPurchasePolicy is { } policy
            ? await policy.CommitAsync(command, card.Stocks, ct).ConfigureAwait(false)
            : await purchases.PurchaseAsync(command, ct).ConfigureAwait(false);
        if (result is null)
        {
            logger.LogWarning("Session {Id} shop buy key={Key} refused: insufficient {Money}.",
                session.Id, key, currency);
            return null;
        }
        var granted = result.Grants[0];

        // Gated: with the option off the hold store is never touched and the
        // frames below are byte-identical to the pre-hold build. With it on the
        // equip record is queued here, right after the grant it belongs to, and
        // travels later on the client's own C2S 803.
        if (options.HoldEquipUntilInventoryOpen)
        {
            if (hold is null)
            {
                throw new InvalidOperationException(
                    "hold_equip_until_inventory_open is on but no Data.EquipHoldStore was supplied to ShopPurchaser.");
            }

            // Same ordinal as the granted bag slot: one purchase, one identity. The purchase has
            // committed, so a queue that filled since the pre-charge check is logged, not thrown.
            var held = new EquippedSlotRecord(user, granted.SlotKey, itemId, EquipRecordState, false);
            if (!hold.TryRemember(user, held))
                logger.LogError(
                    "Session {Id} shop buy key={Key} item={Item}: committed, but the equip hold queue for player {User} filled concurrently; the item is in the bag without a held equip record.",
                    session.Id, key, itemId, user);
            else
                logger.LogInformation(
                "Session {Id} shop buy key={Key} item={Item}: held one S2C 803 container-2 equip record (identity {Identity}, state {State}) for player {User} until the client opens the inventory dialog.",
                session.Id, key, itemId, held.Unknown10, held.Unknown12, user);
        }

        return result;
    }

    internal static string NormalizeCurrency(string moneyType, int key) => moneyType switch
    {
        "cashCN" => "cash",
        "cash" or "lucci" => moneyType,
        _ => throw new InvalidOperationException($"Purchase {key} has an invalid currency."),
    };

    /// <summary>Builds the configured first-purchase wallet; existing stored balances take precedence.</summary>
    /// <param name="options">Starting Lucci and Cash balances.</param>
    /// <param name="user">The purchasing player.</param>
    public static PlayerWallet SeedWallet(ShopOptions options, long user) =>
        new(user, options.StartingLucci, options.StartingCash);
}
