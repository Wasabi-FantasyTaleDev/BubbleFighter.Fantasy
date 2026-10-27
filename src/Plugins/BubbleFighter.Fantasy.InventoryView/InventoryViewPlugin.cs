using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.InventoryView.Handlers;
using BubbleFighter.Fantasy.Packets;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InventoryView;

/// <summary>
/// Publishes authoritative local and remote equipment snapshots through typed senders.
/// An enabled row also logs the C2S view request, read through the packet tables; that
/// request has no proven reply. Disabled rows register nothing, and request-only rows
/// need no outbound codec.
/// </summary>
[Plugin("inventoryview",
    Description = "Equipment selection and typed view publication with a strict view request parser.", 
    Requires = [typeof(IPacketRouter), typeof(IOpcodeTableRegistry)],
    Optional = [typeof(IPlayerInventoryState), typeof(IInventoryRepository), typeof(BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore)],
    OptionalMany = [typeof(IOpcodeTable), typeof(IMessageSender<LocalEquipmentViewUpdated>), typeof(IMessageSender<RemoteEquipmentViewUpdated>)],
    ConditionalProvides = [typeof(IEquippedViewPush), typeof(IEquipmentSelection), typeof(IWeaponPresetPageSource)])]
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
        var protocol = options.Protocol;
        var table = context.Services.Get<IOpcodeTableRegistry>().ForProtocol(protocol);
        // Resolve required senders before registering effects; request-only rows need no S2C capability.
        var localSender = options.PublishEquippedViewPush ? RequireSender<LocalEquipmentViewUpdated>(context, protocol) : null;
        var remoteSender = options.PublishEquippedViewPush ? RequireSender<RemoteEquipmentViewUpdated>(context, protocol) : null;
        // Only a build whose client emits a proven view request registers a route: kr-1256's
        // builder 0x80DA80 has no packet label, while the legacy builds send nothing here.
        if (!string.IsNullOrEmpty(options.ViewRequest))
        {
            var tables = PacketTables.Load(options.PacketsDirectory);
            var build = tables.NearestBuild(context.Row.LineageOrBuild())
                ?? throw new ConfigException($"inventoryview names view request '{options.ViewRequest}', but no build of its lineage is named in '{options.PacketsDirectory}'.");
            // Item and quantity are u32 words in every family; the parameters field's shape (the
            // counted string list kr-1256 sub_80DA80 writes, one string on kr-812 sub_8B9490) stays
            // a table switch, so the contract pins the scalars and the parameters type as bind time.
            PacketLayoutContract.RequireFields(tables, build, "InventoryUseRequest", "Inventory view request",
                ("item_id", "u32"), ("quantity", "u32"));
            var parameters = tables.Resolve(build, "InventoryUseRequest").FirstOrDefault(field => field.Name == "parameters")
                ?? throw new ConfigException($"Inventory view request needs InventoryUseRequest.parameters for '{build}', but the table does not state it.");
            if (parameters.Type is not ("wstr" or "list<wstr>"))
                throw new ConfigException($"Inventory view request cannot read InventoryUseRequest.parameters as {parameters.Type} for '{build}'.");
            var handlers = new InventoryViewHandlers(context.Logger, tables, build);
            var request = table.C2S(options.ViewRequest);
            context.Effect(router.Register(protocol, request, handlers.HandleViewRequestAsync));
        }
        if (options.PublishEquippedViewPush)
        {
            // The persisted active preset is business state; codecs receive it without reading stores.
            var playerState = context.Services.TryGet<IPlayerInventoryState>(out var state) && state is not null
                ? state
                : null;
            if (options.PersistPresetPages && playerState is null)
            {
                throw new ConfigException(
                    "persist_preset_pages is on but no persistence row publishes IPlayerInventoryState; "
                    + "the materialized preset pages could not survive a restart, so the bar would revert to derived.");
            }
            IEquipmentSelection? selection = null;
            if (context.Services.TryGet<IInventoryRepository>(out var inventory) && inventory is not null)
            {
                IEquipmentSelection inner = options.Selection == "catalog_masks"
                    ? new EquipmentSelectionCatalogMasks(inventory, CatalogMasks(context, options))
                    : new EquipmentSelection(inventory, WearLocations(context, options));
                selection = new EquipmentSelectionPolicy(inner, context.Events, protocol, context.Logger);
                context.Effect(context.Services.Register<IEquipmentSelection>(selection));
                context.Effect(context.Events.Intercept<EquipmentSelectionReadRequested, IReadOnlyList<InventorySlotRecord>>(
                    (request, next, ct) => request.Protocol == protocol ? selection.ReadAsync(request.UserNum, ct) : next(ct)));
            }
            var push = new EquippedViewPush(context.Logger, localSender!, remoteSender!, playerState, selection, options.PersistPresetPages);
            context.Effect(context.Services.Register<IEquippedViewPush>(push));
            if (options.PersistPresetPages)
                context.Effect(context.Services.Register<IWeaponPresetPageSource>(push));
            context.Effect(context.Events.Intercept<EquipmentViewRequested, bool>(async (request, next, ct) =>
            {
                if (request.Protocol != protocol)
                    return await next(ct).ConfigureAwait(false);
                if (request.Remote)
                    await push.SendRemoteAsync(request.Session, request.UserNum, request.CharacterItemId, request.EquippedItemIds, ct).ConfigureAwait(false);
                else
                    await push.SendSelectionAsync(request.Session, request.UserNum, request.CharacterItemId, request.EquippedItemIds, ct).ConfigureAwait(false);
                return true;
            }));
            context.Effect(context.Events.Intercept<BubbleFighter.Fantasy.Contracts.Lobby.LoginEquipmentRequested, bool>(async (request, next, ct) =>
            {
                if (request.Protocol == protocol)
                    await push.SendSelectionAsync(request.Session, request.Login.UserNumber, request.CharacterItemId, request.Login.EquippedItemIds, ct).ConfigureAwait(false);
                return await next(ct).ConfigureAwait(false);
            }));
            context.Logger.LogInformation(
                "Inventory view row {Version} on {Protocol} (profile {Profile}) publishes IEquippedViewPush. Active preset slot source: {SlotSource}. Preset pages: {PresetPages}.",
                options.VersionMajor, protocol, options.ClientProfile,
                playerState is not null ? "published IPlayerInventoryState" : "default 0 (no persistence row)",
                options.PersistPresetPages ? "persisted (persist_preset_pages)" : "worn-derived");
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

    /// <summary>
    /// Loads the catalog-masks pair the same way the shop row reads its workbook: by client path through the build's
    /// clientdata row, or from a file path for local tools and tests. The shop workbook carries the location columns,
    /// the FighterConfig workbook the character names <c>allowChr</c>/<c>denyChr</c> resolve through.
    /// </summary>
    private static ItemCatalogMasks CatalogMasks(IPluginContext context, InventoryViewOptions options) =>
        ItemCatalogMasks.Load(
            Workbook(options.ItemLocationsClientPath, options.ItemLocationsPath, context, "item_locations_client_path", "item_locations_path"),
            Workbook(options.FighterConfigClientPath, options.FighterConfigPath, context, "fighter_config_client_path", "fighter_config_path"));

    private static BubbleFighter.Fantasy.Contracts.ClientData.WorkbookFile Workbook(
        string clientPath, string filePath, IPluginContext context, string clientKey, string fileKey)
    {
        if (clientPath.Length == 0)
        {
            if (filePath.Length == 0)
                throw new ConfigException($"selection catalog_masks requires one of {clientKey} and {fileKey}.");
            var file = new FileInfo(filePath);
            if (!file.Exists)
                throw new ConfigException($"{fileKey} '{filePath}' does not exist.");
            return new(file.FullName, file.Length, file.OpenRead);
        }
        if (filePath.Length > 0)
            throw new ConfigException($"Set at most one of {clientKey} and {fileKey}.");
        if (!context.Services.TryGet<BubbleFighter.Fantasy.Contracts.ClientData.IClientDataStore>(out var data) || data is null)
            throw new ConfigException($"{clientKey} requires a clientdata row for this build.");
        return BubbleFighter.Fantasy.Contracts.ClientData.WorkbookFile.FromBytes(clientPath, data.Read(clientPath));
    }
}
