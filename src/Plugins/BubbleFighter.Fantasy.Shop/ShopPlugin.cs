using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.Contracts.Wallet;
using System.Collections.Concurrent;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Shop.Data;
using BubbleFighter.Fantasy.Shop.Handlers;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Shop;

/// <summary>
/// Cash shop and item shop open / list / buy path. The profile row supplies the
/// protocol name and the client path of <c>Config/ShopItem2.xml</c>, which is read
/// from the build's client data through <c>IClientDataStore</c>; the dialect that
/// encodes the packets is the build's lineage.
/// Enter-shop replies carry the on-sale catalog from that file. Buy and card-info
/// look up real rows. With the persistence row active a purchase is charged to the
/// stored wallet, logged, granted into the stored bag and pushed back as the
/// bag family through the lobby's <see cref="IInventoryPushService"/>. Every packet number comes from the
/// build's opcode table by name. Shop opens require IShopSessionStore. When purchase
/// persistence is disabled, wallet replies remain in process memory. With
/// <c>hold_equip_until_inventory_open</c> on, a purchase additionally queues one
/// <c>gspInventoryUpdateInventorySlots</c> container-2 equip record and the row registers
/// <c>gcpEnterInventory</c> so the held record is flushed once the client
/// opens its inventory dialog; with the option off, which is the default, <c>gcpEnterInventory</c>
/// is not registered at all and no wire behaviour changes.
/// </summary>
[Plugin("shop",
    Description = "Item shop and cash shop catalog from Config/ShopItem2.xml with persisted purchases. Dialect selected by the build's lineage.",
    Requires = [typeof(IMessageRouter), typeof(IShopSessionStore)],
    // An inventory-only row reads no workbook; a row that does fails at activation without the clientdata service.
    Optional = [typeof(BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore), typeof(IShopPurchaseStore), typeof(IPlayerWalletRepository), typeof(IInventoryPushService), typeof(IInventoryRepository), typeof(IEquippedViewPush), typeof(IInventoryExpiry), typeof(IPresentStore), typeof(ISessionRegistry), typeof(IPlayerProgressRepository)], OptionalMany = [typeof(IInboundMessageBinding<PresentSendRequested>), typeof(IInboundMessageBinding<PresentListRequested>), typeof(IInboundMessageBinding<PresentActionRequested>), typeof(IInboundMessageBinding<PresentNoticeRequested>), typeof(IMessageSender<PresentSendReplied>), typeof(IMessageSender<PresentListReplied>), typeof(IMessageSender<PresentActionReplied>), typeof(IMessageSender<PresentNoticeSent>), typeof(IMessageSender<ShopStateUpdated>), typeof(IMessageSender<ShopBalanceUpdated>), typeof(IInboundMessageBinding<ShopBalanceRequested>), typeof(IMessageSender<ShopPurchaseReplied>), typeof(IInboundMessageBinding<ShopPurchaseRequested>), typeof(IInboundMessageBinding<ShopInventoryRequested>), typeof(IInboundMessageBinding<ShopCatalogRequested>), typeof(IInboundMessageBinding<ShopCashCatalogRequested>), typeof(IInboundMessageBinding<ShopCardInfoRequested>), typeof(IInboundMessageBinding<ShopCouponUseRequested>), typeof(IInboundMessageBinding<ShopCashCheckRequested>), typeof(IMessageSender<ShopCatalogPublished>), typeof(IMessageSender<ShopCashCatalogPublished>), typeof(IMessageSender<ShopCardInfoReplied>), typeof(IMessageSender<ShopCouponUseReplied>), typeof(IMessageSender<ShopCashCheckReplied>), typeof(IMessageSender<EquippedArticlesFlushed>), typeof(IMessageSender<InventoryReplaced>), typeof(IMessageSender<ItemsExpired>)],
    ConditionalProvides = [typeof(IShopGiftService)])]
public sealed class ShopPlugin : IPlugin
{
    // The workbook always comes from the build's client data by client path; a row without the clientdata
    // service fails at activation instead of reading another build's files.
    private static WorkbookFile Workbook(IPluginContext context, ShopOptions options)
    {
        if (!context.Services.TryGet<BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore>(out var data) || data is null)
            throw new ConfigException("shop_item_client_path requires a clientdata row for this build.");
        return WorkbookFile.FromBytes(options.ShopItemClientPath, data.Read(options.ShopItemClientPath));
    }

    private static IInventoryExpiry? OptionalExpiry(IPluginContext context) =>
        context.Services.TryGet<IInventoryExpiry>(out var expiry) ? expiry : null;

    private static IMessageSender<ItemsExpired>? OptionalNotice(IPluginContext context, string protocol) =>
        context.Services.GetAll<IMessageSender<ItemsExpired>>().SingleOrDefault(sender => sender.Protocol == protocol);

    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<ShopOptions>();
        // A build without the gift box (the modern family) cannot deliver level gifts; a shared table is skipped, not an error.
        if (options.LevelRewards.Length > 0 && (options.InventoryOnly || !options.PresentBox))
            context.Logger.LogWarning("shop.level_rewards is set but the gift box is off on '{Protocol}'; level rewards are skipped.", options.Protocol);
        if (options.InventoryOnly)
        {
            var inventory = context.Services.Get<IInventoryRepository>();
            var replaced = RequireShopSender<InventoryReplaced>(context, options.Protocol);
            if (!context.Services.GetAll<IInboundMessageBinding<ShopInventoryRequested>>()
                .Any(binding => binding.Protocol == options.Protocol))
                throw new ConfigException($"Inventory-only shop requires an inventory entry binding for '{options.Protocol}'.");
            context.Services.TryGet<IEquippedViewPush>(out var view);
            var entry = new InventoryEntryHandler(inventory, replaced, view, context.Logger,
                OptionalExpiry(context), OptionalNotice(context, options.Protocol));
            context.Effect(context.Services.Get<IMessageRouter>().For(context)
                .On<ShopInventoryRequested>(options.Protocol, entry.HandleAsync));
            return ValueTask.CompletedTask;
        }
        var schema = options.CatalogSchema switch
        {
            "kr1256" => ShopCatalogSchema.Kr1256,
            "legacy" => ShopCatalogSchema.Legacy,
            _ => throw new ConfigException($"catalog_schema '{options.CatalogSchema}' is not a known workbook schema."),
        };
        var workbook = Workbook(context, options);
        var catalog = ShopCatalogLoader.Load(workbook, options.MaxShopItemBytes, schema,
            string.IsNullOrEmpty(options.CatalogDisplayColumn) ? null : options.CatalogDisplayColumn,
            message => context.Logger.LogWarning("Shop catalog: {Diagnostic}", message), options.SupplementalOffers, options.ClientProfile);
        var itemCards = catalog.Cards;
        var cashCards = catalog.CashShopCards.ToList();
        if (itemCards.Count == 0)
            throw new InvalidOperationException($"{catalog.SourcePath} has no on-sale cards.");
        // The 1256 dialog sends the TC-shop catalog on open, so an empty cash list
        // leaves it dead; the legacy builds price everything in the workbook's own
        // money types and the ID2014 workbook has no cash column values at all.
        var tableCatalog = schema == ShopCatalogSchema.Kr1256
            || PacketTables.Load(options.PacketsDirectory).Resolve(options.ClientProfile, "EnterShopReply").Count > 0;
        if (cashCards.Count == 0 && schema == ShopCatalogSchema.Kr1256)
            throw new InvalidOperationException($"{catalog.SourcePath} has no on-sale cash cards.");

        // Legacy opcodes beyond catalog entry have different meanings and layouts.
        // In particular the legacy builds have no cash shop entry; kr-812 gcpEnterInventory (kr-812 sub_6C3D20)
        // opens the inventory dialog.
        // Workbook columns and native dialog layouts are independent: a short-frame modern client can
        // retain the older workbook schema while consuming the same typed catalog messages.
        if (!tableCatalog)
        {
            // The capability checks below read the build's own statements in packets/builds.yaml.
            var tables = PacketTables.Load(options.PacketsDirectory);
            if (options.HoldEquipUntilInventoryOpen)
                throw new ConfigException("The selected shop adapter does not support held equipment records.");
            var legacyOpens = new ShopOpenScope(options.MaxOpenSessions);
            context.Effect(legacyOpens);
            context.Effect(context.Events.On<ConnectionClosedEvent>(e => legacyOpens.Remove(e.Connection.Id)));
            // Inventory-dialog entry restates the bag and publishes the view through the
            // build's typed senders; a build without the inventory codec keeps the old
            // shop-state answer inside the purchase handler.
            InventoryEntryHandler? enterInventory = null;
            var replacedSenders = context.Services.GetAll<IMessageSender<InventoryReplaced>>()
                .Where(sender => sender.Protocol == options.Protocol).ToArray();
            if (replacedSenders.Length > 1)
                throw new ConfigException($"Shop requires at most one IMessageSender<InventoryReplaced> for {options.Protocol}.");
            if (replacedSenders.Length == 1
                && context.Services.TryGet<IInventoryRepository>(out var entryInventory) && entryInventory is not null)
            {
                context.Services.TryGet<IEquippedViewPush>(out var viewPush);
                enterInventory = new InventoryEntryHandler(entryInventory, replacedSenders[0], viewPush, context.Logger,
                    OptionalExpiry(context), OptionalNotice(context, options.Protocol));
            }
            // Typed routes need the message router; a catalog-only row has none.
            var legacyRoutes = (options.GrantPurchasedItems || enterInventory is not null || options.SendShopCatalog)
                ? context.Services.Get<IMessageRouter>().For(context)
                : null;
            Purchasing.CatalogPurchaseHandler? buy = null;
            if (options.GrantPurchasedItems)
            {
                if (options.LifeItems && !tables.CapabilityOf(options.ClientProfile, "life_items"))
                    throw new ConfigException($"life_items requires a build with the life_items capability; '{options.ClientProfile}' has none.");
                var termRules = new ItemTermRules(options.PermanentDayStockTypes, options.RejectRepeatFunction10001, options.LifeItems);
                var terms = ItemTermsLoader.Load(workbook, options.MaxShopItemBytes);
                var purchaseStoreLegacy = context.Services.Get<IShopPurchaseStore>();
                var inventoryLegacy = context.Services.Get<IInventoryRepository>();
                var ready = new ShopOpenScope(options.MaxOpenSessions);
                context.Effect(ready);
                context.Effect(context.Events.On<ConnectionClosedEvent>(e => ready.Remove(e.Connection.Id)));
                Func<ShopStockRow, Purchasing.PurchaseGrantTerm> resolveTerms = stock => terms.TryGetValue(stock.ItemId, out var term)
                    ? term.ForStock(stock, termRules) : throw new Purchasing.PurchaseRejectedException($"Item {stock.ItemId} has no purchase terms.");
                var conditional = new Purchasing.ConditionalShopPurchaser(purchaseStoreLegacy, inventoryLegacy,
                    TimeProvider.System, options.MaxPurchaseAttempts);
                var purchaserLegacy = new Purchasing.ShopPurchaser(options, catalog, purchaseStoreLegacy, null, context.Logger)
                {
                    ItemPurchasePolicy = new(conditional, resolveTerms),
                };
                var stateSender = RequireShopSender<ShopStateUpdated>(context, options.Protocol);
                buy = new(options, catalog, purchaserLegacy, inventoryLegacy, context.Services.Get<IPlayerWalletRepository>(),
                    stateSender,
                    RequireShopSender<ShopBalanceUpdated>(context, options.Protocol),
                    RequireShopSender<ShopPurchaseReplied>(context, options.Protocol), ready, enterInventory, context.Logger);
                // Rewards named by a workbook key (the tutorial first-clear gift) are granted under the same terms.
                context.Effect(context.Services.Register<IShopGiftService>(new Purchasing.ShopGiftService(options, catalog,
                    conditional, purchaseStoreLegacy, resolveTerms, stateSender, context.Logger)));
                context.Effect(legacyRoutes!.On<ShopPurchaseRequested>(options.Protocol, buy.BuyAsync));
                context.Effect(legacyRoutes.On<ShopInventoryRequested>(options.Protocol, buy.InventoryAsync));
                if (context.Services.GetAll<IInboundMessageBinding<ShopBalanceRequested>>()
                    .Any(binding => binding.Protocol == options.Protocol))
                    context.Effect(legacyRoutes.On<ShopBalanceRequested>(options.Protocol, buy.BalanceAsync));
                if (options.PresentBox)
                    MountPresentBox(context, options, tables, catalog, conditional, resolveTerms, inventoryLegacy, ready, stateSender, legacyRoutes!);
            }
            else if (options.PresentBox)
            {
                throw new ConfigException("present_box requires grant_purchased_items: a receiver takes a gift through the purchase path.");
            }
            else if (enterInventory is not null)
            {
                context.Effect(legacyRoutes!.On<ShopInventoryRequested>(options.Protocol, enterInventory.HandleAsync));
            }
            if (options.SendShopCatalog)
            {
                // The dialect row parses C2S 801 and encodes S2C 800 from the build's shop.catalog values;
                // this handler only selects the eligible cards and remembers the open. The bag restatement a
                // purchase-gated row sends before the catalog keeps its place ahead of the reply.
                var legacyCatalogSender = RequireShopSender<ShopCatalogPublished>(context, options.Protocol);
                var legacyCatalog = new ShopCatalogHandler(catalog, options.MaxEnterShopFrameBytes,
                    options.MaxEnterShopCards, legacyOpens, context.Services.Get<IShopSessionStore>(),
                    legacyCatalogSender, context.Logger, options.PreferLongestDuration, options);
                context.Effect(legacyRoutes!.On<ShopCatalogRequested>(options.Protocol, async (session, request, ct) =>
                {
                    if (buy is not null)
                        await buy.OpenAsync(session, ct).ConfigureAwait(false);
                    await legacyCatalog.HandleAsync(session, request, ct).ConfigureAwait(false);
                }));
            }
            return ValueTask.CompletedTask;
        }

        var store = context.Services.Get<IShopSessionStore>();
        var purchases = context.Services.TryGet<IShopPurchaseStore>(out var purchaseStore) ? purchaseStore : null;
        var wallets = new Handlers.WalletStore(options.StartingLucci, options.StartingCash);

        IPlayerWalletRepository? money = context.Services.TryGet<IPlayerWalletRepository>(out var moneyRepo) && moneyRepo is not null
            ? moneyRepo
            : null;
        IInventoryPushService? push = context.Services.TryGet<IInventoryPushService>(out var pushService) && pushService is not null
            ? pushService
            : null;

        if (options.GrantPurchasedItems && (purchases is null || money is null || push is null))
        {
            throw new InvalidOperationException(
                "grant_purchased_items is on but the persistence row (IPlayerWalletRepository + IShopPurchaseStore) "
                + "or the lobby IInventoryPushService is missing; mount a persistence row (persistence.mysql or "
                + "persistence.memory) with the lobby row, or set grant_purchased_items: false.");
        }

        context.Logger.LogInformation(
            "Shop catalog {Path}: {Sets} on-sale cards ({Item} lucci, {Cash} cash), {Cats} categories. Store {Store}; purchases {Purchases}; grant {Grant}.",
            catalog.SourcePath, catalog.Cards.Count, catalog.ItemShopCards.Count(), cashCards.Count, catalog.Categories.Count,
            store.GetType().Name, purchases?.GetType().Name ?? "disabled", options.GrantPurchasedItems);

        var opens = new ShopOpenScope(options.MaxOpenSessions);
        context.Effect(opens);
        context.Effect(context.Events.On<ConnectionClosedEvent>(e => opens.Remove(e.Connection.Id)));

        // Every request and reply of this row leaves through the dialect row's typed bindings; the shop never
        // resolves an opcode. A binding the dialect row did not mount fails here, naming the message.
        var routes = context.Services.Get<IMessageRouter>().For(context);
        var protocol = options.Protocol;
        var catalogSender = RequireShopSender<ShopCatalogPublished>(context, protocol);
        var hold = new Data.EquipHoldStore();
        var persistPurchases = options.GrantPurchasedItems && purchases is not null && push is not null;
        var purchaser = new Purchasing.ShopPurchaser(
            options, catalog, purchases, push, context.Logger, hold)
        {
            ItemPurchasePolicy = persistPurchases
                ? Kr1256PurchasePolicy(options, workbook, purchases!, context.Services.Get<IInventoryRepository>(),
                    catalog.Cards.SelectMany(card => card.Stocks).Select(stock => stock.ItemId).Where(id => id > 0).ToHashSet())
                : null,
            ReplySender = RequireShopSender<ShopPurchaseReplied>(context, protocol),
            // The money publication rides the persisted purchase path only; an in-memory row never sends it.
            BalanceSender = persistPurchases ? RequireShopSender<ShopBalanceUpdated>(context, protocol) : null,
        };
        var handlers = new Handlers.ShopDialogHandlers(
            options, store, catalog, itemCards, cashCards, wallets, purchaser, persistPurchases, money, opens,
            catalogSender,
            RequireShopSender<ShopCashCatalogPublished>(context, protocol),
            RequireShopSender<ShopCardInfoReplied>(context, protocol),
            RequireShopSender<ShopCouponUseReplied>(context, protocol),
            RequireShopSender<ShopCashCheckReplied>(context, protocol),
            purchaser.ReplySender!, context.Logger);
        if (context.Services.GetAll<IInboundMessageBinding<ShopBalanceRequested>>()
            .Any(binding => binding.Protocol == protocol))
        {
            var balanceHandler = new Purchasing.ShopBalanceHandler(money,
                user =>
                {
                    var current = wallets.Get(user);
                    return new PlayerWallet(user, current.Lucci, current.Cash);
                }, RequireShopSender<ShopBalanceUpdated>(context, protocol));
            context.Effect(routes.On<ShopBalanceRequested>(protocol, balanceHandler.HandleAsync));
        }
        else
            context.Logger.LogInformation("Shop balance requests are unavailable on {Protocol}: no inbound binding.", protocol);
        context.Effect(routes.On<ShopCatalogRequested>(protocol, handlers.CatalogAsync));
        context.Effect(routes.On<ShopCashCatalogRequested>(protocol, handlers.CashCatalogAsync));
        context.Effect(routes.On<ShopPurchaseRequested>(protocol, handlers.BuyAsync));
        context.Effect(routes.On<ShopCardInfoRequested>(protocol, handlers.CardInfoAsync));
        context.Effect(routes.On<ShopCouponUseRequested>(protocol, handlers.CouponAsync));
        context.Effect(routes.On<ShopCashCheckRequested>(protocol, handlers.CashCheckAsync));
        // The inventory-dialog entry flushes held equip records; the dialect row accepts the empty body. The kr-1256
        // shop entry sub_9328B0 sends Unknown2059 once and nothing answers it, and gcpReqPresentSendSubList and
        // gcpReqRefund are not this row's: they stay unhandled until the gift box and refunds are served.
        var flush = new Handlers.EquipHoldFlushHandler(hold,
            RequireShopSender<EquippedArticlesFlushed>(context, protocol), context.Logger);
        context.Effect(routes.On<ShopInventoryRequested>(protocol, flush.HandleAsync));
        if (options.HoldEquipUntilInventoryOpen)
            context.Logger.LogInformation(
                "Equip hold on: a purchase queues one gspInventoryUpdateInventorySlots container-2 record (cap {Cap} per player) and gcpEnterInventory flushes it on {Protocol}.",
                Data.EquipHoldStore.MaxRecordsPerPlayer, protocol);

        context.Logger.LogInformation(
            "Shop catalog on {Protocol} (profile {Profile}).",
            protocol, options.ClientProfile);
        return ValueTask.CompletedTask;
    }

    /// <summary>The logged-in player's id, or 0 when the session has no player.</summary>
    /// <param name="session">The live session.</param>
    public static long TryGetUserNum(ISession session) =>
        session.TryFeature<IPlayerSession>(out var player) ? player.Identity.UserNum : 0;

    // kr-1256 purchases use the ShopItem terms the legacy builds use (the kr-1256 ShopItem sheet has the same
    // itemId/durationType/maxDuration/buyAgain/function columns, and the client limits renewals itself in
    // kr-1256 sub_8068D0 Inventory::IsMaxDurationOver). Without them a grant was stored as quantity 99999 with no
    // lifetime, which the legacy bag writer could not show to the same shared account.
    internal static Purchasing.ItemPurchasePolicy Kr1256PurchasePolicy(ShopOptions options, WorkbookFile workbook,
        IShopPurchaseStore purchases, IInventoryRepository inventory, IReadOnlySet<int>? purchasableItems = null)
    {
        if (options.LifeItems)
            throw new ConfigException("life_items is not established for the kr1256 catalog schema.");
        var rules = new ItemTermRules(options.PermanentDayStockTypes, options.RejectRepeatFunction10001);
        var terms = ItemTermsLoader.Load(workbook, options.MaxShopItemBytes, purchasableItems);
        var conditional = new Purchasing.ConditionalShopPurchaser(purchases, inventory, TimeProvider.System,
            options.MaxPurchaseAttempts);
        return new(conditional, stock => terms.TryGetValue(stock.ItemId, out var term)
            ? term.ForStock(stock, rules)
            : throw new Purchasing.PurchaseRejectedException($"Item {stock.ItemId} has no purchase terms."));
    }

    // The gift box rides the shop's catalog, item terms and purchase transaction; the dialect row owns its packets.
    private static void MountPresentBox(IPluginContext context, ShopOptions options, PacketTables tables, ShopCatalog catalog,
        Purchasing.ConditionalShopPurchaser conditional, Func<ShopStockRow, Purchasing.PurchaseGrantTerm> resolve,
        IInventoryRepository inventory, ShopOpenScope ready, IMessageSender<ShopStateUpdated> state, IMessageRoutes routes)
    {
        if (!tables.CapabilityOf(options.ClientProfile, "present_box"))
            throw new ConfigException($"present_box requires a build with the present_box capability; '{options.ClientProfile}' has none.");
        if (!Bound<PresentSendRequested>(context, options.Protocol) || !Bound<PresentListRequested>(context, options.Protocol)
            || !Bound<PresentActionRequested>(context, options.Protocol) || !Bound<PresentNoticeRequested>(context, options.Protocol))
            throw new ConfigException($"present_box requires the dialect row of '{options.Protocol}' to bind the gift packets (present_box there).");
        context.Services.TryGet<ISessionRegistry>(out var sessions);
        var service = new Presents.PresentService(options, catalog, conditional, resolve, inventory,
            context.Services.Get<IPresentStore>(), ready, state,
            RequireShopSender<ShopBalanceUpdated>(context, options.Protocol),
            RequireShopSender<PresentSendReplied>(context, options.Protocol),
            RequireShopSender<PresentListReplied>(context, options.Protocol),
            RequireShopSender<PresentActionReplied>(context, options.Protocol),
            RequireShopSender<PresentNoticeSent>(context, options.Protocol),
            sessions, TimeProvider.System, context.Logger);
        context.Effect(routes.On<PresentSendRequested>(options.Protocol, service.SendAsync));
        context.Effect(routes.On<PresentListRequested>(options.Protocol, service.ListAsync));
        context.Effect(routes.On<PresentActionRequested>(options.Protocol, service.ActionAsync));
        context.Effect(routes.On<PresentNoticeRequested>(options.Protocol, service.NoticeAsync));
        Presents.LevelRewardService? rewards = null;
        if (options.LevelRewardClientPath.Length > 0)
        {
            if (!context.Services.TryGet<BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore>(out var data) || data is null)
                throw new ConfigException("level_reward_client_path requires clientdata.");
            if (data.Exists(options.LevelRewardClientPath))
            {
                var native = LevelRewardTable.Read(data.Read(options.LevelRewardClientPath), catalog,
                    message => context.Logger.LogWarning("Level rewards: {Diagnostic}", message));
                options.LevelRewards = native.Concat(options.LevelRewards).GroupBy(entry => entry.Level)
                    .Select(group => group.Last()).OrderBy(entry => entry.Level).ToArray();
                if (options.LevelRewards.Length > ShopOptions.MaxLevelRewardEntries)
                    throw new ConfigException("Combined native and configured level rewards exceed the entry bound.");
                context.Logger.LogInformation("Loaded {Count} native level reward(s) for {Protocol}.", native.Length, options.Protocol);
            }
            else
                context.Logger.LogInformation("No native level reward guide at {Path} for {Protocol}.", options.LevelRewardClientPath, options.Protocol);
        }
        if (options.LevelRewards.Length > 0)
        {
            // A reward the workbook cannot deliver is a misconfiguration, found now rather than when a player levels up.
            foreach (var key in options.LevelRewards.SelectMany(entry => entry.StockKeys).Distinct())
            {
                if (!catalog.Gifts.TryGetValue(key, out var offer))
                    throw new ConfigException($"level_rewards names shop key {key}, which '{options.ClientProfile}' cannot grant.");
                try
                {
                    _ = offer.Stocks.Select(resolve).ToArray();
                }
                catch (InvalidOperationException ex)
                {
                    throw new ConfigException($"level_rewards key {key} cannot be delivered: {ex.Message}");
                }
            }
            if (!context.Services.TryGet<IPlayerProgressRepository>(out var progress) || progress is null)
                throw new ConfigException("level_rewards requires IPlayerProgressRepository to recover interrupted grants at login.");
            rewards = new Presents.LevelRewardService(options, context.Services.Get<IPresentStore>(), service,
                progress, TimeProvider.System, context.Logger);
            context.Effect(context.Events.OnAsync<PlayerLevelRaised>(rewards.OnLevelRaisedAsync));
        }
        context.Effect(context.Events.OnAsync<LoginMenuInstalled>(async (login, ct) =>
        {
            if (rewards is not null)
                await rewards.LoginAsync(login, ct).ConfigureAwait(false);
            await service.LoginAsync(login, ct).ConfigureAwait(false);
        }));
    }

    private static bool Bound<T>(IPluginContext context, string protocol) where T : IMessage =>
        context.Services.GetAll<IInboundMessageBinding<T>>().Any(binding => binding.Protocol == protocol);

    private static IMessageSender<T> RequireShopSender<T>(IPluginContext context, string protocol) where T : IMessage
    {
        var senders = context.Services.GetAll<IMessageSender<T>>().Where(s => s.Protocol == protocol).ToArray();
        return senders.Length == 1 ? senders[0]
            : throw new ConfigException($"Shop requires exactly one {typeof(T).Name} sender for {protocol}.");
    }


}
