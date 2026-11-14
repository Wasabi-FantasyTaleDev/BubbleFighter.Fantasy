using System.Globalization;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Enhancement;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.ItemEnhancement.Data;

namespace BubbleFighter.Fantasy.ItemEnhancement;

/// <summary>Prepares native equipment Articles and publishes earned modifiers from committed inventory snapshots.</summary>
[Plugin("item-enhancement.articles", Category = "items", Names = ["en=Enhancement items", "zh-Hans=强化物品", "zh-Hant=強化物品", "ko=강화 물품"], Description = "Durable equipment Articles and native enhancement presentation.",
    Optional = [typeof(IInventoryMetadataStore), typeof(IClientDataStore), typeof(IItemEnhancementStore), typeof(IInventoryBuildScope)],
    ConditionalProvides = [typeof(IEquipmentArticleResolver)], ConditionalProvidesMany = [typeof(IFavoriteWeaponProgress)])]
public sealed class EnhancementArticlesPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<EnhancementArticleOptions>();
        if (!options.Enabled) return ValueTask.CompletedTask;
        var scope = context.Services.Get<IInventoryBuildScope>().Scope;
        var catalog = EnhancementSetup.Catalog(context, options, scope);
        var favorite = options.Favorite.Enabled
            ? FavoriteWeaponCatalog.Load(context.Services.Get<IClientDataStore>(), options.Favorite.WorkbookPath, options.MaxWorkbookBytes) : null;
        var articles = new EnhancementArticles(catalog, context.Services.Get<IItemEnhancementStore>(), scope,
            TimeProvider.System, options.InitializeArticles);
        context.Effect(context.Services.Register<IEquipmentArticleResolver>(articles));
        if (favorite is not null)
            context.Effect(context.Services.RegisterMany<IFavoriteWeaponProgress>(new FavoriteWeaponProgress(options.Protocol, favorite,
                catalog, context.Services.Get<IItemEnhancementStore>(), scope)));
        var metadataStore = options.InitializeArticles ? context.Services.Get<IInventoryMetadataStore>() : null;
        if (options.InitializeArticles)
            context.Effect(context.Events.Intercept<InventoryMetadataInputsRequested, InventoryMetadataPreparationCommand>(async (request, next, ct) =>
            {
                var command = await next(ct).ConfigureAwait(false);
                if (request.Scope.Key != scope.Key) return command;
                if (command.ArticleInitialization is not null) throw new ConfigException("Inventory metadata has duplicate article catalogue providers.");
                return command with { ArticleInitialization = articles.CreateInitialization(request.UserNum, request.Now) };
            }));
        async ValueTask<InventorySnapshot> PrepareAsync(long userNum, CancellationToken ct)
        {
            if (metadataStore is null) return await articles.PrepareAsync(userNum, ct).ConfigureAwait(false);
            var command = await context.Events.WaterfallAsync<InventoryMetadataInputsRequested, InventoryMetadataPreparationCommand>(
                new(userNum, scope, DateTimeOffset.UtcNow), _ => ValueTask.FromResult(new InventoryMetadataPreparationCommand(userNum, scope)), ct)
                .ConfigureAwait(false);
            return await metadataStore.PrepareInventorySnapshotAsync(command, ct).ConfigureAwait(false);
        }
        context.Effect(context.Events.Intercept<InventoryMetadataPreparing, PetInventorySnapshot?>(async (request, next, ct) =>
        {
            var previous = await next(ct).ConfigureAwait(false);
            if (request.BuildKey != scope.Key || previous is not null) return previous;
            var snapshot = await PrepareAsync(request.UserNum, ct).ConfigureAwait(false);
            if (!options.InitializeArticles && !HasEquipmentArticles(snapshot.CharacterBag)) return null;
            return new(snapshot.CharacterBag, snapshot.EquippedBag);
        }));
        context.Effect(context.Events.Intercept<InventoryPublicationPreparing, InventoryReplaced?>(async (request, next, ct) =>
        {
            var previous = await next(ct).ConfigureAwait(false);
            if (request.Protocol != options.Protocol) return previous;
            if (request.IsComplete)
            {
                foreach (var row in request.CharacterBag.Concat(request.EquippedBag))
                {
                    _ = InventoryArticleIdentityResolver.Of(row);
                    if (!scope.Supports(row.ItemId) || row.UserNum != request.UserNum)
                        throw new InvalidDataException("Complete enhancement inventory crosses the canonical catalog or owner.");
                    if (options.InitializeArticles && catalog.Items.TryGetValue(row.ItemId, out var item) && item.RequiresArticle
                        && row.Quantity > 0 && row.Lifetime.Kind == InventoryLifetimeKind.Permanent
                        && InventoryArticleIdentityResolver.Of(row) is null)
                        throw new InvalidDataException("A complete inventory snapshot lacks its equipment Article identity.");
                }
                return new(request.CharacterBag, request.EquippedBag);
            }
            if (previous is not null) return previous;
            var snapshot = await PrepareAsync(request.UserNum, ct).ConfigureAwait(false);
            if (!options.InitializeArticles && !HasEquipmentArticles(snapshot.CharacterBag)
                && !HasEquipmentArticles(request.CharacterBag.Concat(request.EquippedBag).Concat(request.Changes ?? [])))
                return null;
            return new(snapshot.CharacterBag, snapshot.EquippedBag);
        }));
        context.Effect(context.Events.Intercept<InventoryPresentationRequested, InventoryItemPresentation>(async (request, next, ct) =>
        {
            var previous = await next(ct).ConfigureAwait(false);
            if (request.Protocol != options.Protocol) return previous;
            var state = request.Slot.Enhancements?.Find(scope.Key);
            var kind = state?.Kind ?? ItemEnhancementKind.None;
            var advancement = state?.Advancement ?? 0;
            var modifications = request.Slot.Modifications?.Find(scope.Key);
            var favoritePoints = favorite is null ? null : request.Slot.Favorites?.Find(scope.Key);
            if (kind == ItemEnhancementKind.None && advancement == 0 && modifications is null && favoritePoints is null) return previous;
            if (!catalog.Items.TryGetValue(request.Slot.ItemId, out var item)
                || InventoryArticleIdentityResolver.Of(request.Slot) is null)
                throw new InvalidDataException("Stored enhancement has no matching native Article definition.");
            var attributes = previous.Attributes.ToList();
            if (kind != ItemEnhancementKind.None)
            {
                if (!item.IsEnchant || item.Kind != kind) throw new InvalidDataException("Stored enhancement kind does not match its native item.");
                var selector = checked((uint)(kind == ItemEnhancementKind.Weapon ? options.WeaponAttributeId : options.ClothingAttributeId));
                if (attributes.Any(attribute => attribute.Kind == selector))
                    throw new InvalidDataException("Enhancement presentation repeats its native selector.");
                // kr-1256 sub_A0DED0 requires a value; kr-1256 sub_A04850 applies these presence effects independently of that zero.
                attributes.Add(new(request.Slot.ItemId, selector, new uint[] { 0 }, 0, ""));
            }
            if (advancement != 0)
            {
                if (item.Kind != ItemEnhancementKind.Clothing) throw new InvalidDataException("Stored grade-up has no native clothing item.");
                if (attributes.Any(attribute => attribute.Kind == unchecked((uint)advancement)))
                    throw new InvalidDataException("Grade-up presentation repeats its native selector.");
                // kr-1256 sub_A0DED0 reads one word per buff of the attribute and multiplies it by the buff's conversion.
                attributes.Add(new(request.Slot.ItemId, checked((uint)advancement),
                    catalog.AdvancementValues(advancement).Select(value => (uint)value).ToArray(), 0, ""));
            }
            if (modifications is not null)
            {
                modifications.Validate();
                if (!item.IsModificationWeapon) throw new InvalidDataException("Stored modification has no eligible native weapon.");
                foreach (var attribute in modifications.Attributes)
                {
                    catalog.RequireModificationAttribute(attribute.Id);
                    if (attributes.Any(existing => existing.Kind == unchecked((uint)attribute.Id) && existing.Unknown28 == unchecked((uint)attribute.Group)))
                        throw new InvalidDataException("Modification presentation repeats its native selector and group.");
                    // kr-1256 sub_DFA540 matches selector and numeric tag; kr-1256 sub_DFA900 reads the two values as signed words.
                    attributes.Add(new(request.Slot.ItemId, checked((uint)attribute.Id),
                        new[] { unchecked((uint)attribute.Value0), unchecked((uint)attribute.Value1) }, checked((uint)attribute.Group),
                        attribute.NumericTag.ToString(CultureInfo.InvariantCulture)));
                }
            }
            if (favoritePoints is { } points)
            {
                if (item.Kind != ItemEnhancementKind.Weapon) throw new InvalidDataException("Stored Favorite points have no native weapon.");
                if (attributes.Any(attribute => attribute.Kind == FavoriteWeaponCatalog.AttributeSelector))
                    throw new InvalidDataException("Favorite presentation repeats its native selector.");
                // kr-1256 sub_5EE200 and sub_E03FE0 read value 0 as the grade and value 1 as the points; both ignore the other fields.
                attributes.Add(new(request.Slot.ItemId, FavoriteWeaponCatalog.AttributeSelector,
                    new uint[] { (uint)favorite!.GradeOf(points), checked((uint)points) }, 0, ""));
            }
            return new(attributes, previous.RemainingUntil);
        }));
        return ValueTask.CompletedTask;
    }

    private static bool HasEquipmentArticles(IEnumerable<InventorySlotRecord> rows) =>
        rows.Any(row => row.PetIdentity is null && InventoryArticleIdentityResolver.Of(row) is not null);
}
