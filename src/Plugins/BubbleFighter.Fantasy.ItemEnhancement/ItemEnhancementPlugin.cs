using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Party;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Shop;
using BubbleFighter.Fantasy.ItemEnhancement.Data;

namespace BubbleFighter.Fantasy.ItemEnhancement;

/// <summary>Owns the shared enhancement economy and publishes committed inventory before its native result.</summary>
[Plugin("item-enhancement", Category = "items", Names = ["en=Item enhancement", "zh-Hans=道具强化", "zh-Hant=道具強化", "ko=아이템 강화"], Description = "Atomic enhancement recipes over native owned Articles.",
    Optional = [typeof(IClientDataStore), typeof(IItemEnhancementStore), typeof(IInventoryBuildScope),
        typeof(IEquipmentArticleResolver), typeof(IMessageRouter), typeof(IPetEntitlements), typeof(IItemModificationStore),
        typeof(ILaboratoryInventoryStore)],
    OptionalMany = [typeof(IInboundMessageBinding<LabOperationRequested>), typeof(IMessageSender<LabOperationRefused>),
        typeof(IMessageSender<LabEnchantCompleted>), typeof(IMessageSender<InventoryReplaced>),
        typeof(IMessageSender<LabModificationCompleted>), typeof(IMessageSender<ShopBalanceUpdated>),
        typeof(IMessageSender<LabConversionCompleted>), typeof(IMessageSender<LabAdvancementCompleted>), typeof(IMessageSender<FavoriteWeaponGradeChanged>),
        typeof(IInboundMessageBinding<ScienceLabConfigurationRequested>), typeof(IMessageSender<ScienceLabConfigurationPublished>)])]
public sealed class ItemEnhancementPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<ItemEnhancementOptions>();
        if (!options.Enabled) return ValueTask.CompletedTask;
        var scope = context.Services.Get<IInventoryBuildScope>().Scope;
        var catalog = EnhancementSetup.Catalog(context, options, scope,
            includeSets: options.Conversion is { Enabled: true, SetCardQuantity: not null } || options.Advancement.Enabled);
        EnhancementSetup.RequireRecipe(catalog, options, scope);
        if (context.Services.GetAll<IInboundMessageBinding<LabOperationRequested>>().Count(binding => binding.Protocol == options.Protocol) != 1)
            throw new ConfigException("Item enhancement requires exactly one audited laboratory request binding.");
        if (context.Services.GetAll<IInboundMessageBinding<ScienceLabConfigurationRequested>>().Count(binding => binding.Protocol == options.Protocol) != 1)
            throw new ConfigException("Item enhancement requires exactly one audited laboratory configuration binding.");
        var configuration = Sender<ScienceLabConfigurationPublished>(context, options.Protocol);
        context.Services.TryGet<IPetEntitlements>(out var pets);
        var favorite = options.Favorite.Enabled
            ? FavoriteWeaponCatalog.Load(context.Services.Get<IClientDataStore>(), options.Favorite.WorkbookPath, options.MaxWorkbookBytes) : null;
        var awards = favorite is null ? null
            : new FavoriteAwards(favorite, Sender<FavoriteWeaponGradeChanged>(context, options.Protocol), context.Events, options.Protocol, scope.Key);
        var modificationPolicy = options.Modification.Enabled ? new ModificationPolicy(options.Modification, catalog, scope) : null;
        var modification = modificationPolicy is null ? null : new ModificationRuntime(options.Modification, modificationPolicy, catalog,
            context.Services.Get<IItemModificationStore>(), context.Services.Get<IItemEnhancementStore>(),
            context.Services.Get<IEquipmentArticleResolver>(), scope, Sender<LabOperationRefused>(context, options.Protocol),
            Sender<LabModificationCompleted>(context, options.Protocol), Sender<InventoryReplaced>(context, options.Protocol),
            Sender<ShopBalanceUpdated>(context, options.Protocol), TimeProvider.System,
            System.Security.Cryptography.RandomNumberGenerator.GetInt32, pets, favorite, awards);
        var conversion = options.Conversion.Enabled ? Conversion(context, options, catalog, scope, pets) : null;
        var advancementPolicy = options.Advancement.Enabled ? new AdvancementPolicy(options.Advancement, catalog, scope) : null;
        var advancement = advancementPolicy is null ? null : new AdvancementRuntime(advancementPolicy, catalog,
            context.Services.Get<IItemEnhancementStore>(), context.Services.Get<IEquipmentArticleResolver>(), scope,
            Sender<LabOperationRefused>(context, options.Protocol), Sender<LabAdvancementCompleted>(context, options.Protocol),
            Sender<InventoryReplaced>(context, options.Protocol), TimeProvider.System, pets);
        var runtime = new EnhancementRuntime(options, catalog, context.Services.Get<IItemEnhancementStore>(),
            context.Services.Get<IEquipmentArticleResolver>(), scope, Sender<LabOperationRefused>(context, options.Protocol),
            Sender<LabEnchantCompleted>(context, options.Protocol), Sender<InventoryReplaced>(context, options.Protocol),
            TimeProvider.System, () => Random.Shared.Next(10000), pets, modification, conversion, favorite, advancement, awards);
        context.Effect(runtime);
        var routes = context.Services.Get<IMessageRouter>().For(context);
        context.Effect(routes.On<LabOperationRequested>(options.Protocol, runtime.HandleAsync));
        context.Effect(routes.On<ScienceLabConfigurationRequested>(options.Protocol, (session, request, ct) =>
        {
            if (!session.TryFeature<BubbleFighter.Fantasy.Contracts.Sessions.IPlayerSession>(out _))
                throw new InvalidOperationException("Laboratory configuration requires an authenticated player.");
            return configuration.SendAsync(session, new([options.CardItemId, .. options.KitItemIds], options.AdditiveItemId)
            {
                Modification = modificationPolicy?.Configuration,
                Conversion = conversion is null ? null : new(options.Conversion.CardItemId!.Value, options.Conversion.MinimumDays!.Value),
                Advancement = advancementPolicy?.Configuration,
            }, ct);
        }));
        return ValueTask.CompletedTask;
    }

    private static ConversionRuntime Conversion(IPluginContext context, ItemEnhancementOptions options,
        Data.EnhancementCatalog catalog, InventoryLoadoutScope scope, IPetEntitlements? pets)
    {
        var card = options.Conversion.CardItemId!.Value;
        // The card is a counted material and never itself a conversion target.
        if (!scope.Supports(card) || !catalog.Items.TryGetValue(card, out var definition)
            || definition.DurationType != "count" || definition.IsUnlimited)
            throw new ConfigException($"Conversion card {card} must be a counted catalog material of this build.");
        if (!catalog.Items.Values.Any(item => item.ConversionRequiresArticle && scope.Supports(item.Id)))
            throw new ConfigException("Conversion requires at least one native conversion target in this build's catalog.");
        if (options.Conversion.SetCardQuantity is not null && catalog.Sets.Count == 0)
            throw new ConfigException("Conversion set_card_quantity requires native sets in this build's catalog.");
        return new(options.Conversion, catalog, context.Services.Get<ILaboratoryInventoryStore>(),
            context.Services.Get<IEquipmentArticleResolver>(), scope, Sender<LabOperationRefused>(context, options.Protocol),
            Sender<LabConversionCompleted>(context, options.Protocol), Sender<InventoryReplaced>(context, options.Protocol),
            TimeProvider.System, pets);
    }

    private static IMessageSender<T> Sender<T>(IPluginContext context, string protocol) where T : IMessage
    {
        var senders = context.Services.GetAll<IMessageSender<T>>().Where(sender => sender.Protocol == protocol).Take(2).ToArray();
        return senders.Length == 1 ? senders[0]
            : throw new ConfigException($"Item enhancement requires exactly one {typeof(T).Name} sender for '{protocol}'.");
    }
}
