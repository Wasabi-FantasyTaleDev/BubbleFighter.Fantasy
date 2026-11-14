using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Lobby;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Players;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Rooms;
using BubbleFighter.Fantasy.Pets.Data;
using BubbleFighter.Fantasy.Pets.Rewards;
using BubbleFighter.Fantasy.Pets.Mileage;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.Pets;

/// <summary>Owns pet feeding, growth and ability mutations for one native client catalog.</summary>
[Plugin("pets", Category = "items", Names = ["en=Pets", "zh-Hans=宠物", "zh-Hant=寵物", "ko=펫"], Description = "Native pet growth and abilities with atomic consumable persistence.",
    Optional = [typeof(IClientDataStore), typeof(IInventoryLoadoutStore), typeof(IPetMutationStore), typeof(IPetEntitlements), typeof(IEquipmentArticleResolver), typeof(IInventoryBuildScope), typeof(IEquippedViewPush), typeof(IMessageRouter), typeof(IOnlinePlayers), typeof(IGameText)],
    OptionalMany = [typeof(IInboundMessageBinding<InventoryItemUseRequested>), typeof(IMessageSender<InventoryItemUseReplied>),
        typeof(IMessageSender<InventoryReplaced>), typeof(IMessageSender<PetEvolved>),
        typeof(IPreparedRoundRewards), typeof(IPetClientConfigurationSink), typeof(IInboundMessageBinding<PetLevelUpRequested>), typeof(IMessageSender<PetLevelUpReplied>),
        typeof(IInboundMessageBinding<PetAbilityChangeRequested>), typeof(IMessageSender<PetAbilityChangeReplied>),
        typeof(IRoomPolicyHost), typeof(IMessageSender<ServerNotice>), typeof(IMessageSender<RoomChatReceived>),
        typeof(IMessageSender<EquipmentChangeReplied>)])]
public sealed class PetsPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<PetsOptions>();
        if (!options.Enabled) return;
        var clientData = context.Services.Get<IClientDataStore>();
        var catalog = PetCatalogLoader.Load(clientData, options.CatalogPath,
            options.MaxWorkbookBytes, options.MaxCatalogRows, options.MaxCatalogColumns, options.MaxCellCharacters);
        if (options.InitialFullness > catalog.HungerMaximum)
            throw new ConfigException("initial_fullness exceeds this client's native hunger maximum.");
        var router = context.Services.Get<IMessageRouter>();
        if (!router.Handlers.Any(handler => handler.Protocol == options.Protocol && handler.MessageType == typeof(InventoryItemUseRequested)))
            throw new ConfigException("The pets row must activate below an inventory-use handler, such as the lobby row.");
        RequireBinding<InventoryItemUseRequested>(context, options.Protocol);
        var rules = new PetRules(catalog, options, Random.Shared);
        var scope = context.Services.Get<IInventoryBuildScope>().Scope;
        var entitlements = context.Services.Get<IPetEntitlements>();
        if (scope.Key != options.BuildKey || entitlements.BuildKey != options.BuildKey
            || scope.SupportedItemIds.Any(item => !catalog.Items.ContainsKey(item)))
            throw new ConfigException("The pets and pet entitlement rows must select the same build key.");
        var mileage = PetMileagePolicy.Create(options, catalog, scope);
        if (CreateClientConfiguration(options, catalog, scope, clientData) is { } clientConfiguration)
            RegisterClientConfiguration(context, options.Protocol, clientConfiguration);
        context.Services.TryGet<IEquipmentArticleResolver>(out var articles);
        var runtime = new PetRuntime(catalog, options, rules, context.Services.Get<IInventoryLoadoutStore>(),
            context.Services.Get<IPetMutationStore>(), scope, Sender<InventoryItemUseReplied>(context, options.Protocol),
            Sender<InventoryReplaced>(context, options.Protocol), Sender<PetEvolved>(context, options.Protocol),
            context.Services.Get<IEquippedViewPush>(), TimeProvider.System, entitlements, mileage, articles);
        var projection = new PetPresentation(catalog, options, TimeProvider.System);
        var equipmentPhase = new PetEquipmentPhasePolicy(options.Protocol, context.Services.Get<IOnlinePlayers>(), entitlements);
        context.Effect(context.Events.Intercept<EquipmentSelectionApplying, EquipmentSelectionDecision>(equipmentPhase.ApplyAsync));
        PetRewardHook.Register(context, catalog, options.Protocol, options.Rewards);
        context.Effect(context.Events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>(async (request, next, ct) =>
        {
            if (request.Protocol == options.Protocol && projection.Project(request.Slot) is { } value) return value;
            return await next(ct).ConfigureAwait(false);
        }));
        var routes = router.For(context);
        await PetRoomMutationRoutes.RegisterAsync(context, options, catalog, entitlements, routes).ConfigureAwait(false);
        context.Effect(routes.Use<InventoryItemUseRequested>(options.Protocol, async (session, request, next, ct) =>
        {
            if (!await runtime.UseAsync(session, request, ct).ConfigureAwait(false))
                await next(session, request, ct).ConfigureAwait(false);
        }));
        if (options.ManualLevelUp)
        {
            RequireBinding<PetLevelUpRequested>(context, options.Protocol);
            var replies = Sender<PetLevelUpReplied>(context, options.Protocol);
            context.Effect(routes.On<PetLevelUpRequested>(options.Protocol, (session, request, ct) => runtime.LevelUpAsync(session, request, replies, ct)));
        }
        var changes = context.Services.GetAll<IInboundMessageBinding<PetAbilityChangeRequested>>()
            .Where(binding => binding.Protocol == options.Protocol).ToArray();
        if (changes.Length > 1) throw new ConfigException("Pet ability change has multiple native bindings for this protocol.");
        if (changes.Length == 1)
        {
            var replies = Sender<PetAbilityChangeReplied>(context, options.Protocol);
            context.Effect(routes.On<PetAbilityChangeRequested>(options.Protocol, (session, request, ct) => runtime.ChangeAsync(session, request, replies, ct)));
        }
        context.Logger.LogInformation("Pet system for {Protocol} loaded {Pets} pets and {Attributes} native attribute definitions.",
            options.Protocol, catalog.Items.Values.Count(item => item.Function == PetCatalog.PetFunction), catalog.Skills.Count);
    }

    /// <summary>Validates the selected native cooldown source and returns only settings that require dialect publication.</summary>
    internal static PetClientConfiguration? CreateClientConfiguration(PetsOptions options, PetCatalog catalog,
        InventoryLoadoutScope scope, IClientDataStore? clientData = null)
    {
        var errors = options.ValidateClientConfiguration(requireSettings: true).ToArray();
        if (errors.Length != 0) throw new ConfigException(string.Join(" ", errors.Select(error => error.ErrorMessage)));
        if (options.ClientConfigurationSource == PetClientConfigurationSource.None) return null;
        if (options.ClientConfigurationSource == PetClientConfigurationSource.NativePetConfig)
        {
            var nativeSeconds = PetNativeConfiguration.ReadFeedCooldownSeconds(
                clientData ?? throw new ConfigException("NativePetConfig requires the configured client data store."),
                options.NativePetConfigPath!, options.MaxWorkbookBytes, options.MaxCatalogRows,
                options.MaxCatalogColumns, options.MaxCellCharacters);
            if (options.FeedCooldownSeconds != nativeSeconds)
                throw new ConfigException($"feed_cooldown_seconds must match the native PetConfig duration of {nativeSeconds} seconds.");
            return null;
        }
        // cn-774 sub_E567E0 consults category 14's cooldown for ordinary feeding independently of skill resets.
        var maximum = options.Mileage.DisplayMaximum!.Value;
        if (catalog.Items.TryGetValue(PetMileagePolicy.BalanceItemId, out var balance)
            && (balance.Function != 1114 || balance.DurationType != "count" || balance.MaximumDuration is not > 0
                || maximum > balance.MaximumDuration.Value || !scope.Supports(balance.ItemId)))
            throw new ConfigException("Pet mileage display maximum disagrees with the native balance catalog.");
        return new(maximum, options.FeedCooldownSeconds
            ?? throw new ConfigException("Native pet configuration requires explicit feed_cooldown_seconds."));
    }

    /// <summary>Validates the active dialect before publishing one immutable, effect-owned configuration provider.</summary>
    internal static void RegisterClientConfiguration(IPluginContext context, string protocol, PetClientConfiguration configuration)
    {
        var sinks = context.Services.GetAll<IPetClientConfigurationSink>()
            .Where(sink => sink.Protocol == protocol).ToArray();
        if (sinks.Length != 1)
            throw new ConfigException("Native pet feeding requires exactly one audited client-configuration route for its protocol.");
        sinks[0].Validate(configuration);
        context.Effect(context.Events.Intercept<PetClientConfigurationRequested, PetClientConfiguration?>((request, next, ct) =>
            request.Protocol == protocol ? ValueTask.FromResult<PetClientConfiguration?>(configuration) : next(ct)));
    }

    private static IMessageSender<T> Sender<T>(IPluginContext context, string protocol) where T : IMessage =>
        context.Services.GetAll<IMessageSender<T>>().SingleOrDefault(sender => sender.Protocol == protocol)
            ?? throw new ConfigException($"Pet system requires a {typeof(T).Name} sender for '{protocol}'.");

    private static void RequireBinding<T>(IPluginContext context, string protocol) where T : IMessage
    {
        if (context.Services.GetAll<IInboundMessageBinding<T>>().Count(binding => binding.Protocol == protocol) != 1)
            throw new ConfigException($"Pet system requires exactly one {typeof(T).Name} binding for '{protocol}'.");
    }
}
