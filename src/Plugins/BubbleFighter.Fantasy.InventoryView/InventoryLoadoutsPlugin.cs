using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Inventory;
using Microsoft.Extensions.Logging;

namespace BubbleFighter.Fantasy.InventoryView;

/// <summary>Mounts a build's inventory and preset projection over the shared account store.</summary>
[Plugin("inventory.loadouts", Description = "Independent client-build equipment and weapon presets over shared ownership.",
    Requires = [typeof(IInventoryLoadoutStore), typeof(IClientDataStore)],
    Provides = [typeof(IInventoryRepository), typeof(IPlayerInventoryState)])]
public sealed class InventoryLoadoutsPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<InventoryLoadoutsOptions>();
        var data = context.Services.Get<IClientDataStore>();
        var scope = LoadScope(options, data);
        var inventory = new BuildInventory(context.Services.Get<IInventoryLoadoutStore>(), scope);
        context.Effect(context.Services.Register<IInventoryRepository>(inventory));
        context.Effect(context.Services.Register<IPlayerInventoryState>(inventory));
        context.Logger.LogInformation("Build loadout {BuildKey} uses {Items} client catalog identities with shared item ownership.",
            scope.Key, scope.SupportedItemIds.Count);
        return ValueTask.CompletedTask;
    }

    internal static InventoryLoadoutScope LoadScope(InventoryLoadoutsOptions options, IClientDataStore data)
    {
        IReadOnlyList<IReadOnlyList<string>> Sheet(string path, string name) => SpreadsheetWorkbook.Load(
            WorkbookFile.FromBytes(path, data.Read(path, checked((int)options.MaxWorkbookBytes))), options.MaxWorkbookBytes,
            "max_workbook_bytes", [name])[name];
        var items = Sheet(options.ShopItemClientPath, "ShopItem");
        if (items.Count is < 2 || items.Count > options.MaxCatalogItems + 1)
            throw new ConfigException("The build loadout ShopItem sheet is empty or exceeds max_catalog_items.");
        var itemColumn = items[0].Select((value, index) => (value, index)).Where(cell => cell.value.Trim() == "itemId").ToArray();
        if (itemColumn.Length != 1) throw new ConfigException("The build loadout ShopItem sheet requires one itemId column.");
        var supported = new HashSet<int>();
        foreach (var row in items.Skip(1))
        {
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            if (itemColumn[0].index >= row.Count || !int.TryParse(row[itemColumn[0].index], out var id) || id <= 0 || !supported.Add(id))
                throw new ConfigException("The build loadout ShopItem sheet has an invalid or duplicate itemId.");
        }
        var fighters = Sheet(options.FighterConfigClientPath, "FighterConfig");
        var fields = fighters.SingleOrDefault(row => row.Count > 0 && row[0].Trim() == "[Field]")
            ?? throw new ConfigException("The build loadout FighterConfig sheet lacks [Field].");
        var names = fighters.SingleOrDefault(row => row.Count > 0 && row[0].Trim() == "name")
            ?? throw new ConfigException("The build loadout FighterConfig sheet lacks name.");
        var characters = new HashSet<int>();
        // kr-1256 sub_EF12E0 builds preview items as 300000 + character type; FighterConfig supplies the fields.
        // A shop row alone cannot establish that this client has the corresponding character definition.
        for (var column = 2; column < fields.Count; column++)
            if (int.TryParse(fields[column], out var field) && field is >= 1 and <= 31
                && column < names.Count && !string.IsNullOrWhiteSpace(names[column])) characters.Add(300000 + field);
        if (characters.Count == 0) throw new ConfigException("The build loadout FighterConfig sheet defines no characters.");
        supported.RemoveWhere(id => id is >= 300001 and <= 300031 && !characters.Contains(id));
        return new(options.BuildKey, supported.Order().ToArray());
    }
}

/// <summary>Client data and identity limits for one build's independent loadout row.</summary>
public sealed class InventoryLoadoutsOptions : IValidatableOptions
{
    /// <summary>Gets or sets the stable build key shared by this build's deployment nodes.</summary>
    [Required]
    public string BuildKey { get; set; } = "";

    /// <summary>Gets or sets the native workbook identifying items this client can display.</summary>
    [Required]
    public string ShopItemClientPath { get; set; } = "Config/ShopItem2.xml";

    /// <summary>Gets or sets the native workbook identifying character models in this client.</summary>
    [Required]
    public string FighterConfigClientPath { get; set; } = "Config/FighterConfig.xml";

    /// <summary>Gets or sets the maximum parsed workbook byte length.</summary>
    [Range(1024L, 134217728L)]
    public long MaxWorkbookBytes { get; set; } = 134217728;

    /// <summary>Gets or sets the maximum number of item rows in this build's catalog.</summary>
    [Range(1, 65536)]
    public int MaxCatalogItems { get; set; } = 65536;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate()
    {
        string? error = null;
        try { InventoryLoadoutScope.ValidateKey(BuildKey); }
        catch (ArgumentException exception) { error = exception.Message; }
        if (error is not null) yield return new(error, [nameof(BuildKey)]);
    }
}
