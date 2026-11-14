using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Packets;
using BubbleFighter.Fantasy.Equipment.Data;
using BubbleFighter.Fantasy.Equipment.Handlers;
using BubbleFighter.Fantasy.Equipment.Selection;
using BubbleFighter.Fantasy.Equipment.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Equipment;

/// <summary>Applies typed equipment changes and publishes each committed inventory and appearance snapshot.</summary>
[Plugin("equipment", Category = "items", Names = ["en=Equipment", "zh-Hans=装备", "zh-Hant=裝備", "ko=장비"], 
    Description = "Atomic worn selection with typed results, inventory and equipped-view updates.",
    Requires = [typeof(IMessageRouter)],
    Optional = [typeof(IInventoryRepository), typeof(IInventoryPushService), typeof(IEquipmentSelection), typeof(IEquippedViewPush), typeof(IPlayerInventoryState), typeof(IWeaponPresetPageSource), typeof(IClientDataStore), typeof(IPetEntitlements), typeof(IEquipmentArticleResolver)], OptionalMany = [typeof(IInboundMessageBinding<EquipmentChangeRequested>), typeof(IMessageSender<EquipmentChangeReplied>), typeof(IInboundMessageBinding<WeaponPresetChangeRequested>), typeof(IMessageSender<WeaponPresetChangeReplied>), typeof(IMessageSender<InventoryReplaced>)])]
public sealed class EquipmentPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<EquipmentOptions>();
        if (!options.Enabled)
        {
            context.Logger.LogInformation(
                "Equipment row disabled by configuration; no typed request handler is registered on {Protocol}.",
                options.Protocol);
            return ValueTask.CompletedTask;
        }

        var router = context.Services.Get<IMessageRouter>();
        var bindings = context.Services.GetAll<IInboundMessageBinding<EquipmentChangeRequested>>()
            .Where(binding => binding.Protocol == options.Protocol).ToArray();
        if (bindings.Length != 1)
            throw new ConfigException($"Equipment requires exactly one {nameof(EquipmentChangeRequested)} binding for '{options.Protocol}'; found {bindings.Length}.");
        var senders = context.Services.GetAll<IMessageSender<EquipmentChangeReplied>>()
            .Where(sender => sender.Protocol == options.Protocol).ToArray();
        if (senders.Length != 1)
            throw new ConfigException($"Equipment requires exactly one {nameof(EquipmentChangeReplied)} sender for '{options.Protocol}'; found {senders.Length}.");
        IInventoryRepository? inventory = null;
        IInventoryPushService? push = null;
        if (options.PersistWornState)
        {
            if (!context.Services.TryGet<IInventoryRepository>(out var store) || store is null
                || !context.Services.TryGet<IInventoryPushService>(out var pushService) || pushService is null)
            {
                throw new InvalidOperationException(
                    "persist_worn_state is on but no persistence row publishing IInventoryRepository "
                    + "(persistence.mysql or persistence.memory) or no lobby row publishing "
                    + "IInventoryPushService (inventory_source: mysql) is active; mount both rows, "
                    + "or set persist_worn_state: false for an ack-only equipment row.");
            }

            inventory = store;
            push = pushService;
        }

        context.Services.TryGet<IEquipmentSelection>(out var selection);
        context.Services.TryGet<IEquippedViewPush>(out var equippedView);
        if (options.PersistWornState && (selection is null || equippedView is null))
            throw new InvalidOperationException("persist_worn_state requires IEquipmentSelection and IEquippedViewPush; enable the inventory-view provider so equipment changes cannot remain flag-only.");
        if (options.PetSharedSlot)
        {
            var tables = PacketTables.Load(options.PacketsDirectory);
            var build = tables.NearestBuild(context.Row.LineageOrBuild())
                ?? throw new ConfigException("pet_shared_slot requires a client build declared in the packet tables.");
            if (!tables.CapabilityOf(build, "pet_equipment"))
                throw new ConfigException($"pet_shared_slot is unavailable: the native pet equipment path is not established for '{build}'.");
            // The rule is a default-off capability of this row: the pet identities come from
            // the build's own workbook, and a missing store or unreadable workbook fails the
            // row instead of arming a rule with no pets in it.
            if (inventory is null)
                throw new ConfigException("pet_shared_slot requires persist_worn_state: an ack-only row never consults the selection the rule guards.");
            if (!context.Services.TryGet<IClientDataStore>(out var clientData) || clientData is null)
                throw new ConfigException($"pet_shared_slot is on but no clientdata row is active; '{options.PetCatalogClientPath}' could not be read.");
            var pets = PetItemCatalogLoader.Load(
                WorkbookFile.FromStore(clientData, options.PetCatalogClientPath),
                options.MaxPetCatalogBytes, "equipment.max_pet_catalog_bytes");
            var petPolicy = new PetSharedSlotPolicy(pets, inventory, options.Protocol, context.Logger);
            var petItems = Array.AsReadOnly(pets.Order().ToArray());
            var petGroup = new InventoryWearGroup(petItems[0], petItems[^1], petItems[0])
            {
                ItemIds = petItems,
                RejectWhenOccupied = true,
            };
            context.Effect(context.Events.Intercept<EquipmentWearGroupsRequested, IReadOnlyList<InventoryWearGroup>>(
                async (request, next, ct) =>
                {
                    var groups = await next(ct).ConfigureAwait(false);
                    if (request.Protocol != options.Protocol) return groups;
                    if (groups is null || groups.Count >= InventoryLimits.MaxWearGroups)
                        throw new ConfigException("The shared pet slot exceeds the equipment position limit.");
                    return Array.AsReadOnly(groups.Append(petGroup).ToArray());
                }));
            // One middleware of the shared EquipmentSelectionApplying waterfall: the denial
            // rides the selection's existing refusal path, and any other selection policy
            // (a character mask, for instance) stays in the chain untouched.
            context.Effect(context.Events.Intercept<EquipmentSelectionApplying, EquipmentSelectionDecision>(petPolicy.ApplyAsync));
            context.Logger.LogInformation(
                "Pet shared slot rule is live on {Protocol}: {Pets} function-10001 items share one worn slot.",
                options.Protocol, pets.Count);
        }
        var equippedItems = new EquippedItemScope(options.MaxTrackedConnections, options.MaxEquippedPerSession);
        context.Effect(equippedItems);
        context.Effect(context.Events.On<ConnectionClosedEvent>(e => equippedItems.Remove(e.Connection.Id)));
        context.Services.TryGet<IPetEntitlements>(out var petEntitlements);
        context.Services.TryGet<IEquipmentArticleResolver>(out var articles);
        var inventorySender = context.Services.GetAll<IMessageSender<InventoryReplaced>>()
            .SingleOrDefault(sender => sender.Protocol == options.Protocol);
        if ((petEntitlements is not null || articles is not null) && options.PersistWornState && inventorySender is null)
            throw new ConfigException("Article equipment requires the protocol's complete inventory sender.");
        var handlers = new EquipmentHandlers(equippedItems, inventory, push, context.Logger, senders[0], selection, equippedView,
            context.Events, petEntitlements, inventorySender, articles);
        var protocol = options.Protocol;
        context.Effect(router.For(context).On<EquipmentChangeRequested>(protocol, handlers.HandleAsync));

        // A build whose codec row names the preset switch gets the full reply path; a codec
        // missing either direction is a config error, never a silently dropped request.
        var presetBindings = context.Services.GetAll<IInboundMessageBinding<WeaponPresetChangeRequested>>()
            .Where(binding => binding.Protocol == protocol).ToArray();
        var presetSenders = context.Services.GetAll<IMessageSender<WeaponPresetChangeReplied>>()
            .Where(sender => sender.Protocol == protocol).ToArray();
        if (presetBindings.Length != presetSenders.Length || presetBindings.Length > 1)
        {
            throw new ConfigException(
                $"Weapon preset codecs for '{protocol}' are half-mounted: {presetBindings.Length} request binding(s) "
                + $"and {presetSenders.Length} reply sender(s); mount both or neither.");
        }
        if (presetBindings.Length == 1)
        {
            if (!context.Services.TryGet<IPlayerInventoryState>(out var playerState) || playerState is null)
            {
                throw new InvalidOperationException(
                    "The weapon preset codec is mounted but no persistence row publishes IPlayerInventoryState; "
                    + "the granted slot could not persist, so a switch would revert on the next view push.");
            }
            context.Services.TryGet<IWeaponPresetPageSource>(out var presetPages);
            var presets = new WeaponPresetChangeHandler(playerState, selection, equippedView, presetSenders[0], context.Logger, presetPages, handlers);
            context.Effect(router.For(context).On<WeaponPresetChangeRequested>(protocol, presets.HandleAsync));
            context.Logger.LogInformation(
                "Weapon preset switching is live on {Protocol}: requests persist the granted slot and republish the view.",
                protocol);
        }
        context.Logger.LogInformation(
            "Equipment dialect {Version} on {Protocol} (profile {Profile}); max worn keys per connection {Max}, tracked connections {Tracked}, worn state {Worn}.",
            options.VersionMajor, protocol, options.ClientProfile, options.MaxEquippedPerSession,
            options.MaxTrackedConnections,
            options.PersistWornState
                ? "persisted exclusive selection, inventory flags and equipped view"
                : "ack-only (persist_worn_state: false)");
        return ValueTask.CompletedTask;
    }

    /// <summary>The logged-in player's id, or 0 when the session has no player.</summary>
    /// <param name="session">The live session.</param>
    public static long TryGetUserNum(ISession session) =>
        session.TryFeature<IPlayerSession>(out var player) ? player.Identity.UserNum : 0;
}
