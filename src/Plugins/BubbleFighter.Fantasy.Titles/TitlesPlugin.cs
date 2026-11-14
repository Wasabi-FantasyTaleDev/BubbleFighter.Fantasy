using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Abstractions.Protocol;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.Contracts.Presents;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Contracts.Progress;
using BubbleFighter.Fantasy.Contracts.Titles;
using BubbleFighter.Fantasy.Titles.Data;

namespace BubbleFighter.Fantasy.Titles;

/// <summary>Owns shared title exchange, medal information and equipment routes over a native catalogue.</summary>
[Plugin("titles", Category = "gameplay", Names = ["en=Titles and medals", "zh-Hans=称号与勋章", "zh-Hant=稱號與勳章", "ko=칭호와 훈장"], Description = "Native title medal exchange and persistent worn state.",
    Optional = [typeof(IClientDataStore), typeof(ITitleMedalStore), typeof(IInventoryLoadoutStore),
        typeof(IInventoryBuildScope), typeof(IEquipmentSelection), typeof(IEquippedViewPush), typeof(IMessageRouter),
        typeof(IPetEntitlements), typeof(IEquipmentArticleResolver), typeof(IPlayerProgressRepository)],
    OptionalMany = [typeof(IInboundMessageBinding<TitleMakeRequested>), typeof(IInboundMessageBinding<TitleSetRequested>),
        typeof(IInboundMessageBinding<MedalInfoRequested>), typeof(IMessageSender<TitleMakeReplied>),
        typeof(IMessageSender<TitleSetReplied>), typeof(IMessageSender<MedalInfoReplied>), typeof(IMessageSender<InventoryReplaced>),
        typeof(IFavoriteWeaponProgress), typeof(IMessageSender<TitleProgressNotified>)])]
public sealed class TitlesPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<TitlesOptions>();
        if (!options.Enabled) return ValueTask.CompletedTask;
        var scope = context.Services.Get<IInventoryBuildScope>().Scope;
        if (scope.Key != options.BuildKey) throw new ConfigException("Title and inventory rows must use the same canonical build key.");
        var catalog = TitleCatalog.Load(context.Services.Get<IClientDataStore>(), options.TitleCatalogPath,
            options.ItemCatalogPath, Enum.Parse<TitleCatalogSchema>(options.CatalogSchema),
            options.MaxWorkbookBytes, options.MaxCatalogRows, options.MaxCatalogColumns, options.MaxCellCharacters, options.BonusMedalQuantity,
            options.FavoriteTitles);
        if (catalog.Titles.Count == 0 || catalog.Titles.Values.Any(title => !scope.Supports(title.GrantTerm.ItemId)
                || title.BonusGrants.Any(term => !scope.Supports(term.ItemId))))
            throw new ConfigException("The native title stock grants must belong to the active inventory catalogue.");
        RequireBinding<TitleMakeRequested>(context, options.Protocol);
        RequireBinding<TitleSetRequested>(context, options.Protocol);
        RequireBinding<MedalInfoRequested>(context, options.Protocol);
        IFavoriteWeaponProgress? favorite = null;
        if (options.FavoriteTitles)
        {
            if (!catalog.Titles.Values.Any(title => title.FavoriteMasters is not null))
                throw new ConfigException("favorite_titles requires Favorite titles with a supported condition in the native catalogue.");
            var readers = context.Services.GetAll<IFavoriteWeaponProgress>().Where(reader => reader.Protocol == options.Protocol).Take(2).ToArray();
            favorite = readers.Length == 1 ? readers[0]
                : throw new ConfigException($"Favorite titles require exactly one Favorite weapon progress reader for '{options.Protocol}'.");
        }
        context.Services.TryGet<IPetEntitlements>(out var pets);
        context.Services.TryGet<IEquipmentArticleResolver>(out var articles);
        var runtime = new TitleRuntime(options, catalog, context.Services.Get<ITitleMedalStore>(),
            context.Services.Get<IInventoryLoadoutStore>(), scope, context.Services.Get<IEquipmentSelection>(),
            context.Services.Get<IEquippedViewPush>(), context.Events,
            Sender<InventoryReplaced>(context, options.Protocol), Sender<TitleMakeReplied>(context, options.Protocol),
            Sender<TitleSetReplied>(context, options.Protocol), Sender<MedalInfoReplied>(context, options.Protocol),
            TimeProvider.System, pets, articles, favorite,
            options.FavoriteTitles ? Sender<TitleProgressNotified>(context, options.Protocol) : null);
        if (options.RoundMedalsEnabled)
        {
            if (context.Services.Get<IPlayerProgressRepository>() is not IRoundTitleMedalSettlementRepository)
                throw new ConfigException("Round medals require atomic title-medal settlement support from the progress provider.");
            var policy = new TitleRoundMedals(options, catalog);
            context.Effect(context.Events.Intercept<RoundTitleMedalsPreparing, TitleMedalGrantCommand?>(async (request, next, ct) =>
            {
                var prior = await next(ct).ConfigureAwait(false);
                if (request.Protocol != options.Protocol) return prior;
                if (prior is not null) throw new ConfigException("A game protocol has more than one round medal policy.");
                return policy.Prepare(request);
            }));
        }
        context.Effect(runtime);
        var routes = context.Services.Get<IMessageRouter>().For(context);
        context.Effect(routes.On<TitleMakeRequested>(options.Protocol, runtime.HandleMakeAsync));
        context.Effect(routes.On<TitleSetRequested>(options.Protocol, runtime.HandleSetAsync));
        context.Effect(routes.On<MedalInfoRequested>(options.Protocol, runtime.HandleMedalsAsync));
        if (options.FavoriteTitles)
        {
            // Evaluated once the menu exists, then after every committed Favorite award.
            context.Effect(context.Events.OnAsync<LoginMenuInstalled>((installed, ct) =>
                installed.Protocol == options.Protocol ? runtime.EvaluateFavoriteAsync(installed.Session, ct) : ValueTask.CompletedTask));
            context.Effect(context.Events.OnAsync<FavoriteProgressCommitted>((committed, ct) =>
                committed.Protocol == options.Protocol ? runtime.EvaluateFavoriteAsync(committed.Session, ct) : ValueTask.CompletedTask));
        }
        // kr-812 sub_885F40 and kr-1256 sub_EAF420 resolve the selected title from the worn inventory.
        var titleItems = Array.AsReadOnly(catalog.Titles.Values.Select(title => title.GrantTerm.ItemId).Distinct().Order().ToArray());
        var titleGroup = new InventoryWearGroup(titleItems[0], titleItems[^1], titleItems[0]) { ItemIds = titleItems };
        context.Effect(context.Events.Intercept<EquipmentWearGroupsRequested, IReadOnlyList<InventoryWearGroup>>(async (request, next, ct) =>
        {
            var groups = await next(ct).ConfigureAwait(false);
            if (request.Protocol != options.Protocol) return groups;
            if (groups is null || groups.Count >= InventoryLimits.MaxWearGroups)
                throw new ConfigException("The title position exceeds the equipment group limit.");
            return Array.AsReadOnly(groups.Append(titleGroup).ToArray());
        }));
        return ValueTask.CompletedTask;
    }

    private static void RequireBinding<T>(IPluginContext context, string protocol) where T : IMessage
    {
        if (context.Services.GetAll<IInboundMessageBinding<T>>().Count(binding => binding.Protocol == protocol) != 1)
            throw new ConfigException($"Titles require exactly one audited {typeof(T).Name} binding for '{protocol}'.");
    }

    private static IMessageSender<T> Sender<T>(IPluginContext context, string protocol) where T : IMessage
    {
        var senders = context.Services.GetAll<IMessageSender<T>>().Where(sender => sender.Protocol == protocol).Take(2).ToArray();
        return senders.Length == 1 ? senders[0]
            : throw new ConfigException($"Titles require exactly one {typeof(T).Name} sender for '{protocol}'.");
    }
}
