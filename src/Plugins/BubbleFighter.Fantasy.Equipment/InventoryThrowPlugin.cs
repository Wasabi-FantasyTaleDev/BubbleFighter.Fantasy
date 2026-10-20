using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Equipment.Handlers;
using BubbleFighter.Fantasy.Packets;

namespace BubbleFighter.Fantasy.Equipment;

/// <summary>Handles authenticated expired-item actions and republishes their resulting equipment selection.</summary>
[Plugin("inventory.throw", Description = "Owned expired-item removal and equipment synchronization.",
    Requires = [typeof(IMessageRouter), typeof(IInventoryItemRemoval), typeof(IEquipmentSelection), typeof(IInventoryPushService), typeof(IEquippedViewPush)],
    RequiresMany = [typeof(IInboundMessageBinding<InventoryThrowRequested>), typeof(IMessageSender<InventoryThrowReplied>)])]
public sealed class InventoryThrowPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = context.Config.Bind<InventoryThrowOptions>();
        if (string.IsNullOrWhiteSpace(options.Protocol)) throw new ConfigException("inventory.throw requires a protocol.");
        var tables = PacketTables.Load(options.PacketsDirectory);
        var build = tables.NearestBuild(context.Row.LineageOrBuild())
            ?? throw new ConfigException("inventory.throw requires a build named in the packet tables.");
        var excludeCategorySix = tables.CapabilityOf(build, "inventory_throw_excludes_category6");
        var bindings = context.Services.GetAll<IInboundMessageBinding<InventoryThrowRequested>>()
            .Count(binding => binding.Protocol == options.Protocol);
        var senders = context.Services.GetAll<IMessageSender<InventoryThrowReplied>>()
            .Where(sender => sender.Protocol == options.Protocol).ToArray();
        if (bindings != 1 || senders.Length != 1)
            throw new ConfigException($"inventory.throw requires one request binding and reply sender for '{options.Protocol}'.");
        var handler = new InventoryThrowHandler(context.Services.Get<IInventoryItemRemoval>(),
            context.Services.Get<IEquipmentSelection>(), context.Services.Get<IInventoryPushService>(),
            context.Services.Get<IEquippedViewPush>(), senders[0], context.Events, excludeCategorySix);
        context.Effect(context.Services.Get<IMessageRouter>().For(context)
            .On<InventoryThrowRequested>(options.Protocol, handler.HandleAsync));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Selects the connection protocol whose expired-item requests this row owns.</summary>
public sealed class InventoryThrowOptions
{
    /// <summary>Gets or sets the game protocol with the mounted typed inventory action codec.</summary>
    [Required]
    public string Protocol { get; set; } = "";

    /// <summary>Gets or sets the directory whose build capabilities define native discard restrictions.</summary>
    [Required]
    public string PacketsDirectory { get; set; } = "packets";
}
