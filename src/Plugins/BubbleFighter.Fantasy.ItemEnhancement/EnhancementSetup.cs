using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using BubbleFighter.Fantasy.ItemEnhancement.Data;

namespace BubbleFighter.Fantasy.ItemEnhancement;

internal static class EnhancementSetup
{
    internal static EnhancementCatalog Catalog(IPluginContext context, EnhancementArticleOptions options, InventoryLoadoutScope scope,
        bool includeSets = false)
    {
        var catalog = EnhancementCatalog.Load(context.Services.Get<IClientDataStore>(), options.CatalogPath,
            options.MaxWorkbookBytes, options.MaxCatalogRows, options.MaxCatalogColumns, options.MaxCellCharacters, includeSets);
        if (scope.Key != options.BuildKey || scope.SupportedItemIds.Any(id => !catalog.Items.ContainsKey(id)))
            throw new ConfigException("Enhancement and inventory rows must use the same canonical catalog.");
        catalog.RequirePresenceAttribute(options.WeaponAttributeId, options.WeaponBuffId, ItemEnhancementKind.Weapon);
        catalog.RequirePresenceAttribute(options.ClothingAttributeId, options.ClothingBuffId, ItemEnhancementKind.Clothing);
        return catalog;
    }

    internal static void RequireRecipe(EnhancementCatalog catalog, ItemEnhancementOptions options, InventoryLoadoutScope scope)
    {
        foreach (var material in options.KitItemIds.Append(options.CardItemId).Append(options.AdditiveItemId))
            if (!scope.Supports(material) || !catalog.Items.TryGetValue(material, out var item) || item.DurationType != "count")
                throw new ConfigException("Every enhancement material must be a count-based item in the canonical catalog.");
    }
}
