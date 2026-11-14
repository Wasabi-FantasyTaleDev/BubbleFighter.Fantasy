using System.ComponentModel.DataAnnotations;
using BubbleFighter.Fantasy.Abstractions;
using BubbleFighter.Fantasy.Contracts.ClientData;
using BubbleFighter.Fantasy.Contracts.Pets;
using BubbleFighter.Fantasy.Pets.Data;

namespace BubbleFighter.Fantasy.Pets.Combat;

/// <summary>Publishes a bounded native pet-effect resolver independently of pet mutation and match services.</summary>
[Plugin("pets.combat-effects", Category = "items", Names = ["en=Pet combat effects", "zh-Hans=宠物战斗效果", "zh-Hant=寵物戰鬥效果", "ko=펫 전투 효과"], Description = "Native pet combat attribute conversion.",
    Optional = [typeof(IClientDataStore)], ConditionalProvidesMany = [typeof(IPetCombatEffects)])]
public sealed class PetCombatEffectsPlugin : IPlugin
{
    /// <inheritdoc />
    public ValueTask ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var options = context.Config.Bind<PetCombatEffectsOptions>();
        if (!options.Enabled) return ValueTask.CompletedTask;
        var identity = context.Row.ClientBuild
            ?? throw new ConfigException("pets.combat-effects requires an explicit client_build when enabled.");
        var build = $"{identity.Region}-{identity.Major}";
        if (!string.Equals(options.Protocol, build + "/game", StringComparison.Ordinal))
            throw new ConfigException("Pet combat effects require the owning client build's game protocol.");
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = PetCatalogLoader.Load(context.Services.Get<IClientDataStore>(), options.CatalogPath,
            options.MaxWorkbookBytes, options.MaxCatalogRows, options.MaxCatalogColumns, options.MaxCellCharacters);
        context.Effect(context.Services.RegisterMany<IPetCombatEffects>(
            new PetCombatEffects(options.Protocol, catalog, options.SinglePrecisionInput)));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Controls opt-in publication and resource bounds of one build's pet combat resolver.</summary>
public sealed class PetCombatEffectsOptions
{
    /// <summary>Gets or sets whether this row loads native data and publishes its resolver.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the client build's game protocol, matched to the row's client_build.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string Protocol { get; set; } = "kr-812/game";

    /// <summary>Gets or sets whether the client converts the signed payload to float before multiplication.</summary>
    public bool SinglePrecisionInput { get; set; } = true;

    /// <summary>Gets or sets the native item workbook containing pet identities, attributes and conversion tables.</summary>
    [Required, StringLength(260, MinimumLength = 1)]
    public string CatalogPath { get; set; } = "Config/ShopItem2.xml";

    /// <summary>Gets or sets the maximum native workbook byte size.</summary>
    [Range(1024, 134217728)]
    public int MaxWorkbookBytes { get; set; } = 134217728;

    /// <summary>Gets or sets the total maximum row count across parsed native worksheets.</summary>
    [Range(1, 65536)]
    public int MaxCatalogRows { get; set; } = 65536;

    /// <summary>Gets or sets the maximum column count in each parsed native worksheet.</summary>
    [Range(4, 1024)]
    public int MaxCatalogColumns { get; set; } = 256;

    /// <summary>Gets or sets the maximum characters retained from one native worksheet cell.</summary>
    [Range(1, 65536)]
    public int MaxCellCharacters { get; set; } = 16384;
}
