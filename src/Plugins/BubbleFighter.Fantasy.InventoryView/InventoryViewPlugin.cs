using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InventoryView.Handlers;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InventoryView;

/// <summary>
/// Publishes authoritative local and remote equipment snapshots through typed senders.
/// An enabled row also logs the strict C2S view request; that request has no proven reply.
/// Disabled rows register nothing, and request-only rows need no outbound codec.
/// </summary>
[Plugin("inventoryview",
    Description = "Equipment selection and typed view publication with a strict view request parser.", 
    Requires = [typeof(IPacketRouter), typeof(IOpcodeTableRegistry)],
    Optional = [typeof(IPlayerInventoryState), typeof(IInventoryRepository), typeof(BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore)], OptionalMany = [typeof(IOpcodeTable), typeof(IMessageSender<LocalEquipmentViewUpdated>), typeof(IMessageSender<RemoteEquipmentViewUpdated>)],
    ConditionalProvides = [typeof(IEquippedViewPush), typeof(IEquipmentSelection)])]
public sealed class InventoryViewPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<InventoryViewOptions>();
        if (!options.Enabled)
        {
            context.Logger.LogInformation(
                "Inventory view row disabled by configuration; no view is sent and no request is registered on {Protocol}.",
                options.Protocol);
            return ValueTask.CompletedTask;
        }

        var router = context.Services.Get<IPacketRouter>();
        var handlers = new InventoryViewHandlers(context.Logger);
        var protocol = options.Protocol;
        var table = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(protocol);
        // Resolve required senders before registering effects; request-only rows need no S2C capability.
        var localSender = options.PublishEquippedViewPush ? RequireSender<LocalEquipmentViewUpdated>(context, protocol) : null;
        var remoteSender = options.PublishEquippedViewPush ? RequireSender<RemoteEquipmentViewUpdated>(context, protocol) : null;
        // Only a build whose client emits a proven view request registers a route: kr-1256's
        // builder 0x80DA80 has no packet label, while the legacy builds send nothing here.
        if (!string.IsNullOrEmpty(options.ViewRequest))
        {
            var request = table.C2S(options.ViewRequest);
            context.Effect(router.Register(protocol, request, handlers.HandleViewRequestAsync));
        }
        if (options.PublishEquippedViewPush)
        {
            // The persisted active preset is business state; codecs receive it without reading stores.
            var playerState = context.Services.TryGet<IPlayerInventoryState>(out var state) && state is not null
                ? state
                : null;
            IEquipmentSelection? selection = null;
            if (context.Services.TryGet<IInventoryRepository>(out var inventory) && inventory is not null)
            {
                selection = new EquipmentSelectionPolicy(new EquipmentSelection(inventory, WearLocations(context, options)), context.Events, protocol, context.Logger);
                context.Effect(context.Services.Register<IEquipmentSelection>(selection));
                context.Effect(context.Events.Intercept<EquipmentSelectionReadRequested, IReadOnlyList<InventorySlotRecord>>(
                    (request, next, ct) => request.Protocol == protocol ? selection.ReadAsync(request.UserNum, ct) : next(ct)));
            }
            var push = new EquippedViewPush(context.Logger, localSender!, remoteSender!, playerState, selection);
            context.Effect(context.Services.Register<IEquippedViewPush>(push));
            context.Effect(context.Events.Intercept<BubbleFighter.Fantasy.Contracts.Lobby.LoginEquipmentRequested, bool>(async (request, next, ct) =>
            {
                if (request.Protocol == protocol)
                    await push.SendSelectionAsync(request.Session, request.Login.UserNumber, request.CharacterItemId, request.Login.EquippedItemIds, ct).ConfigureAwait(false);
                return await next(ct).ConfigureAwait(false);
            }));
            context.Logger.LogInformation(
                "Inventory view row {Version} on {Protocol} (profile {Profile}) publishes IEquippedViewPush. Active preset slot source: {SlotSource}.",
                options.VersionMajor, protocol, options.ClientProfile,
                playerState is not null ? "published IPlayerInventoryState" : "default 0 (no persistence row)");
        }
        else
        {
            context.Logger.LogInformation(
                "Inventory view row {Version} on {Protocol} (profile {Profile}); view requests are logged without publishing equipment.",
                options.VersionMajor, protocol, options.ClientProfile);
        }
        return ValueTask.CompletedTask;
    }

    private static IMessageSender<T> RequireSender<T>(IPluginContext context, string protocol) where T : IMessage
    {
        var senders = context.Services.GetAll<IMessageSender<T>>().Where(sender => sender.Protocol == protocol).ToArray();
        return senders.Length == 1 ? senders[0]
            : throw new ConfigException($"Inventory view requires exactly one {typeof(T).Name} sender for '{protocol}'; found {senders.Length}.");
    }

    // The workbook comes from the build's client data by client path, or from a file path for local tools and tests.
    private static InventoryWearGroup[] WearLocations(IPluginContext context, InventoryViewOptions options)
    {
        if (options.ItemLocationsClientPath.Length == 0)
            return ItemWearLocations.Load(options.ItemLocationsPath, options.ClothingCategory, options.AccessoryCategory);
        if (options.ItemLocationsPath.Length > 0)
            throw new ConfigException("Set at most one of item_locations_client_path and item_locations_path.");
        if (!context.Services.TryGet<BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore>(out var data) || data is null)
            throw new ConfigException("item_locations_client_path requires a clientdata row for this build.");
        using var stream = new MemoryStream(data.Read(options.ItemLocationsClientPath), writable: false);
        return ItemWearLocations.Load(stream, options.ClothingCategory, options.AccessoryCategory);
    }
}
