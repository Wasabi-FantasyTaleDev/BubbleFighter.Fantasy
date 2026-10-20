using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Clans;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Shop;
using Microsoft.Extensions.Logging;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Dialect.Messages;

/// <summary>
/// Adapts the legacy builds' game connection wire messages to the shared
/// lobby contracts. Where the builds differ, the row's lineage selects the layout of the nearest
/// build that has one. Also registers C2S <c>gcpReqGotoLobby</c> wherever the build's table
/// names it, and the mid-CN main-menu <c>gspGotoRoom</c> / <c>gspGotoMenu</c> sender for
/// cn-735 and its descendants (no <c>dialect.rooms</c> row on those profiles).
/// </summary>
[Plugin("dialect.messages", Description = "Typed message adapters for every build, with per-build layouts from the packet tables.",
    Requires = [typeof(IPacketRouter), typeof(IMessageRouter), typeof(IOpcodeTableRegistry)],
    Optional = [], OptionalMany = [typeof(IOpcodeTable), typeof(IMessageSender<MainMenuRoomOpened>)],
    Provides = [], ProvidesMany = [],
    ConditionalProvides = [], ConditionalProvidesMany = [typeof(IInboundMessageBinding<LoginRequested>), typeof(IMessageSender<LoginAccepted>), typeof(IMessageSender<LoginRejected>), typeof(IMessageSender<LoginSucceeded>), typeof(IInboundMessageBinding<MainMenuInitRequest>), typeof(IMessageSender<EmptySessionSignature>), typeof(IInboundMessageBinding<CenterStatusRequested>), typeof(IInboundMessageBinding<CenterAvailabilityRequested>), typeof(IMessageSender<CenterStatusReplied>), typeof(IMessageSender<CenterAvailabilityReplied>), typeof(IMessageSender<ServerNotice>), typeof(IInboundMessageBinding<InventoryItemUseRequested>), typeof(IMessageSender<InventoryItemUseReplied>), typeof(IMessageSender<UserStatUpdated>), typeof(IInboundMessageBinding<CharacterNameRequested>), typeof(IInboundMessageBinding<CharacterCreateRequested>), typeof(IInboundMessageBinding<CharacterLoginRequested>), typeof(IMessageSender<CharacterCreationOpened>), typeof(IMessageSender<CharacterNameReplied>), typeof(IMessageSender<CharacterCreateReplied>), typeof(IMessageSender<MainMenuRoomOpened>), typeof(IMessageSender<ShopStateUpdated>), typeof(IMessageSender<ShopPurchaseReplied>), typeof(IMessageSender<ShopBalanceUpdated>), typeof(IInboundMessageBinding<ShopBalanceRequested>), typeof(IInboundMessageBinding<ShopPurchaseRequested>), typeof(IInboundMessageBinding<ShopInventoryRequested>), typeof(IMessageSender<InventoryReplaced>), typeof(IMessageSender<InventorySlotsUpdated>), typeof(IMessageSender<InventoryPurchaseApplied>), typeof(IMessageSender<ItemsExpired>), typeof(IMessageSender<LocalEquipmentViewUpdated>), typeof(IMessageSender<RemoteEquipmentViewUpdated>), typeof(IInboundMessageBinding<EquipmentChangeRequested>), typeof(IMessageSender<EquipmentChangeReplied>), typeof(IInboundMessageBinding<WeaponPresetChangeRequested>), typeof(IMessageSender<WeaponPresetChangeReplied>), typeof(IInboundMessageBinding<PartyInviteRequested>), typeof(IInboundMessageBinding<PartyInviteAccepted>), typeof(IInboundMessageBinding<PartyInviteAnswered>), typeof(IInboundMessageBinding<PartyInviteTokenRefused>), typeof(IInboundMessageBinding<PartyLeaveRequested>), typeof(IInboundMessageBinding<PartyKickRequested>), typeof(IInboundMessageBinding<PartyLeaderChangeRequested>), typeof(IMessageSender<PartyInviteReplied>), typeof(IMessageSender<PartyInviteNotified>), typeof(IMessageSender<PartyUpdated>), typeof(IMessageSender<PartyLeaderReplaced>), typeof(IMessageSender<PartyLeft>), typeof(IMessageSender<PartyMemberLeft>), typeof(IMessageSender<PartyMemberJoined>), typeof(IMessageSender<PartyKickReplied>), typeof(IMessageSender<PartyKicked>), typeof(IMessageSender<PartyMemberKicked>), typeof(IMessageSender<PartyLeaderChanged>), typeof(IInboundMessageBinding<ClanServerAckReceived>), typeof(IInboundMessageBinding<ClanMainOpened>), typeof(IInboundMessageBinding<ClanAttendanceCheckedIn>), typeof(IMessageSender<ClanOpened>), typeof(IMessageSender<ClanAttendanceStateUpdated>), typeof(IInboundMessageBinding<FighterInfoRequested>), typeof(IMessageSender<FighterInfoReplied>), typeof(IInboundMessageBinding<PresentSendRequested>), typeof(IInboundMessageBinding<PresentListRequested>), typeof(IInboundMessageBinding<PresentActionRequested>), typeof(IInboundMessageBinding<PresentNoticeRequested>), typeof(IMessageSender<PresentSendReplied>), typeof(IMessageSender<PresentListReplied>), typeof(IMessageSender<PresentActionReplied>), typeof(IMessageSender<PresentNoticeSent>)])]
public sealed class MessagesPlugin : IPlugin
{
    // cn-774 carries the same GotoRoom/GotoMenu pair on a different lineage (kr-1256) and is not yet decoded.
    private static readonly BuildVariants<bool> MidCnMenu = new BuildVariants<bool>("mid-CN main menu").Add("cn-735", true);

    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<MessagesOptions>();
        var build = context.Row.ClientBuild ?? throw new ConfigException("dialect.messages requires an explicit client_build.");
        var lineage = context.Row.LineageOrBuild();
        var tables = context.Services.Get<IOpcodeTableRegistry>();
        var router = context.Services.Get<IMessageRouter>();
        var table = tables.ForProtocol(options.Protocol);
        // The packet tables are read once per row; a build the tables do not name uses its nearest ancestor's.
        var packetTablesOfRow = new Lazy<(PacketTables Tables, string Build)>(() =>
        {
            var loaded = PacketTables.Load(options.PacketsDirectory);
            return (loaded, loaded.NearestBuild(lineage)
                ?? throw new ConfigException($"No build of lineage [{string.Join(", ", lineage)}] is named in '{options.PacketsDirectory}'."));
        });
        var (loginTables, loginBuild) = packetTablesOfRow.Value;
        var loginValues = LoginValues.From(loginBuild, loginTables.ValuesOf(loginBuild));
        var shape = loginValues.Shape;
        context.Logger.LogInformation("dialect.messages: login layout of build {Build} ({Shape}, {Reply} reply, {Burst} steps) for lineage [{Lineage}].",
            loginBuild, shape, loginValues.Reply, loginValues.Burst.Count, string.Join(", ", lineage));
        // character_creation asks for native creation where the build has it (its packets/builds.yaml capability,
        // inherited through base), so the bundle states it once; the preview still requires the capability.
        var creation = options.CharacterCreation
            && packetTablesOfRow.Value.Tables.CapabilityOf(packetTablesOfRow.Value.Build, "character_creation");
        if (options.CharacterCreationPreview || creation)
        {
            if (!packetTablesOfRow.Value.Tables.CapabilityOf(packetTablesOfRow.Value.Build, "character_creation"))
                throw new ConfigException("Native character creation preview requires a build with the character_creation capability.");
            new CharacterCreationBinding(options.Protocol, table, packetTablesOfRow.Value.Tables, packetTablesOfRow.Value.Build).Bind(context, router);
        }
        if (options.CenterDirectory)
            new CenterDirectoryBinding(options.Protocol, table, packetTablesOfRow.Value.Tables, packetTablesOfRow.Value.Build).Bind(context, router);
        if (options.Party)
        {
            if (!packetTablesOfRow.Value.Tables.CapabilityOf(packetTablesOfRow.Value.Build, "party"))
                throw new ConfigException("party requires a build with the party capability.");
            new PartyBinding(options.Protocol, table, packetTablesOfRow.Value.Tables, packetTablesOfRow.Value.Build).Bind(context, router);
        }
        if (options.Clan)
        {
            if (!packetTablesOfRow.Value.Tables.CapabilityOf(packetTablesOfRow.Value.Build, "clan"))
                throw new ConfigException("clan requires a build with the clan capability.");
            new ClanBinding(options.Protocol, table, packetTablesOfRow.Value.Tables, packetTablesOfRow.Value.Build).Bind(context, router);
        }
        if (options.FighterInfo)
        {
            if (!packetTablesOfRow.Value.Tables.CapabilityOf(packetTablesOfRow.Value.Build, "fighter_info"))
                throw new ConfigException("fighter_info requires a build with the fighter_info capability.");
            new FighterInfoBinding(options.Protocol, table, packetTablesOfRow.Value.Tables, packetTablesOfRow.Value.Build).Bind(context, router);
        }
        if (options.ServerNotices)
        {
            if (!packetTablesOfRow.Value.Tables.CapabilityOf(packetTablesOfRow.Value.Build, "server_notice"))
                throw new ConfigException("server_notices requires a build with the server_notice capability.");
            var (packetTables, buildName) = packetTablesOfRow.Value;
            if (packetTables.Resolve(buildName, "ServerNotice").Count == 0)
                throw new ConfigException("Server notices require a verified packet layout for this build.");
            context.Effect(context.Services.RegisterMany<IMessageSender<ServerNotice>>(
                new ServerNoticeBinding(options.Protocol, table, packetTables, buildName)));
        }
        ExpiredItemsBinding? expiredItems = null;
        if (options.ExpiredItemNotices)
        {
            var (packetTables, buildName) = packetTablesOfRow.Value;
            if (!packetTables.CapabilityOf(buildName, "expired_item_notice"))
                throw new ConfigException("expired_item_notices requires a build with the expired_item_notice capability.");
            if (packetTables.Resolve(buildName, "ExpiredItems").Count == 0)
                throw new ConfigException("Expired item notices require a verified packet layout for this build.");
            expiredItems = new ExpiredItemsBinding(options.Protocol, table, packetTables, buildName);
            context.Effect(context.Services.RegisterMany<IMessageSender<ItemsExpired>>(expiredItems));
        }
        if (options.InventoryItemUse)
        {
            var (packetTables, buildName) = packetTablesOfRow.Value;
            if (packetTables.Resolve(buildName, "InventoryUseRequest").Count == 0)
                throw new ConfigException("Inventory item use requires a verified packet layout for this build.");
            new InventoryUseBinding(options.Protocol, table, packetTables, buildName).Bind(context, router);
        }
        if (options.PresentBox)
        {
            var (packetTables, buildName) = packetTablesOfRow.Value;
            if (!packetTables.CapabilityOf(buildName, "present_box"))
                throw new ConfigException($"present_box requires a build with the present_box capability; '{buildName}' has none.");
            new PresentBinding(options.Protocol, table, packetTables, buildName).Bind(context, router);
        }
        InventoryBinding? inventoryBinding = null;
        InventoryBaselineScope? inventoryScope = null;
        // The bag senders are published for a row that states the inventory option (the build's own
        // publication order and envelope) or that binds the shop, which owns the purchases path.
        if (options.EnablePurchases || options.Inventory)
        {
            // One inventory time origin per connection, shared by every bag and shop writer.
            inventoryScope = new InventoryBaselineScope(options.MaxShopSessions, TimeProvider.System);
            context.Effect(inventoryScope);
            context.Effect(context.Events.On<ConnectionClosedEvent>(e => inventoryScope.Remove(e.Connection.Id)));
            inventoryBinding = new InventoryBinding(options.Protocol, table,
                packetTablesOfRow.Value.Tables, packetTablesOfRow.Value.Build, inventoryScope);
            context.Effect(context.Services.RegisterMany<IMessageSender<InventoryReplaced>>(inventoryBinding));
            context.Effect(context.Services.RegisterMany<IMessageSender<InventorySlotsUpdated>>(inventoryBinding));
            context.Effect(context.Services.RegisterMany<IMessageSender<InventoryPurchaseApplied>>(inventoryBinding));
        }
        if (options.EnablePurchases)
        {
            var cashCheckRequest = packetTablesOfRow.Value.Tables.CapabilityOf(packetTablesOfRow.Value.Build, "cash_check_request");
            var scope = inventoryScope
                ?? throw new InvalidOperationException("The purchases path requires the inventory time origin.");
            _ = packetTablesOfRow.Value.Tables.Resolve(packetTablesOfRow.Value.Build, "ShopPurchaseReply");
            _ = packetTablesOfRow.Value.Tables.Resolve(packetTablesOfRow.Value.Build, "ShopPurchaseRequest");
            var shop = new ShopBinding(options.Protocol, table, scope, packetTablesOfRow.Value.Tables, packetTablesOfRow.Value.Build);
            context.Effect(context.Services.RegisterMany<IMessageSender<ShopStateUpdated>>(shop));
            context.Effect(context.Services.RegisterMany<IMessageSender<ShopBalanceUpdated>>(shop));
            if (cashCheckRequest)
            {
                context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ShopBalanceRequested>>(shop));
                context.Effect(context.Services.Get<IPacketRouter>().Register(options.Protocol,
                    table.C2S("gcpReqCheckCash"), async (session, packet, ct) =>
                    {
                        var request = ShopBinding.ReadBalanceRequest(packet.Payload.Span);
                        if (!await router.DispatchAsync(options.Protocol, session, request, ct).ConfigureAwait(false))
                            throw new InvalidOperationException("The balance message has no handler.");
                    }));
            }
            context.Effect(context.Services.RegisterMany<IMessageSender<ShopPurchaseReplied>>(shop));
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ShopPurchaseRequested>>(shop));
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<ShopInventoryRequested>>(shop));
            context.Effect(context.Services.Get<IPacketRouter>().Register(options.Protocol,
                table.C2S("gcpReqShopBuy"), async (session, packet, ct) =>
                {
                    var request = ShopBinding.ReadPurchase(packetTablesOfRow.Value.Tables, packetTablesOfRow.Value.Build, packet.Payload.Span);
                    if (!await router.DispatchAsync(options.Protocol, session, request, ct).ConfigureAwait(false))
                        throw new InvalidOperationException("The purchase message has no handler.");
                }));
            context.Effect(context.Services.Get<IPacketRouter>().Register(options.Protocol,
                table.C2S("gcpEnterInventory"), async (session, packet, ct) =>
                {
                    if (packet.Payload.Length != 0)
                        throw new PacketFormatException("Inventory entry requires an empty body.", packet.Payload.Length);
                    if (!await router.DispatchAsync(options.Protocol, session, new ShopInventoryRequested(), ct).ConfigureAwait(false))
                        throw new InvalidOperationException("The inventory message has no handler.");
                }));

            if (table.Find(PacketDirection.ServerToClient, "gspReqWeaponPreSetUpdate") is not null
                || table.Find(PacketDirection.ServerToClient, "gspReqInventoryChangeWeaponPresetIdx") is not null)
                PresetWire.RequirePresets(packetTablesOfRow.Value.Tables, packetTablesOfRow.Value.Build);

            BindEquipmentView(required: false);
            BindEquipment(presets: true);
        }
        else
        {
            if (options.EquipmentView)
                BindEquipmentView(required: true);
            if (options.Equipment)
                BindEquipment(presets: false);
        }
        if (options.Login)
        {
            var requestOpcode = table.C2S("gcpReqLogin");
            var replyOpcode = table.S2C("gspRepLogin");
            // A burst that names the bag step needs the row to publish it: the modern reply form always sent
            // the bag, and without a publication the step would silently drop S2C 802/803/808/810. The
            // response-pair family (cn-735 and the builds based on it) has no bag publication by design -
            // its reply carries the user state and its bundle mounts no inventory row - so it keeps the step
            // absent.
            if (loginValues.Burst.Contains(LoginBurstStep.Inventory) && inventoryBinding is null
                && !loginValues.Shape.LoginResponse)
                throw new ConfigException(
                    $"'{context.Row.Id}' ({options.Protocol}, build '{loginBuild}') names the inventory step in its login.burst but publishes no bag; set inventory: true (or enable_purchases) on this row.");
            var binding = new LoginBinding(options.Protocol, loginValues, replyOpcode, table, loginTables, loginBuild,
                context.Services, context.Events, inventoryBinding, expiredItems, context.Logger);
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<LoginRequested>>(binding));
            context.Effect(context.Services.RegisterMany<IMessageSender<LoginAccepted>>(binding));
            context.Effect(context.Services.RegisterMany<IMessageSender<LoginRejected>>(binding));
            context.Effect(context.Services.RegisterMany<IMessageSender<LoginSucceeded>>(binding));
            context.Effect(context.Services.Get<IPacketRouter>().Register(options.Protocol, requestOpcode, async (session, packet, ct) =>
            {
                // A match-bound session's C2S 3 is the client naming the round's map on its match
                // connection, not a login: the in-game seam answers it with the in-session bootstrap.
                if (await context.Events.WaterfallAsync<LoginConnectionRouting, bool>(new(options.Protocol, session),
                        _ => ValueTask.FromResult(false), ct).ConfigureAwait(false))
                {
                    context.Logger.LogInformation(
                        "Session {Id} C2S 3 on a match-bound session; the in-game bootstrap owns it, no login chain.",
                        session.Id);
                    return;
                }
                // The reconnect body is never a login: it must arrive on an authenticated relay connection.
                if (LoginBodyShape.IsReconnectShape(packet.Payload.Span))
                {
                    context.Logger.LogWarning("Session {Id} rejected an unauthenticated match reconnect on the login protocol.", session.Id);
                    await session.Connection.CloseAsync("match reconnect requires relay authentication").ConfigureAwait(false);
                    return;
                }
                LoginRequested request;
                try
                {
                    request = LoginMessageCodec.ReadRequest(packet.Payload.Span, shape);
                }
                catch (PacketFormatException ex)
                {
                    // The body is closed, not answered with a gspRepLogin refusal: a body that matches no
                    // client builder is never repaired into a login, so it fails loud instead of keeping the
                    // socket open with an error dialog.
                    context.Logger.LogError("Session {Id} gcpReqLogin does not match the {Build} layout ({Reason}); closing.", session.Id, build, ex.Message);
                    await session.Connection.CloseAsync("malformed gcpReqLogin").ConfigureAwait(false);
                    return;
                }
                if (!await router.DispatchAsync(options.Protocol, session, request, ct).ConfigureAwait(false))
                    throw new InvalidOperationException($"No login handler is installed for the {options.Protocol} protocol.");
            }));
            // The login window's requests the modern login codec owned. They are bound only where the build's
            // table names them, so a legacy build that does not name one keeps no handler.
            if (table.Find(PacketDirection.ClientToServer, "gcpRepPing") is { } pingReply)
                context.Effect(context.Services.Get<IPacketRouter>().Register(options.Protocol, pingReply,
                    static (_, _, _) => ValueTask.CompletedTask));
            if (table.Find(PacketDirection.ClientToServer, "Unknown53") is { } moveStage)
            {
                _ = loginTables.Resolve(loginBuild, "MoveStageRequest");
                _ = loginTables.Resolve(loginBuild, "gspRepMoveInto");
                var moveInto = table.S2C("gspRepMoveInto");
                context.Effect(context.Services.Get<IPacketRouter>().Register(options.Protocol, moveStage, (session, packet, ct) =>
                {
                    // The reply's result must be nonzero: a zero result shuts the client's socket down
                    // (kr-1256 sub_10BA630), so a successful move answers 1.
                    var channel = LoginBurstWire.ReadMoveStage(loginTables, loginBuild, packet.Payload.Span);
                    context.Logger.LogInformation(
                        "Session {Id} C2S 53 channel move to {Channel} (builder sub_56FB50); replying S2C 11 gspRepMoveInto.",
                        session.Id, channel);
                    return session.SendAsync(moveInto, LoginBurstWire.WriteMoveInto(loginTables, loginBuild, 1, channel), ct);
                }));
            }
            if (table.Find(PacketDirection.ClientToServer, "gcpReqDynamicConfig") is { } clientEnvironment)
            {
                var dynamicConfig = table.S2C("gspRepDynamicConfig");
                context.Effect(context.Services.Get<IPacketRouter>().Register(options.Protocol, clientEnvironment,
                    (session, packet, ct) =>
                    {
                        context.Logger.LogInformation(
                            "Session {Id} C2S 1500 gcpClientEnvironment ({Bytes} B, builder sub_99E860). Replying S2C 1500 mask 0 (sub_65F370: no zlib blobs).",
                            session.Id, packet.Payload.Length);
                        return session.SendAsync(dynamicConfig, LoginBurstWire.WriteDynamicConfigEmpty(), ct);
                    }));
            }
            if (table.Find(PacketDirection.ClientToServer, "gcpReqUserLaboratoryState") is { } laboratory)
            {
                context.Effect(context.Services.Get<IPacketRouter>().Register(options.Protocol, laboratory,
                    (session, packet, _) =>
                    {
                        context.Logger.LogInformation(
                            "Session {Id} C2S 109 gcpReqUserLaboratoryState ({Bytes} B, builder sub_E7E020). No reply: S2C 109 has no sourced body.",
                            session.Id, packet.Payload.Length);
                        return ValueTask.CompletedTask;
                    }));
            }
        }
        // The kr-1256 family's main-menu probe (C2S 521, builders sub_937560 / sub_937920, empty body) is
        // answered with the empty gspSessionSignatureData body; the lobby's main-menu handler publishes the
        // reply through this row, exactly as the modern row answered it before this slice. The probe is not
        // the legacy gcpReqGotoLobby request: the client's 521 is an anonymous empty packet with no main-menu
        // role, and the legacy request keeps its own handler below.
        if (options.MainMenuInit)
        {
            var probeOpcode = table.C2S("Unknown521");
            var signatureOpcode = table.S2C("gspSessionSignatureData");
            if (loginTables.Resolve(loginBuild, "SessionSignatureData").Count == 0)
                throw new ConfigException($"main_menu_init requires a gspSessionSignatureData layout; build '{loginBuild}' has none.");
            var binding = new MainMenuInitBinding(options.Protocol, loginTables, loginBuild, signatureOpcode);
            context.Effect(context.Services.RegisterMany<IInboundMessageBinding<MainMenuInitRequest>>(binding));
            context.Effect(context.Services.RegisterMany<IMessageSender<EmptySessionSignature>>(binding));
            context.Effect(context.Services.Get<IPacketRouter>().Register(options.Protocol, probeOpcode, async (session, packet, ct) =>
            {
                if (!packet.Payload.IsEmpty)
                    context.Logger.LogError(
                        "Session {Id} C2S 521 expected an empty body from kr-1256 sub_937560; payload={Hex}. Continuing the empty signature request.",
                        session.Id, Convert.ToHexString(packet.Payload.Span));
                if (!await router.DispatchAsync(options.Protocol, session, new MainMenuInitRequest(), ct).ConfigureAwait(false))
                    throw new InvalidOperationException("No main-menu handler is installed.");
            }));
        }
        // Plaza enter needs both packets; a build whose table lacks the proven C2S row
        // (cn-735 on, where no sender has been read yet) never gets the handler. The play-room
        // codec delivers the whole entry burst when it owns the plaza, so this row must stay out
        // of the way: one opcode cannot be registered twice.
        if (table.Find(PacketDirection.ClientToServer, "gcpReqGotoLobby") is { } gotoLobbyRequest && !options.TypedPlazaEnter)
        {
            var gotoLobby = new GotoLobbyHandler(table.S2C("gspGotoLobby"), context.Logger);
            context.Effect(context.Services.Get<IPacketRouter>().Register(options.Protocol, gotoLobbyRequest, gotoLobby.HandleAsync));
        }
        // Mid-CN menu (GotoRoom + GotoMenu only) for cn-735 and the builds that inherit it.
        if (MidCnMenu.Resolve(lineage) is not null)
        {
            foreach (var structure in new[] { "gspGotoRoom", "SessionSetting", "GameSetting", "UserSlot", "EquippedItemList", "RoomSceneTransition" })
                _ = packetTablesOfRow.Value.Tables.Resolve(packetTablesOfRow.Value.Build, structure);
            context.Effect(context.Services.RegisterMany<IMessageSender<MainMenuRoomOpened>>(
                new MidCnMenuBinding(options.Protocol, tables, packetTablesOfRow.Value.Tables, packetTablesOfRow.Value.Build)));
        }
        return ValueTask.CompletedTask;

        // The equipped view and its preset publications, in the order the build's equipment.local_view states;
        // with the bag a build whose table lacks the view row has no view senders; a row that asks for the view needs it.
        void BindEquipmentView(bool required)
        {
            if ((required ? table.S2C("gspInventoryViewUpdateView") : table.Find(PacketDirection.ServerToClient, "gspInventoryViewUpdateView")) is not { } viewOpcode)
                return;
            var values = EquipmentViewValues.Of(packetTablesOfRow.Value.Tables, packetTablesOfRow.Value.Build);
            // A part the build lists must have its packet: equipment.local_view is the build's own statement.
            ushort? Part(string part, string packet) => values.LocalView.Contains(part) ? table.S2C(packet) : null;
            var view = new EquipmentViewBinding(options.Protocol, viewOpcode,
                Part("preset_update", "gspReqWeaponPreSetUpdate"), Part("preset_reply", "gspReqInventoryChangeWeaponPresetIdx"),
                packetTablesOfRow.Value.Tables, packetTablesOfRow.Value.Build);
            context.Effect(context.Services.RegisterMany<IMessageSender<LocalEquipmentViewUpdated>>(view));
            context.Effect(context.Services.RegisterMany<IMessageSender<RemoteEquipmentViewUpdated>>(view));
        }

        // Wear and removal requests with their result replies, and the weapon preset switch where the build's
        // table names it and the row also owns the bag (presets).
        void BindEquipment(bool presets)
        {
            // Equipment and preset switches are bound only where the build names them;
            // a request without its reply is never routed. The wear/removal result reply rides
            // only the builds whose bag UI reads it (capability equip_result_reply in builds.yaml).
            _ = packetTablesOfRow.Value.Tables.Resolve(packetTablesOfRow.Value.Build, "InventoryEquipmentRequest");
            _ = packetTablesOfRow.Value.Tables.Resolve(packetTablesOfRow.Value.Build, "InventoryEquipmentResult");
            var equipResultReply = packetTablesOfRow.Value.Tables.CapabilityOf(packetTablesOfRow.Value.Build, "equip_result_reply");
            var equipment = new EquipmentBinding(options.Protocol,
                presets ? table.Find(PacketDirection.ServerToClient, "gspReqInventoryChangeWeaponPresetIdx") : null,
                packetTablesOfRow.Value.Tables, packetTablesOfRow.Value.Build,
                equipResultReply ? table.S2C("gspRepInventoryEquip") : null,
                equipResultReply ? table.S2C("gspRepInventoryUnequip") : null);
            var packets = context.Services.Get<IPacketRouter>();
            var requestsBound = false;
            if (table.Find(PacketDirection.ClientToServer, "gcpReqInventoryEquip") is { } equip)
            {
                requestsBound = true;
                context.Effect(packets.Register(options.Protocol, equip, async (session, packet, ct) =>
                {
                    var request = EquipmentRequestWire.ReadRequest(packetTablesOfRow.Value.Tables, packetTablesOfRow.Value.Build, packet.Payload.Span, true);
                    if (!await router.DispatchAsync(options.Protocol, session, request, ct).ConfigureAwait(false))
                        throw new InvalidOperationException("The equipment message has no handler.");
                }));
            }
            if (table.Find(PacketDirection.ClientToServer, "gcpReqInventoryUnequip") is { } unequip)
            {
                requestsBound = true;
                context.Effect(packets.Register(options.Protocol, unequip, async (session, packet, ct) =>
                {
                    var request = EquipmentRequestWire.ReadRequest(packetTablesOfRow.Value.Tables, packetTablesOfRow.Value.Build, packet.Payload.Span, false);
                    if (!await router.DispatchAsync(options.Protocol, session, request, ct).ConfigureAwait(false))
                        throw new InvalidOperationException("The equipment message has no handler.");
                }));
            }
            if (requestsBound)
            {
                context.Effect(context.Services.RegisterMany<IInboundMessageBinding<EquipmentChangeRequested>>(equipment));
                context.Effect(context.Services.RegisterMany<IMessageSender<EquipmentChangeReplied>>(equipment));
            }
            if (equipment.HasPresetReply
                && table.Find(PacketDirection.ServerToClient, "gspReqWeaponPreSetUpdate") is not null
                && table.Find(PacketDirection.ClientToServer, "gcpReqInventoryChangeWeaponPresetIdx") is { } presetChange)
            {
                context.Effect(context.Services.RegisterMany<IInboundMessageBinding<WeaponPresetChangeRequested>>(equipment));
                context.Effect(context.Services.RegisterMany<IMessageSender<WeaponPresetChangeReplied>>(equipment));
                context.Effect(packets.Register(options.Protocol, presetChange, async (session, packet, ct) =>
                {
                    var request = PresetWire.ReadChangeRequest(packetTablesOfRow.Value.Tables, packetTablesOfRow.Value.Build, packet.Payload.Span);
                    if (!await router.DispatchAsync(options.Protocol, session, request, ct).ConfigureAwait(false))
                        throw new InvalidOperationException("The weapon preset message has no handler.");
                }));
            }
        }
    }
}

/// <summary>Selects the game connection scope for the legacy message adapters.</summary>
public sealed class MessagesOptions
{
    /// <summary>
    /// Gets or sets whether this row binds <c>gcpReqLogin</c> and sends the login reply. Off while another row of
    /// the build still owns its login, so this row can carry the build's other features.
    /// </summary>
    public bool Login { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the row answers the kr-1256 family's main-menu probe (C2S 521, builders
    /// <c>sub_937560</c>/<c>sub_937920</c>, empty body) with the empty <c>gspSessionSignatureData</c> body. YAML
    /// key <c>main_menu_init</c>; off for builds whose table does not name the probe.
    /// </summary>
    public bool MainMenuInit { get; set; }

    /// <summary>
    /// Gets or sets whether to bind the wear and removal requests and their replies on a row without
    /// <see cref="EnablePurchases"/>, which binds them together with the bag. YAML key <c>equipment</c>.
    /// </summary>
    public bool Equipment { get; set; }

    /// <summary>
    /// Gets or sets whether to publish the equipped view and its preset bar on a row without
    /// <see cref="EnablePurchases"/>, which publishes them together with the bag. YAML key <c>equipment_view</c>.
    /// </summary>
    public bool EquipmentView { get; set; }

    /// <summary>Gets or sets whether to bind native center status and availability messages.</summary>
    public bool CenterDirectory { get; set; }

    /// <summary>
    /// Gets or sets whether to bind the native party packets (invitation, accept, leave, kick, leader handover and the
    /// party list) for the <c>party</c> row, on builds with the <c>party</c> capability in <c>packets/builds.yaml</c>.
    /// </summary>
    public bool Party { get; set; }

    /// <summary>
    /// Gets or sets whether to bind the clan game-connection messages (the three empty client requests in, the
    /// passport, mark list, guild info and attendance state out; <c>packets/clan.yaml</c>) for the <c>clan</c> row,
    /// on builds with the <c>clan</c> capability in <c>packets/builds.yaml</c>. YAML key <c>clan</c>.
    /// </summary>
    public bool Clan { get; set; }

    /// <summary>
    /// Gets or sets whether to bind the player menu's "view info" packets (<c>gcpReqFighterInfo</c> and
    /// <c>gspRepFighterInfo</c>, <c>packets/fighter-info.yaml</c>) for the <c>lobby</c> row's <c>fighter_info</c>, on
    /// builds with the <c>fighter_info</c> capability in <c>packets/builds.yaml</c>.
    /// </summary>
    public bool FighterInfo { get; set; }

    /// <summary>Gets or sets whether the profile enables verified inventory consumable requests and replies.</summary>
    public bool InventoryItemUse { get; set; }

    /// <summary>
    /// Gets or sets whether this dialect binds the gift box ("Present") packets to the shared gift messages. Needs the
    /// build's <c>present_box</c> capability and a shop row with <c>present_box</c> to answer them. YAML key
    /// <c>present_box</c>.
    /// </summary>
    public bool PresentBox { get; set; }

    /// <summary>Gets or sets whether this dialect publishes the reusable native server-notice sender.</summary>
    public bool ServerNotices { get; set; }

    /// <summary>
    /// Gets or sets whether this dialect publishes the native expiry notice (<c>gspCheckExpiredTimeResult</c>)
    /// sender and writes it after the menu opens when a login removed expired items. Needs the build's
    /// <c>expired_item_notice</c> capability. YAML key <c>expired_item_notices</c>.
    /// </summary>
    public bool ExpiredItemNotices { get; set; }
    /// <summary>
    /// Gets or sets whether to bind native character creation for a store-backed lobby, on builds whose
    /// <c>character_creation</c> capability in <c>packets/builds.yaml</c> is set; other builds keep it off.
    /// </summary>
    public bool CharacterCreation { get; set; }
    /// <summary>Gets or sets whether to bind the verified native creation requests for recording sessions.</summary>
    public bool CharacterCreationPreview { get; set; }

    /// <summary>Gets or sets the directory holding the client's creation packet structures.</summary>
    [Required]
    public string PacketsDirectory { get; set; } = "packets";
    /// <summary>Gets or sets whether to mount the selected client's verified purchase, inventory,
    /// equipment and weapon-preset adapters. Each request route is bound only where the build's
    /// opcode table names the packet and its reply.</summary>
    public bool EnablePurchases { get; set; }

    /// <summary>
    /// Gets or sets whether this row publishes the build's bag senders
    /// (<c>IMessageSender&lt;InventoryReplaced&gt;</c>, <c>IMessageSender&lt;InventorySlotsUpdated&gt;</c>
    /// and <c>IMessageSender&lt;InventoryPurchaseApplied&gt;</c>) on a row without
    /// <see cref="EnablePurchases"/>. The packets are resolved by their names and the publication order
    /// and envelope come from the build's <c>inventory</c> values in <c>packets/builds.yaml</c>. YAML key
    /// <c>inventory</c>.
    /// </summary>
    public bool Inventory { get; set; }

    /// <summary>
    /// Gets or sets whether the play-room codec owns <c>gcpReqGotoLobby</c> and answers it with
    /// the whole plaza entry burst. On, this row does not register its own one-packet reply, so
    /// the opcode is registered exactly once. It needs the builds' play-room row with
    /// <c>typed_play_rooms</c>. YAML key <c>typed_plaza_enter</c>.
    /// </summary>
    public bool TypedPlazaEnter { get; set; } = true;

    /// <summary>Gets or sets the maximum live connections retaining an inventory time origin.</summary>
    [Range(1, 65536)]
    public int MaxShopSessions { get; set; } = 512;

    /// <summary>Gets or sets the legacy game connection protocol, such as <c>kr-812/game</c>.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Protocol { get; set; } = "kr-812/game";
}
