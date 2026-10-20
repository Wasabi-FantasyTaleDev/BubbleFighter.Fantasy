using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Net;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Sessions;
using BubbleFighter.Fantasy.Equipment.Handlers;
using BubbleFighter.Fantasy.Equipment.Sessions;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Equipment;

/// <summary>Applies typed equipment changes and publishes each committed inventory and appearance snapshot.</summary>
[Plugin("equipment",
    Description = "Atomic worn selection with typed results, inventory and equipped-view updates.", 
    Requires = [typeof(IMessageRouter)],
    Optional = [typeof(IInventoryRepository), typeof(IInventoryPushService), typeof(IEquipmentSelection), typeof(IEquippedViewPush), typeof(IPlayerInventoryState)], OptionalMany = [typeof(IInboundMessageBinding<EquipmentChangeRequested>), typeof(IMessageSender<EquipmentChangeReplied>), typeof(IInboundMessageBinding<WeaponPresetChangeRequested>), typeof(IMessageSender<WeaponPresetChangeReplied>)])]
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
        var equippedItems = new EquippedItemScope(options.MaxTrackedConnections, options.MaxEquippedPerSession);
        context.Effect(equippedItems);
        context.Effect(context.Events.On<ConnectionClosedEvent>(e => equippedItems.Remove(e.Connection.Id)));
        var handlers = new EquipmentHandlers(equippedItems, inventory, push, context.Logger, senders[0], selection, equippedView, context.Events);
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
            var presets = new WeaponPresetChangeHandler(playerState, selection, equippedView, presetSenders[0], context.Logger);
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
