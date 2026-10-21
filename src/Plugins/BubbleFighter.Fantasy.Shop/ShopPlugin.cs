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
using BubbleFighter.Fantasy.Shop.Dialects;
using BubbleFighter.Fantasy.Shop.Handlers;
using BubbleFighter.Fantasy.Shop.Wire;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Shop;

/// <summary>
/// Cash shop and item shop open / list / buy path. The profile row supplies the
/// protocol name, <c>version_major</c>, and the client path of <c>Config/ShopItem2.xml</c>, which is read
/// from the build's client data through <c>IClientDataStore</c>.
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
    Description = "Item shop and cash shop catalog from Config/ShopItem2.xml with persisted purchases. Dialect selected by version_major.", 
    Requires = [typeof(IPacketRouter), typeof(IShopSessionStore)],
    // An inventory-only row reads no workbook; a row that does fails at activation without the clientdata service.
    Optional = [typeof(BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore), typeof(IShopPurchaseStore), typeof(IPlayerWalletRepository), typeof(IInventoryPushService), typeof(IOpcodeTableRegistry), typeof(IInventoryRepository), typeof(IMessageRouter), typeof(IEquippedViewPush), typeof(IInventoryExpiry), typeof(IPresentStore), typeof(ISessionRegistry), typeof(IPlayerProgressRepository)], OptionalMany = [typeof(IInboundMessageBinding<PresentSendRequested>), typeof(IInboundMessageBinding<PresentListRequested>), typeof(IInboundMessageBinding<PresentActionRequested>), typeof(IInboundMessageBinding<PresentNoticeRequested>), typeof(IMessageSender<PresentSendReplied>), typeof(IMessageSender<PresentListReplied>), typeof(IMessageSender<PresentActionReplied>), typeof(IMessageSender<PresentNoticeSent>), typeof(IOpcodeTable), typeof(IMessageSender<ShopStateUpdated>), typeof(IMessageSender<ShopBalanceUpdated>), typeof(IInboundMessageBinding<ShopBalanceRequested>), typeof(IMessageSender<ShopPurchaseReplied>), typeof(IInboundMessageBinding<ShopPurchaseRequested>), typeof(IInboundMessageBinding<ShopInventoryRequested>), typeof(IMessageSender<InventoryReplaced>), typeof(IMessageSender<ItemsExpired>)],
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
        if (cashCards.Count == 0 && schema == ShopCatalogSchema.Kr1256)
            throw new InvalidOperationException($"{catalog.SourcePath} has no on-sale cash cards.");

        // Legacy opcodes beyond catalog entry have different meanings and layouts.
        // In particular the legacy builds have no cash shop entry; kr-812 gcpEnterInventory (kr-812 sub_6C3D20)
        // opens the inventory dialog.
        if (schema == ShopCatalogSchema.Legacy)
        {
            var tables = PacketTables.Load(options.PacketsDirectory);
            var shape = LegacyShopShape.From(options.ClientProfile, tables.ValuesOf(options.ClientProfile));
            if (options.HoldEquipUntilInventoryOpen)
                throw new ConfigException("The selected shop adapter does not support held equipment records.");
            var table = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.Protocol);
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
            var routes = (options.GrantPurchasedItems || enterInventory is not null)
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
                context.Effect(routes!.On<ShopPurchaseRequested>(options.Protocol, buy.BuyAsync));
                context.Effect(routes.On<ShopInventoryRequested>(options.Protocol, buy.InventoryAsync));
                if (context.Services.GetAll<IInboundMessageBinding<ShopBalanceRequested>>()
                    .Any(binding => binding.Protocol == options.Protocol))
                    context.Effect(routes.On<ShopBalanceRequested>(options.Protocol, buy.BalanceAsync));
                if (options.PresentBox)
                    MountPresentBox(context, options, tables, catalog, conditional, resolveTerms, inventoryLegacy, ready, stateSender, routes);
            }
            else if (options.PresentBox)
            {
                throw new ConfigException("present_box requires grant_purchased_items: a receiver takes a gift through the purchase path.");
            }
            else if (enterInventory is not null)
            {
                context.Effect(routes!.On<ShopInventoryRequested>(options.Protocol, enterInventory.HandleAsync));
            }
            if (options.SendShopCatalog)
            {
                var legacy = new LegacyShopCatalogHandler(shape, table.S2C("gspRepEnterShop"),
                    catalog, options.MaxEnterShopFrameBytes, legacyOpens, context.Services.Get<IShopSessionStore>(), context.Logger,
                    options.MaxEnterShopCards, options.PreferLongestDuration, options);
                context.Effect(context.Services.Get<IPacketRouter>().Register(options.Protocol,
                    table.C2S("gcpEnterShop"), async (session, packet, ct) =>
                    {
                        legacy.ValidateRequest(packet.Payload.Span);
                        if (buy is not null)
                            await buy.OpenAsync(session, ct).ConfigureAwait(false);
                        await legacy.HandleAsync(session, packet, ct).ConfigureAwait(false);
                    }));
            }
            return ValueTask.CompletedTask;
        }

        var router = context.Services.Get<IPacketRouter>();
        var store = context.Services.Get<IShopSessionStore>();
        var purchases = context.Services.TryGet<IShopPurchaseStore>(out var purchaseStore) ? purchaseStore : null;
        var wallets = new WalletStore(options.StartingLucci, options.StartingCash);

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

        // Request and reply numbers come from the build's opcode table by name; a table without one fails here.
        var opcodes = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(options.Protocol);
        var numbers = ShopPacketNumbers.From(opcodes);
        var hold = new Data.EquipHoldStore();
        var persistPurchases = options.GrantPurchasedItems && purchases is not null && push is not null;
        var purchaser = new Purchasing.ShopPurchaser(
            options, catalog, purchases, push, context.Logger, hold)
        {
            ItemPurchasePolicy = persistPurchases
                ? Kr1256PurchasePolicy(options, workbook, purchases!, context.Services.Get<IInventoryRepository>())
                : null,
            Packets = numbers,
        };
        var handlers = new Handlers(
            options, store, catalog, itemCards, cashCards, wallets, purchaser, persistPurchases, money, opens, numbers, context.Logger);
        var protocol = options.Protocol;
        if (context.Services.GetAll<IInboundMessageBinding<ShopBalanceRequested>>()
            .Any(binding => binding.Protocol == protocol))
        {
            var balanceHandler = new Purchasing.ShopBalanceHandler(money,
                user =>
                {
                    var current = wallets.Get(user);
                    return new PlayerWallet(user, current.Lucci, current.Cash);
                }, RequireShopSender<ShopBalanceUpdated>(context, protocol));
            context.Effect(context.Services.Get<IMessageRouter>().For(context)
                .On<ShopBalanceRequested>(protocol, balanceHandler.HandleAsync));
        }
        else
            context.Logger.LogInformation("Shop balance requests are unavailable on {Protocol}: no inbound binding.", protocol);
        context.Effect(router.Register(protocol, opcodes.C2S("gcpEnterShop"), handlers.HandleEnterShopAsync));
        context.Effect(router.Register(protocol, opcodes.C2S("gcpEnterTCShop_"), handlers.HandleEnterTCShopAsync));
        context.Effect(router.Register(protocol, opcodes.C2S("gcpReqShopBuy"), handlers.HandleShopBuyAsync));
        context.Effect(router.Register(protocol, opcodes.C2S("gcpReqShopItemCardInfo"), handlers.HandleShopItemCardInfoAsync));
        context.Effect(router.Register(protocol, opcodes.C2S("gcpReqRealCouponUse"), handlers.HandleRealCouponUseAsync));
        context.Effect(router.Register(protocol, opcodes.C2S("Unknown834"), handlers.HandleCheckCashAsync));
        // The kr-1256 shop entry sub_9328B0 sends Unknown2059 once and nothing answers it, so the row only accepts
        // the body; without it the shop entry lands in the unhandled log. gcpReqPresentSendSubList and gcpReqRefund
        // are not this row's: they stay unhandled until the gift box and refunds are served.
        context.Effect(router.Register(protocol, opcodes.C2S("Unknown2059"), handlers.HandleSceneBootstrapAsync));
        if (options.HoldEquipUntilInventoryOpen)
        {
            var inventoryRequest = new InventoryRequestHandlers(hold, numbers.InventorySlotsUpdate, context.Logger);
            context.Effect(router.Register(
                protocol, opcodes.C2S("gcpEnterInventory"), inventoryRequest.HandleRequestInventoryAsync));
            context.Logger.LogInformation(
                "Equip hold on: a purchase queues one gspInventoryUpdateInventorySlots container-2 record (cap {Cap} per player) and gcpEnterInventory flushes it on {Protocol}.",
                Data.EquipHoldStore.MaxRecordsPerPlayer, protocol);
        }

        context.Logger.LogInformation(
            "Shop dialect {Version} on {Protocol} (profile {Profile}).",
            options.VersionMajor, protocol, options.ClientProfile);
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
        IShopPurchaseStore purchases, IInventoryRepository inventory)
    {
        if (options.LifeItems)
            throw new ConfigException("life_items is not established for the kr1256 catalog schema.");
        var rules = new ItemTermRules(options.PermanentDayStockTypes, options.RejectRepeatFunction10001);
        var terms = ItemTermsLoader.Load(workbook, options.MaxShopItemBytes);
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

    private sealed class WalletStore(int startingLucci, int startingCash)
    {
        private readonly ConcurrentDictionary<long, (int Lucci, int Cash)> _byUser = new();

        public (int Lucci, int Cash) Get(long userNum) =>
            _byUser.GetOrAdd(userNum, _ => (startingLucci, startingCash));

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

    private sealed class Handlers(
        ShopOptions options,
        IShopSessionStore store,
        ShopCatalog catalog,
        IReadOnlyList<ShopCardRow> itemCards,
        IReadOnlyList<ShopCardRow> cashCards,
        WalletStore wallets,
        Purchasing.ShopPurchaser purchaser,
        bool persistPurchases,
        IPlayerWalletRepository? money,
        ShopOpenScope opens,
        ShopPacketNumbers numbers,
        ILogger logger)
    {
    public async ValueTask HandleSceneBootstrapAsync(ISession session, InboundPacket packet, CancellationToken ct)
        {
            // Fire-and-forget: kr-1256 sub_92F6C0 latches at dword_18E566C+984 and sends once, and no reply path
            // exists, so the row only accepts the body. The shop entry sends (0x0D, 0).
            var request = SceneBootstrapRequest.Parse(packet.Payload.Span);
            logger.LogInformation(
                "Session {Id} Unknown2059 unknown0=0x{Unknown0:X2} unknown4={Unknown4}; fire-and-forget, no reply.",
                session.Id, request.Unknown0, request.Unknown4);
            await ValueTask.CompletedTask.ConfigureAwait(false);
        }

        public async ValueTask HandleEnterShopAsync(ISession session, InboundPacket packet, CancellationToken ct)
        {
            var request = EnterShopRequest.Parse(packet.Payload.Span);
            var record = new ShopOpenRecord(TryGetUserNum(session), request.Token, request.Unknown4, ShopKind.ItemShop);
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
            if (options.SendShopCatalog)
            {
                var cards = itemCards;
                if (options.CatalogCardLimit > 0 && options.CatalogCardLimit < cards.Count)
                    cards = cards.Take(options.CatalogCardLimit).ToList();
                byte[] body;
                if (options.CatalogTree == "id_ranges")
                {
                    var (tree, placed) = ShopWire.BuildRangeTree(catalog, cards);
                    var selected = ShopWire.SelectEnterShopCategory(request.Unknown4, tree);
                    var revision = ShopWire.CatalogRevision(ShopWire.RepEnterShop(0, placed, tree, 0));
                    logger.LogInformation(
                        "Session {Id} gspRepEnterShop: {Mains} tab(s), {Placed} card(s), selected {Selected} (requested {Requested}).",
                        session.Id, tree.Count, placed.Count, selected, request.Unknown4);
                    body = ShopWire.RepEnterShop(revision, placed, tree, selected);
                }
                else
                {
                    if (catalog.Categories.All(c => c.Id != options.EnterShopMainCategory))
                        logger.LogWarning("Session {Id} selected shop category {Category} is absent from the catalog.",
                            session.Id, options.EnterShopMainCategory);
                    body = ShopWire.RepEnterShop(request.Token, cards, catalog.Categories, options.EnterShopMainCategory);
                }
                await session.SendAsync(numbers.EnterShopReply, body, ct).ConfigureAwait(false);
            }
        }

        public async ValueTask HandleEnterTCShopAsync(ISession session, InboundPacket packet, CancellationToken ct)
        {
            var request = EnterTCShopRequest.Parse(packet.Payload.Span);
            var record = new ShopOpenRecord(TryGetUserNum(session), request.Token, 0, ShopKind.CashShop);
            opens.Remember(session.Connection.Id, record);
            await store.RememberOpenAsync(record, ct).ConfigureAwait(false);
            logger.LogInformation(
                "Session {Id} gcpEnterTCShop_ token={Token} cashCards={Cards}.",
                session.Id, request.Token, cashCards.Count);
            await session.SendAsync(numbers.EnterCashShopReply, ShopWire.RepEnterTCShop(request.Token, cashCards), ct)
                .ConfigureAwait(false);
        }

        public ValueTask HandleShopBuyAsync(ISession session, InboundPacket packet, CancellationToken ct)
        {
            var request = ShopBuyRequest.Parse(packet.Payload.Span);
            // Persistence active: charge, log, grant, push. Otherwise the row
            // answers from the in-memory wallet and logs that nothing is stored.
            return persistPurchases
                ? purchaser.PurchaseAsync(session, request.ItemId, ct)
                : ReplyBuyAsync(session, request.ItemId, ct);
        }

        public ValueTask HandleShopItemCardInfoAsync(ISession session, InboundPacket packet, CancellationToken ct) =>
            ReplyCardAsync(session, ShopItemCardInfoRequest.Parse(packet.Payload.Span).ItemId, ct);

        public ValueTask HandleRealCouponUseAsync(ISession session, InboundPacket packet, CancellationToken ct)
        {
            var request = RealCouponUseRequest.Parse(packet.Payload.Span);
            logger.LogError(
                "Session {Id} gcpReqRealCouponUse unknown0Length={Length}: no coupon table in ShopItem2.xml. Replying gspRepRealCouponUse unknown0=-1.",
                session.Id, request.Unknown0.Length);
            return session.SendAsync(numbers.RealCouponUseReply, ShopWire.RepRealCouponUse(-1), ct);
        }

        public async ValueTask HandleCheckCashAsync(ISession session, InboundPacket packet, CancellationToken ct)
        {
            var request = CheckCashRequest.Parse(packet.Payload.Span);
            var user = TryGetUserNum(session);
            if (money is not null)
            {
                // A first-seen player has no row yet: seed the configured starting
                // balances now so the buy flow has a wallet to charge. The login
                // burst still sends the all-zero snapshot, so this is
                // the first place the client learns a real balance.
                var stored = await money.FindByUserNumAsync(user, ct).ConfigureAwait(false);
                var wallet = stored ?? Purchasing.ShopPurchaser.SeedWallet(options, user);
                if (stored is null)
                    await money.UpsertAsync(wallet, ct).ConfigureAwait(false);

                logger.LogInformation(
                    "Session {Id} Unknown834 unknown1Length={Length} cash={Cash} lucci={Lucci} (stored wallet).",
                    session.Id, request.Unknown1.Length, wallet.Cash, wallet.Lucci);
                await session.SendAsync(numbers.CheckCashReply, ShopWire.RepCheckCash(0, wallet.Cash, 0), ct)
                    .ConfigureAwait(false);
                // gspRepCheckCash only writes the cash dword (+0x50); the buy pre-check reads the cached lucci
                // dword (+0x4C), so re-push the full six-field snapshot the login burst uses.
                await session.SendAsync(
                    numbers.UserMoneyStat, ShopWire.RepUserMoneyStat(wallet), ct)
                    .ConfigureAwait(false);
                return;
            }

            var cash = wallets.Get(user).Cash;
            logger.LogInformation(
                "Session {Id} Unknown834 unknown1Length={Length} cash={Cash} (in-memory).",
                session.Id, request.Unknown1.Length, cash);
            await session.SendAsync(numbers.CheckCashReply, ShopWire.RepCheckCash(0, cash, 0), ct)
                .ConfigureAwait(false);
        }

        private ValueTask ReplyBuyAsync(ISession session, int key, CancellationToken ct)
        {
            var card = catalog.FindPurchase(key);
            if (card is null)
            {
                logger.LogError("Session {Id} shop buy key={Key} not in catalog. gspRepShopBuy result=0.", session.Id, key);
                return session.SendAsync(numbers.ShopBuyReply, ShopWire.RepShopBuy(key, 0, string.Empty), ct);
            }

            var user = TryGetUserNum(session);
            if (!wallets.TrySpend(user, card))
            {
                logger.LogError(
                    "Session {Id} shop buy key={Key} item={Item} refused: wallet cannot pay {Price} {Money}.",
                    session.Id, key, card.Stocks[0].ItemId, card.Price, card.MoneyType);
                return session.SendAsync(numbers.ShopBuyReply, ShopWire.RepShopBuy(key, 0, string.Empty), ct);
            }

            logger.LogInformation(
                "Session {Id} shop buy key={Key} stockSet={Set} item={Item} name={Name}. gspRepShopBuy result=1. Inventory grant is not sent.",
                session.Id, key, card.StockSetId, card.Stocks[0].ItemId, card.Name);
            return session.SendAsync(
                numbers.ShopBuyReply,
                ShopWire.RepShopBuy(key, 1, string.Empty),
                ct);
        }

        private ValueTask ReplyCardAsync(ISession session, int key, CancellationToken ct)
        {
            var card = catalog.Find(key);
            if (card is null)
            {
                logger.LogError("Session {Id} shop card key={Key} not in catalog. gspRepShopItemCardInfo result=-1.", session.Id, key);
                return session.SendAsync(numbers.ShopItemCardInfoReply, ShopWire.RepShopItemCardInfo(-1, null), ct);
            }

            logger.LogInformation(
                "Session {Id} shop card key={Key} stockSet={Set} item={Item}. gspRepShopItemCardInfo result=1.",
                session.Id, key, card.StockSetId, card.PrimaryItemId);
            return session.SendAsync(numbers.ShopItemCardInfoReply, ShopWire.RepShopItemCardInfo(1, card), ct);
        }
    }
}
